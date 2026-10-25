using System;

using XEngine.Echo;

namespace XEngine.NodePlay.BT;

/// <summary>
/// Tick children in order; first Success wins (Failure continues). <see cref="Dynamic"/> is the
/// reactive core: every tick restarts the scan from child 0, aborting any still-Running
/// higher-priority branch the moment a lower-priority child succeeds/fails first.
/// <see cref="Random"/> shuffles the order per pass.
/// </summary>
[NPName("Selector", 90)]
[NPCategory("Composites")]
[NPDescription("Reactive pick: first child to succeed wins; Dynamic restarts the scan and aborts stale running branches.")]
public class BTSelector : BTComposite
{
    public bool Dynamic;
    public bool Random;

    private int _currentIndex;
    private int[] _order = Array.Empty<int>();

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        int count = EnabledChildCount();
        if (count == 0)
            return NPStatus.Failure;

        EnsureOrder(count);
        if (Dynamic)
        {
            if (_currentIndex != 0)
            {
                graph.ResetBranch(this);
                _currentIndex = 0;
            }
        }

        while (_currentIndex < _order.Length)
        {
            NPNode? child = EnabledChildAt(_order[_currentIndex]);
            if (child == null)
            {
                _currentIndex++;
                continue;
            }

            NPStatus status = graph.ExecuteNode(child, runner);
            switch (status)
            {
                case NPStatus.Running:
                    return NPStatus.Running;
                case NPStatus.Success:
                case NPStatus.Optional:
                    _currentIndex = 0;
                    return status;
                default:
                    _currentIndex++;
                    break;
            }
        }

        _currentIndex = 0;
        return NPStatus.Failure;
    }

    internal override void OnAbort(NPBehaviourTreeAsset graph) => _currentIndex = 0;

    private void EnsureOrder(int count)
    {
        if (_order.Length != count)
            _order = new int[count];
        for (int i = 0; i < count; i++)
            _order[i] = i;

        if (!Random)
            return;

        // Deterministic-enough Fisher-Yates driven by frame count (editor-seeded ok for AI V1).
        Random rng = new(FrameSeed());
        for (int i = count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_order[i], _order[j]) = (_order[j], _order[i]);
        }
    }

    private static int FrameSeed() => (int)(XEngine.Runtime.Time.FrameCount & 0x7fffffff);
}

/// <summary>
/// Tick children in order; first Failure fails (Success continues). <see cref="Dynamic"/>
/// restarts the scan from child 0 every tick with running-branch abort — the other half of
/// reactive BT evaluation.
/// </summary>
[NPName("Sequence", 89)]
[NPCategory("Composites")]
[NPDescription("Ordered AND: stops at first failure; Dynamic re-checks from the first child every tick.")]
public class BTSequencer : BTComposite
{
    public bool Dynamic;
    public bool Random;

    private int _currentIndex;
    private int[] _order = Array.Empty<int>();

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        int count = EnabledChildCount();
        if (count == 0)
            return NPStatus.Success;

        EnsureOrder(count);
        if (Dynamic && _currentIndex != 0)
        {
            graph.ResetBranch(this);
            _currentIndex = 0;
        }

        while (_currentIndex < _order.Length)
        {
            NPNode? child = EnabledChildAt(_order[_currentIndex]);
            if (child == null)
            {
                _currentIndex++;
                continue;
            }

            NPStatus status = graph.ExecuteNode(child, runner);
            switch (status)
            {
                case NPStatus.Running:
                    return NPStatus.Running;
                case NPStatus.Failure:
                    _currentIndex = 0;
                    return NPStatus.Failure;
                default:
                    _currentIndex++;
                    break;
            }
        }

        _currentIndex = 0;
        return NPStatus.Success;
    }

    internal override void OnAbort(NPBehaviourTreeAsset graph) => _currentIndex = 0;

    private void EnsureOrder(int count)
    {
        if (_order.Length != count)
            _order = new int[count];
        for (int i = 0; i < count; i++)
            _order[i] = i;
    }
}

