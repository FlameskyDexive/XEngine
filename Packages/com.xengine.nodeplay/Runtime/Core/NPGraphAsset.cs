using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.NodePlay;

/// <summary>
/// Base asset for all NodePlay graphs, persisted as Echo text (`.nodeplay`) with flat,
/// AI-diffable fields — inspect/patch it directly with xengine-cli. One asset class per module
/// derives from this type so the root `$type` names the module and module-specific persisted
/// state lives on the subclass (instead of NodeCanvas's opaque derived-data bag).
/// </summary>
/// <remarks>
/// Persistence contract:
/// - <see cref="Nodes"/> entries serialize polymorphically (a `$type` envelope per node).
/// - <see cref="Connections"/> reference nodes by stable <see cref="NPNode.Id"/>.
/// - <see cref="PrimeNodeId"/> names the entry node (empty = first node), replacing NodeCanvas's
///   "prime is index 0" shuffle which would churn the text file on re-root.
/// Runtime adjacency (<c>InConnections/OutConnections</c>) is rebuilt via <see cref="BuildTopology"/>
/// after load or any structural edit.
/// </remarks>
public abstract class NPGraphAsset : ScriptableObject
{
    public const string FileExtension = ".nodeplay";

    public string GraphName = "";
    /// <summary>Module tag ("bt" / "fsm" / "dialogue"); informational next to the typed subclasses.</summary>
    public string Module = "";
    public List<NPNode> Nodes = new();
    public List<NPConnection> Connections = new();
    public List<NPGroup> Groups = new();
    public List<NPSticky> Stickies = new();

    public string PrimeNodeId = "";

    /// <summary>Bumped on every structural edit so editors can re-mirror cheaply (not persisted).</summary>
    [SerializeIgnore]
    public int StructureVersion;

    /// <summary>Graph-local variable scope persisted with the asset (three-layer chain root).</summary>
    public NPBlackboardSource LocalBlackboard = new();

    // ---- runtime session state (never persisted; wired by NPRunner at start) ----

    public NPRunner OwnerRunner = null!;
    public NPBlackboardSource GraphBlackboard = null!;
    public float ElapsedTime;

    /// <summary>Delta of the most recent TickRunner call (0 before the first tick).</summary>
    [SerializeIgnore] public float LastDelta;

    /// <summary>Convenience for module nodes reading the running delta.</summary>
    public float ElapsedDeltaSafe() => LastDelta;

    /// <summary>Runner entered the graph.</summary>
    protected internal virtual void OnStarted(NPRunner runner)
    {
    }

    /// <summary>Runner paused/resumed the graph.</summary>
    protected internal virtual void OnPaused(NPRunner runner, bool paused)
    {
    }

    /// <summary>Runner stopped the graph (success=false on abort).</summary>
    protected internal virtual void OnStopped(NPRunner runner, bool success)
    {
    }

    /// <summary>One graph tick with the runner's delta — module subclasses implement semantics.</summary>
    protected internal virtual void TickRunner(float delta)
    {
    }

    /// <summary>A queued named event delivered just before the next tick.</summary>
    protected internal virtual void HandleEvent(string name, object? value)
    {
    }

    /// <summary>Clone as <see cref="NPGraphAsset"/>-typed instance for a per-agent run.</summary>
    public NPGraphAsset CloneAsInstance()
        => Serializer.Deserialize(Serializer.Serialize(this), GetType()) as NPGraphAsset
           ?? throw new InvalidOperationException("graph instance clone deserialized to null");

