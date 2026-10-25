using System;
using System.Collections.Generic;

using XEngine.Echo;

namespace XEngine.NodePlay.BT;

/// <summary>How a <see cref="NPParallel"/> decides overall success.</summary>
public enum NPParallelPolicy
{
    One,
    All,
}

/// <summary>How guards arbitrate branch ownership (mutual exclusion across a running tree).</summary>
public sealed class NPGuardRegistry
{
    private readonly Dictionary<string, object> _holders = new();

    public bool TryAcquire(string token, object holder)
    {
        if (_holders.TryGetValue(token, out object? current))
            return ReferenceEquals(current, holder);
        _holders[token] = holder;
        return true;
    }

    public void ReleaseAll(object holder)
    {
        List<string>? doomed = null;
        foreach (KeyValuePair<string, object> kv in _holders)
        {
            if (ReferenceEquals(kv.Value, holder))
                (doomed ??= new List<string>()).Add(kv.Key);
        }

        if (doomed == null)
            return;
        for (int i = 0; i < doomed.Count; i++)
            _holders.Remove(doomed[i]);
    }

    public void Clear() => _holders.Clear();
}

/// <summary>
/// Reactive behaviour tree runtime. Ticks the prime node each update (throttled by
/// <see cref="UpdateInterval"/>) and loops via <see cref="Repeat"/> after the root settles.
/// Nodes carry their own <see cref="NPStatus"/>; <see cref="LastRootStatus"/> exposes the settled
/// outcome between repeats. Breakpoint hits pause the runner and raise <see cref="OnBreakpointHit"/>.
/// </summary>
public partial class NPBehaviourTreeAsset : NPGraphAsset
{
    /// <summary>Raised when a node flagged <see cref="NPNode.Breakpoint"/> executes (editor M9 hooks in).</summary>
    public static event Action<NPBehaviourTreeAsset, NPNode>? OnBreakpointHit;

    [SerializeIgnore] public NPStatus LastRootStatus = NPStatus.Resting;
    [SerializeIgnore] private float _sinceLastTick;
    [SerializeIgnore] private float _lastDelta;
    [SerializeIgnore] internal readonly NPGuardRegistry Guards = new();

    public bool Repeat = true;
    public float UpdateInterval;

    public NPBehaviourTreeAsset() => Module = "bt";

    protected internal override void OnStarted(NPRunner runner)
    {
        base.OnStarted(runner);
        ResetTree();
        _sinceLastTick = float.MaxValue; // tick immediately on the first frame
        _lastDelta = 0f;
    }

    /// <summary>Delta of the tick currently being evaluated (0 outside a tick).</summary>
    internal float ElapsedDelta() => _lastDelta;

    protected internal override void OnStopped(NPRunner runner, bool success)
    {
        base.OnStopped(runner, success);
        Guards.Clear();
    }

    protected internal override void TickRunner(float delta)
    {
        LastDelta = delta;
        NPNode? root = PrimeNode;
        if (root == null)
            return;

        _sinceLastTick += delta;
        if (UpdateInterval > 0f && _sinceLastTick < UpdateInterval)
            return;
        _lastDelta = _sinceLastTick;
        _sinceLastTick = 0f;

        NPStatus status = ExecuteNode(root, OwnerRunner!);
        LastRootStatus = status;

        if (status != NPStatus.Running && Repeat)
            ResetTree(); // next tick restarts from the top
    }

    /// <summary>Recursive evaluation — the whole tree ticks through this one entry point.</summary>
    internal NPStatus ExecuteNode(NPNode node, NPRunner runner)
    {
        if (node.Disabled)
            return SkipDisabled(node, runner);

        if (node.Breakpoint)
            OnBreakpointHit?.Invoke(this, node);

        NPStatus result = node switch
        {
            BTComposite composite => composite.Execute(this, runner),
            BTDecorator decorator => decorator.Execute(this, runner),
            BTLeaf leaf => leaf.Execute(this, runner),
            _ => node.OutConnections.Count > 0
                ? ExecuteNode(node.OutConnections[0].Target, runner)
                : NPStatus.Success,
        };

        node.Status = result;
        return result;
    }

    private NPStatus SkipDisabled(NPNode node, NPRunner runner)
    {
        // Disabled nodes act as pass-through: surface their first enabled child instead.
        for (int i = 0; i < node.OutConnections.Count; i++)
        {
            NPConnection wire = node.OutConnections[i];
            if (!wire.Disabled && !wire.Target.Disabled)
                return ExecuteNode(wire.Target, runner);
        }

        return NPStatus.Failure;
    }

    /// <summary>Abort every running branch below <paramref name="node"/> and clear statuses.</summary>
    internal void ResetBranch(NPNode node)
    {
        // The branch ROOT is part of the branch: abort/reset it too (guards, timers, cursors).
        if (node is BTNode self)
        {
            if (self.Status == NPStatus.Running)
                self.OnAbort(this);
            self.Status = NPStatus.Resting;
        }

        for (int i = 0; i < node.OutConnections.Count; i++)
        {
            if (node.OutConnections[i].Target is BTNode child)
            {
                if (child.Status == NPStatus.Running)
                    child.OnAbort(this);
                child.Status = NPStatus.Resting;
                ResetBranch(child);
            }
        }
    }

    private void ResetTree()
    {
        // LastRootStatus deliberately survives: it is the settled outcome between repeats.
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (Nodes[i] is BTNode bt)
            {
                if (bt.Status == NPStatus.Running)
                    bt.OnAbort(this); // only interrupted work needs the abort signal
                bt.Status = NPStatus.Resting;
            }
        }

        Guards.Clear();
    }
}
