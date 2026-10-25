using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.NodePlay;

public enum NPUpdateMode
{
    /// <summary>Tick in the component Update channel.</summary>
    NormalUpdate,
    LateUpdate,
    FixedUpdate,
    /// <summary>Only ticks when UpdateBehaviour is called explicitly (orchestration/dialogue).</summary>
    Manual,
}

/// <summary>What to do with the graph on component enable/disable.</summary>
public enum NPRunAction
{
    Start,
    Pause,
    DoNothing,
}

/// <summary>One prefab-override preset value applied into the graph's LocalBlackboard at start.</summary>
public class NPParameterOverride
{
    public string VariableName = "";
    public object Value = "";
}

/// <summary>
/// The owner that binds a NodePlay graph to a scene object and drives it per frame
/// (the counterpart of GraphOwner). Graph source precedence: an editor/test override instance,
/// then the <see cref="Graph"/> asset reference.
/// </summary>
/// <remarks>Per-frame path is allocation-free: no LINQ, closures, params, or string building in Step.</remarks>
public class NPRunner : MonoBehaviour
{
    public AssetRef<NPGraphAsset> Graph = default;

    public NPUpdateMode UpdateMode = NPUpdateMode.NormalUpdate;
    public NPRunAction OnEnableAction = NPRunAction.Start;
    public NPRunAction OnDisableAction = NPRunAction.Pause;

    /// <summary>Prefab-overridable variable presets pushed into the graph scope after start.</summary>
    public List<NPParameterOverride> ParameterOverrides = new();

    public NPGraphAsset? RunningGraph => _running;
    public bool IsRunning => _running != null && !_paused;
    public bool IsPaused => _paused;

    /// <summary>Live scope of the running graph (its persisted LocalBlackboard instance).</summary>
    public INPBlackboard? Blackboard => _running?.GraphBlackboard;

    private NPGraphAsset? _overrideGraph;
    private NPGraphAsset? _running;
    private readonly List<PendingEvent> _pendingEvents = new();
    private bool _paused;

    private struct PendingEvent
    {
        public string Name;
        public object? Value;
        public PendingEvent(string name, object? value) { Name = name; Value = value; }
    }

    private static readonly List<NPRunner> Owners = new();

    /// <summary>Every owner currently bound to a graph (live-editor overlays read this).</summary>
    public static IReadOnlyList<NPRunner> RunningOwners => Owners;

    /// <summary>Editor/headless injection point taking precedence over the serialized asset ref.</summary>
    public void SetOverrideGraph(NPGraphAsset? graph)
    {
        if (_running != null)
            StopBehaviour();
        _overrideGraph = graph;
    }

    // ---- engine lifecycle -----------------------------------------------------------------

    public override void Start()
    {
        base.Start();
        if (OnEnableAction == NPRunAction.Start && !IsRunning)
            StartBehaviour();
    }

    public override void OnEnable()
    {
        base.OnEnable();
        if (OnEnableAction == NPRunAction.Start)
            StartBehaviour();
    }

    public override void OnDisable()
    {
        if (OnDisableAction == NPRunAction.Pause)
            PauseBehaviour();
        else if (OnDisableAction == NPRunAction.Start)
            StopBehaviour();
        base.OnDisable();
    }

    public override void Update() => TickChannel(NPUpdateMode.NormalUpdate);

    public override void LateUpdate() => TickChannel(NPUpdateMode.LateUpdate);

    public override void FixedUpdate() => TickChannel(NPUpdateMode.FixedUpdate);

    public override void OnRemovedFromScene()
    {
        StopBehaviour();
        base.OnRemovedFromScene();
    }

    private void TickChannel(NPUpdateMode mode)
    {
        if (!IsRunning || mode != UpdateMode)
            return;
        Step(Time.DeltaTime);
    }

    /// <summary>Manual-mode drivers call this with their own delta.</summary>
    public void UpdateBehaviour(float delta)
    {
        if (IsRunning)
            Step(delta);
    }