    /// <summary>Runner stop path: every nested node drops instances cached for this owner key.</summary>
    public void ReleaseNestedInstances(object ownerKey)
    {
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (Nodes[i] is NPNestedNode nested)
                nested.Nested.ReleaseFor(ownerKey);
        }
    }

    public override void OnValidate()
    {
        Nodes ??= new List<NPNode>();
        Connections ??= new List<NPConnection>();
        Groups ??= new List<NPGroup>();
        Stickies ??= new List<NPSticky>();
        LocalBlackboard ??= new NPBlackboardSource();
        LocalBlackboard.OnValidate();

        // Backfill empty ids, break duplicate ids from bad merges, drop wires whose endpoints vanished.
        for (int i = 0; i < Nodes.Count; i++)
        {
            NPNode node = Nodes[i];
            if (node == null)
                continue;
            if (string.IsNullOrEmpty(node.Id))
            {
                node.Id = NPNode.NewId();
                continue;
            }

            for (int j = 0; j < i; j++)
            {
                if (Nodes[j] != null && Nodes[j].Id == node.Id)
                    node.Id = NPNode.NewId();
            }
        }

        for (int i = Connections.Count - 1; i >= 0; i--)
        {
            NPConnection c = Connections[i];
            if (c == null || GetNode(c.FromNodeId) == null || GetNode(c.ToNodeId) == null)
                Connections.RemoveAt(i);
        }
    }

    /// <summary>Find a node by id (scope is per graph; subgraph instances own their id space).</summary>
    public NPNode? GetNode(string id)
    {
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (Nodes[i].Id == id)
                return Nodes[i];
        }

        return null;
    }

    public T? GetNode<T>(string id) where T : NPNode => GetNode(id) as T;

    /// <summary>The prime (entry) node: explicit id, else the first node. Null on an empty graph.</summary>
    public NPNode? PrimeNode
    {
        get
        {
            if (!string.IsNullOrEmpty(PrimeNodeId))
            {
                NPNode? byId = GetNode(PrimeNodeId);
                if (byId != null)
                    return byId;
            }

            return Nodes.Count > 0 ? Nodes[0] : null;
        }
    }

    public void SetPrime(NPNode node)
    {
        PrimeNodeId = node.Id;
        OnValidate();
    }

    /// <summary>Append a node and wire its owning graph; the first node of a graph becomes prime.</summary>
    public T AddNode<T>(T node) where T : NPNode
    {
        node.Graph = this;
        Nodes.Add(node);
        StructureVersion++;
        if (Nodes.Count == 1)
            SetPrime(node);
        return node;
    }

    /// <summary>
    /// Create a wire honouring port limits and module validation. Returns null when rejected,
    /// with a UI-facing reason in <paramref name="error"/>.
    /// </summary>
    public NPConnection? Connect(NPNode from, NPNode to, out string? error)
    {
        error = ValidateConnection(from, to);
        if (error != null)
            return null;

        NPConnection connection = CreateConnection();
        connection.FromNodeId = from.Id;
        connection.ToNodeId = to.Id;
        Connections.Add(connection);
        StructureVersion++;
        LinkConnection(connection);
        return connection;
    }

    /// <summary>Module hook: the connection type this graph wires with (FSM adds payload).</summary>
    protected internal virtual NPConnection CreateConnection() => new NPConnection();

    protected virtual string? ValidateConnection(NPNode from, NPNode to)
    {
        if (ReferenceEquals(from, to))
            return "Cannot connect a node to itself.";
        if (from.OutConnections.Count >= from.MaxOutConnections)
            return $"{from.DisplayName} has no free output ports.";
        if (to.InConnections.Count >= to.MaxInConnections)
            return $"{to.DisplayName} has no free input ports.";
        return from.ValidateConnection(to);
    }

    /// <summary>Public preview used by editors for live rejection feedback before applying.</summary>
    public bool IsConnectionAllowed(NPNode from, NPNode to) => ValidateConnection(from, to) == null;

    public void Disconnect(NPConnection connection)
    {
        if (Connections.Remove(connection))
            StructureVersion++;
        connection.Source?.OutConnections.Remove(connection);
        connection.Target?.InConnections.Remove(connection);
    }

    public void RemoveNode(NPNode node)
    {
        for (int i = Connections.Count - 1; i >= 0; i--)
        {
            NPConnection c = Connections[i];
            if (c.FromNodeId == node.Id || c.ToNodeId == node.Id)
                Disconnect(c);
        }

        Nodes.Remove(node);
        StructureVersion++;
        node.Graph = null!;
        if (PrimeNodeId == node.Id)
            PrimeNodeId = "";
    }

    /// <summary>Rebuild In/Out adjacency and node back-refs from the flat id-based wire list.</summary>
    public void BuildTopology()
    {
        for (int i = 0; i < Nodes.Count; i++)
        {
            NPNode node = Nodes[i];
            node.Graph = this;
            node.InConnections.Clear();
            node.OutConnections.Clear();
        }

        for (int i = 0; i < Connections.Count; i++)
            LinkConnection(Connections[i]);
    }

    /// <summary>
    /// Deep copy via an Echo roundtrip — the same mechanism the runner uses to instantiate a graph
    /// per agent, so what tests prove here holds for live behaviour instances.
    /// </summary>
    public T CloneGraph<T>() where T : NPGraphAsset
    {
        EchoObject echo = Serializer.Serialize(this);
        var clone = Serializer.Deserialize(echo, GetType()) as T
                    ?? throw new InvalidOperationException("graph clone deserialized to the wrong type");
        clone.GraphName = GraphName;
        clone.BuildTopology();
        return clone;
    }

    private void LinkConnection(NPConnection connection)
    {
        NPNode? source = GetNode(connection.FromNodeId);
        NPNode? target = GetNode(connection.ToNodeId);
        if (source == null || target == null)
            return;

        connection.Source = source;
        connection.Target = target;
        source.OutConnections.Add(connection);
        target.InConnections.Add(connection);
    }
}