/// <summary>
/// Tick every child each tick (true parallelism is not simulated — order is connection order).
/// Success/failure policies follow <see cref="NPParallelPolicy"/>; Running when any child runs.
/// </summary>
[NPName("Parallel", 80)]
[NPCategory("Composites")]
[NPDescription("Ticks all children each pass; overall result decided by the success/failure policies.")]
public class BTParallel : BTComposite
{
    public NPParallelPolicy SucceedPolicy = NPParallelPolicy.One;
    public NPParallelPolicy FailPolicy = NPParallelPolicy.One;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        int success = 0, failure = 0, running = 0, total = 0;
        for (int i = 0; i < OutConnections.Count; i++)
        {
            NPConnection wire = OutConnections[i];
            if (wire.Disabled)
                continue;
            total++;
            NPStatus status = graph.ExecuteNode(wire.Target, runner);
            switch (status)
            {
                case NPStatus.Running:
                    running++;
                    break;
                case NPStatus.Failure:
                    failure++;
                    break;
                default:
                    success++;
                    break;
            }
        }

        if (total == 0)
            return NPStatus.Success;
        if (running > 0 && !PolicyMet(SucceedPolicy, success, total) && !PolicyMet(FailPolicy, failure, total))
            return NPStatus.Running;

        // Failure policy wins ties: a satisfied failure is decisive even with enough successes.
        if (PolicyMet(FailPolicy, failure, total))
            return NPStatus.Failure;
        if (PolicyMet(SucceedPolicy, success, total))
            return NPStatus.Success;
        return running > 0 ? NPStatus.Running : NPStatus.Success;
    }

    private static bool PolicyMet(NPParallelPolicy policy, int hits, int total)
        => hits > 0 && (policy == NPParallelPolicy.One || hits == total);
}

/// <summary>
/// Utility AI: scores each child by its <see cref="Weights"/> entry (inline-or-bound float) and
/// runs the best; a running child keeps control while it still scores at least as high.
/// </summary>
[NPName("Priority Selector", 78)]
[NPCategory("Composites")]
[NPDescription("Utility AI: runs the highest-scoring child; a running branch stays sticky while competitive.")]
public class BTPrioritySelector : BTComposite
{
    /// <summary>Parallel to the outgoing connections; scores are converted with NPTypeConverter.</summary>
    public System.Collections.Generic.List<NPParameter> Weights = new();

    private int _runningIndex = -1;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        INPBlackboard bb = graph.GraphBlackboard!;
        int best = -1;
        float bestScore = float.MinValue;
        for (int i = 0; i < OutConnections.Count; i++)
        {
            if (OutConnections[i].Disabled)
                continue;
            float score = i < Weights.Count ? Weights[i].GetValue(bb, 0f) : 0f;
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        if (best < 0)
            return NPStatus.Failure;

        // Stickiness: a still-running branch keeps control even if another now ties it.
        if (_runningIndex >= 0 && _runningIndex < OutConnections.Count
            && OutConnections[_runningIndex].Target.Status == NPStatus.Running)
            best = _runningIndex;

        NPStatus status = graph.ExecuteNode(OutConnections[best].Target, runner);
        _runningIndex = best;
        _runningDone = status != NPStatus.Running;
        if (_runningDone)
            _runningIndex = -1;
        return status;
    }

    private bool _runningDone;

    internal override void OnAbort(NPBehaviourTreeAsset graph)
    {
        _runningIndex = -1;
        _runningDone = false;
    }
}

/// <summary>Weighted random pick; child weight comes from <see cref="Weights"/> (default 1).</summary>
[NPName("Probability Selector", 76)]
[NPCategory("Composites")]
[NPDescription("Weighted random child selection.")]
public class BTProbabilitySelector : BTComposite
{
    public System.Collections.Generic.List<NPParameter> Weights = new();

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        float total = 0f;
        int usable = 0;
        Span<float> scores = stackalloc float[Math.Max(OutConnections.Count, 1)];
        for (int i = 0; i < OutConnections.Count; i++)
        {
            if (OutConnections[i].Disabled)
                continue;
            float w = i < Weights.Count ? Weights[i].GetValue(graph.GraphBlackboard!, 1f) : 1f;
            scores[i] = MathF.Max(w, 0f);
            total += scores[i];
            usable++;
        }