    // ---- graph control -------------------------------------------------------------------

    public bool StartBehaviour()
    {
        if (IsRunning)
            return true;

        NPGraphAsset? instance = ResolveInstance();
        if (instance == null)
            return false;

        _running = instance;
        Wire(instance);
        ApplyOverrides(instance);
        Owners.Add(this);
        _paused = false;
        instance.OnStarted(this);
        NPGraphConsole.Log(instance, "info", $"started '{instance.GraphName}' on '{Name}'");
        FlushEvents(instance);
        return true;
    }

    private void ApplyOverrides(NPGraphAsset instance)
    {
        var bb = instance.GraphBlackboard;
        for (int i = 0; i < ParameterOverrides.Count; i++)
        {
            NPParameterOverride o = ParameterOverrides[i];
            if (string.IsNullOrEmpty(o.VariableName))
                continue;
            if (bb.Get(o.VariableName) == null)
            {
                XEngine.Runtime.Debug.LogWarning(
                    $"[NodePlay] override '{o.VariableName}' has no matching variable in '{instance.GraphName}'.");
                continue;
            }

            bb.SetValue(o.VariableName, o.Value);
        }
    }

    public void PauseBehaviour()
    {
        if (_running == null || _paused)
            return;
        _paused = true;
        _running.OnPaused(this, true);
    }

    public void ResumeBehaviour()
    {
        if (_running == null || !_paused)
            return;
        _paused = false;
        _running.OnPaused(this, false);
    }

    public void StopBehaviour()
    {
        if (_running == null)
            return;

        NPGraphAsset stopping = _running;
        _running = null;
        _paused = false;
        for (int i = 0; i < Owners.Count; i++)
        {
            if (ReferenceEquals(Owners[i], this))
            {
                Owners.RemoveAt(i);
                break;
            }
        }

        NPGraphConsole.Log(stopping, "info", $"stopped '{stopping.GraphName}' on '{Name}'");
        stopping.OnStopped(this, success: true);
        stopping.ReleaseNestedInstances(this);
        stopping.OwnerRunner = null!;
        if (!ReferenceEquals(stopping, _overrideGraph))
            stopping.Dispose();
    }

    /// <summary>Queue a named event consumed before the next step (also observable via NPEvents).</summary>
    public void SendEvent(string name, object? value = null)
    {
        _pendingEvents.Add(new PendingEvent(name, value));
        NPEvents.Notify(this, name, value);
    }

    internal static void Broadcast(string name, object? value)
    {
        for (int i = Owners.Count - 1; i >= 0; i--)
            Owners[i].SendEvent(name, value);
    }

    // ---- internals -----------------------------------------------------------------------

    private NPGraphAsset? ResolveInstance()
    {
        if (_running != null)
            return _running;
        if (_overrideGraph != null)
        {
            NPGraphAsset clone = _overrideGraph.CloneAsInstance();
            clone.BuildTopology();
            return clone;
        }

        NPGraphAsset? referenced = Graph.Res;
        if (referenced == null)
            Graph.EnsureLoaded();
        return Graph.Res;
    }

    private void Wire(NPGraphAsset graph)
    {
        graph.OwnerRunner = this;
        graph.LocalBlackboard ??= new NPBlackboardSource();
        graph.GraphBlackboard = graph.LocalBlackboard;
        graph.GraphBlackboard.Identifier = Name;
    }

    private void Step(float delta)
    {
        NPGraphAsset graph = _running!;
        FlushEvents(graph);
        graph.LastDelta = delta;
        graph.ElapsedTime += delta;
        graph.TickRunner(delta);
    }

    private void FlushEvents(NPGraphAsset graph)
    {
        for (int i = 0; i < _pendingEvents.Count; i++)
            graph.HandleEvent(_pendingEvents[i].Name, _pendingEvents[i].Value);
        _pendingEvents.Clear();
    }
}