        if (usable == 0 || total <= 0f)
            return NPStatus.Failure;

        float pick = (float)((XEngine.Runtime.Time.FrameCount * 2654435761u % 100000u) / 100000.0) * total;
        for (int i = 0; i < OutConnections.Count; i++)
        {
            if (OutConnections[i].Disabled)
                continue;
            pick -= scores[i];
            if (pick <= 0f || i == OutConnections.Count - 1)
                return graph.ExecuteNode(OutConnections[i].Target, runner);
        }

        return NPStatus.Failure;
    }
}

/// <summary>Gate child behind a condition; on failure returns <see cref="FailReturn"/> immediately.</summary>
[NPName("Binary Selector", 74)]
[NPCategory("Composites")]
public class BTBinarySelector : BTDecorator
{
    public NPConditionTask? Condition;
    public NPStatus FailReturn = NPStatus.Failure;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        if (Condition != null
            && (Condition.Bind(RunnerOf(graph), graph.GraphBlackboard, graph) != null || !Condition.Check()))
            return FailReturn;

        return Child != null ? graph.ExecuteNode(Child, runner) : NPStatus.Failure;
    }
}

/// <summary>Run the single child and invert its result (Success↔Failure; Running passes).</summary>
[NPName("Flip Selector", 70)]
[NPCategory("Composites")]
public class BTFlipSelector : BTDecorator
{
    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
        => Child is not { } child ? NPStatus.Failure : Flip(graph.ExecuteNode(child, runner));

    internal static NPStatus Flip(NPStatus status) => status switch
    {
        NPStatus.Success => NPStatus.Failure,
        NPStatus.Failure => NPStatus.Success,
        _ => status,
    };
}

/// <summary>Route by matching a parameter's string form against each child's connection comment tag.</summary>
[NPName("Switch", 68)]
[NPCategory("Composites")]
public class BTSwitch : BTComposite
{
    public NPParameter Value = new();
    public bool PassthroughOnNoMatch = true;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        string key = Value.GetValue(graph.GraphBlackboard!, "").ToString() ?? "";
        NPConnection? passthrough = null;
        for (int i = 0; i < OutConnections.Count; i++)
        {
            NPConnection wire = OutConnections[i];
            if (wire.Disabled)
                continue;
            if (string.Equals(wire.Target.Name, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(wire.Target.Comment, key, StringComparison.OrdinalIgnoreCase))
                return graph.ExecuteNode(wire.Target, runner);
            if (passthrough == null)
                passthrough = wire;
        }

        return PassthroughOnNoMatch && passthrough != null
            ? graph.ExecuteNode(passthrough.Target, runner)
            : NPStatus.Failure;
    }
}

/// <summary>Iterate children round-robin, one step per pass (success carries the iterator).</summary>
[NPName("Step Iterator", 64)]
[NPCategory("Composites")]
public class BTStepIterator : BTComposite
{
    private int _cursor;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        int count = 0;
        for (int i = 0; i < OutConnections.Count; i++)
        {
            if (!OutConnections[i].Disabled)
                count++;
        }

        if (count == 0)
            return NPStatus.Success;

        // find next enabled slot from cursor
        for (int step = 0; step < OutConnections.Count; step++)
        {
            int idx = (_cursor + step) % OutConnections.Count;
            NPConnection wire = OutConnections[idx];
            if (wire.Disabled)
                continue;
            _cursor = (idx + 1) % OutConnections.Count;
            NPStatus status = graph.ExecuteNode(wire.Target, runner);
            return status == NPStatus.Running ? NPStatus.Running : NPStatus.Success;
        }

        return NPStatus.Success;
    }

    internal override void OnAbort(NPBehaviourTreeAsset graph) => _cursor = 0;
}
