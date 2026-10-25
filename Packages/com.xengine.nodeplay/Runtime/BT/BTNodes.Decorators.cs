using System;

using XEngine.Echo;

namespace XEngine.NodePlay.BT;

/// <summary>
/// Conditional gate around one child. With <see cref="Dynamic"/> the condition re-checks every
/// tick and aborts the child on failure (reactive guard); otherwise it is checked once on enter.
/// <see cref="FailReturn"/> chooses the response (Failure, or Optional to swallow).
/// </summary>
[NPName("Conditional", 88)]
[NPCategory("Decorators")]
[NPDescription("Condition gate with optional per-tick re-check (Dynamic) that aborts the child on failure.")]
public class BTConditionalEvaluator : BTDecorator
{
    public NPConditionTask? Condition;
    public bool Dynamic = true;
    public NPStatus FailReturn = NPStatus.Failure;
    public bool InvertFailReturn;

    private bool _checked;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        if (Child is not { } child)
            return NPStatus.Failure;

        if (Condition == null)
            return graph.ExecuteNode(child, runner);

        if (Condition.Bind(RunnerOf(graph), graph.GraphBlackboard, graph) != null)
            return FailReturn;

        bool pass = Condition.Check();
        if (!Dynamic && _checked)
            pass = true; // one-shot: keep running child after the initial pass
        _checked = true;

        if (!pass)
        {
            if (Child is { } c0)
                graph.ResetBranch(c0);
            _checked = false;
            return FailReturn;
        }

        NPStatus status = graph.ExecuteNode(child, runner);
        if (status != NPStatus.Running)
            _checked = false; // re-arm for the next pass through this gate
        return status;
    }

    internal override void OnAbort(NPBehaviourTreeAsset graph) => _checked = false;
}

/// <summary>
/// Abort the child when the condition turns true (watchdog). Returns
/// <see cref="AbortStatus"/> that tick; while quiet the child flows through.
/// </summary>
[NPName("Interruptor", 84)]
[NPCategory("Decorators")]
[NPDescription("Watchdog: aborts the child the moment the condition turns true.")]
public class BTInterruptor : BTDecorator
{
    public NPConditionTask? Condition;
    public NPStatus AbortStatus = NPStatus.Failure;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        if (Child is not { } child)
            return NPStatus.Failure;

        if (Condition != null
            && Condition.Bind(RunnerOf(graph), graph.GraphBlackboard, graph) == null
            && Condition.Check())
        {
            if (Child is { } c1)
                graph.ResetBranch(c1);
            return AbortStatus;
        }

        return graph.ExecuteNode(child, runner);
    }
}

/// <summary>
/// Token-based mutual exclusion: while any other Guarded branch in the tree holds the
/// <see cref="Token"/>, this node fails fast instead of starting its child.
/// </summary>
[NPName("Guard", 82)]
[NPCategory("Decorators")]
[NPDescription("Token mutex: fails fast while another branch holds the same token.")]
public class BTGuard : BTDecorator
{
    public string Token = "guard";

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        if (Child is not { } child)
            return NPStatus.Failure;

        if (!graph.Guards.TryAcquire(Token, this))
            return NPStatus.Failure;

        NPStatus status = graph.ExecuteNode(child, runner);
        if (status != NPStatus.Running)
            graph.Guards.ReleaseAll(this);
        return status;
    }

    internal override void OnAbort(NPBehaviourTreeAsset graph) => graph.Guards.ReleaseAll(this);
}

/// <summary>Keep an action running alongside the child every tick (heartbeat/animation keepers).</summary>
[NPName("Monitor", 80)]
[NPCategory("Decorators")]
public class BTMonitor : BTDecorator
{
    public NPActionTask? KeepAlive;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        if (KeepAlive != null && KeepAlive.Result == null
            && KeepAlive.Bind(RunnerOf(graph), graph.GraphBlackboard, graph) == null)
        {
            KeepAlive.Tick();
        }

        return Child is not { } child ? NPStatus.Failure : graph.ExecuteNode(child, runner);
    }
}

/// <summary>Time window around the child: start delay, max duration, and re-entry cooldown.</summary>
[NPName("Filter", 78)]
[NPCategory("Decorators")]
public class BTFilter : BTDecorator
{
    public float StartDelay;
    public float MaxTime;
    public float Cooldown;

    private float _cooldownLeft;
    private float _activeTime;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        if (_cooldownLeft > 0f)
        {
            _cooldownLeft -= graph.ElapsedDelta();
            return NPStatus.Failure;
        }

        if (Child is not { } child)
            return NPStatus.Failure;

        if (StartDelay > 0f && graph.ElapsedTime < StartDelay)
            return NPStatus.Failure;

        NPStatus status = graph.ExecuteNode(child, runner);
        if (status == NPStatus.Running)
        {
            if (MaxTime > 0f)
            {
                _activeTime += graph.ElapsedDelta();
                if (_activeTime > MaxTime)
                {
                    if (Child is { } c2)
                        graph.ResetBranch(c2);
                    _activeTime = 0f;
                    _cooldownLeft = Cooldown;
                    return NPStatus.Failure;
                }
            }

            return NPStatus.Running;
        }

        _activeTime = 0f;
        _cooldownLeft = Cooldown;
        return status;
    }

    internal override void OnAbort(NPBehaviourTreeAsset graph)
    {
        _cooldownLeft = Cooldown;
        _activeTime = 0f;
    }
}

/// <summary>Iterate a list variable; the element is written to <see cref="Item"/> each pass.</summary>
[NPName("Iterator", 76)]
[NPCategory("Decorators")]
public class BTIterator : BTDecorator
{
    public NPParameter List = new();
    public string ItemName = "item";
    public bool Loop;

    private System.Collections.IEnumerable? _source;
    private System.Collections.IEnumerator? _enumerator;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        if (Child is not { } child)
            return NPStatus.Failure;

        if (_enumerator == null)
        {
            _source = List.GetValue<object>(graph.GraphBlackboard!, "") as System.Collections.IEnumerable;
            if (_source == null)
                return NPStatus.Failure;
            _enumerator = _source.GetEnumerator();
        }

        INPBlackboard bb = graph.GraphBlackboard!;
        while (true)
        {
            if (!_enumerator.MoveNext())
            {
                if (!Loop)
                {
                    Stop();
                    return NPStatus.Success;
                }

                _enumerator = _source!.GetEnumerator();
                continue;
            }

            bb.Ensure(ItemName, "");
            bb.SetValue(ItemName, _enumerator.Current ?? "");
            return graph.ExecuteNode(child, runner);
        }
    }

    internal override void OnAbort(NPBehaviourTreeAsset graph) => Stop();

    private void Stop()
    {
        _enumerator = null;
        _source = null;
    }
}

/// <summary>Fail the child when it exceeds <see cref="Seconds"/>.</summary>
[NPName("Timeout", 74)]
[NPCategory("Decorators")]
public class BTTimeout : BTDecorator
{
    public float Seconds = 5f;

    private float _elapsed;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        if (Child is not { } child)
            return NPStatus.Failure;

        _elapsed += graph.ElapsedDelta();
        if (_elapsed > Seconds)
        {
            if (Child is { } c3)
                graph.ResetBranch(c3);
            _elapsed = 0f;
            return NPStatus.Failure;
        }

        NPStatus status = graph.ExecuteNode(child, runner);
        if (status != NPStatus.Running)
            _elapsed = 0f;
        return status;
    }

    internal override void OnAbort(NPBehaviourTreeAsset graph) => _elapsed = 0f;
}

/// <summary>Block until a condition passes, reporting Running meanwhile.</summary>
[NPName("Wait Until", 72)]
[NPCategory("Decorators")]
public class BTWaitUntil : BTDecorator
{
    public NPConditionTask? Condition;
    public bool CheckChildFirst = true;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        if (Child is { } child && CheckChildFirst)
        {
            NPStatus status = graph.ExecuteNode(child, runner);
            if (status == NPStatus.Running)
                return NPStatus.Running;
        }

        if (Condition != null
            && Condition.Bind(RunnerOf(graph), graph.GraphBlackboard, graph) == null)
        {
            return Condition.Check() && Child is { } ok ? graph.ExecuteNode(ok, runner) : NPStatus.Running;
        }

        return Child is { } c2 ? graph.ExecuteNode(c2, runner) : NPStatus.Failure;
    }
}

/// <summary>Repeat the child; <see cref="Times"/>=0 means forever (stops early on failure).</summary>
[NPName("Repeater", 70)]
[NPCategory("Decorators")]
public class BTRepeater : BTDecorator
{
    public int Times;

    private int _done;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        if (Child is not { } child)
            return NPStatus.Success;

        NPStatus status = graph.ExecuteNode(child, runner);
        if (status == NPStatus.Running)
            return NPStatus.Running;
        if (status == NPStatus.Failure)
        {
            _done = 0;
            return NPStatus.Failure;
        }

        _done++;
        if (Times <= 0 || _done < Times)
        {
            if (Child is { } c4)
                graph.ResetBranch(c4);
            return NPStatus.Running; // continue repeating next tick
        }

        _done = 0;
        return NPStatus.Success;
    }

    internal override void OnAbort(NPBehaviourTreeAsset graph) => _done = 0;
}

/// <summary>Invert the child result (Success↔Failure, Running passes, Optional stays).</summary>
[NPName("Inverter", 68)]
[NPCategory("Decorators")]
public class BTInverter : BTDecorator
{
    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
        => Child is not { } child ? NPStatus.Failure : BTFlipSelector.Flip(graph.ExecuteNode(child, runner));
}

/// <summary>Swallow child failure as <see cref="NPStatus.Optional"/> (selectors treat it as success).</summary>
[NPName("Optional", 66)]
[NPCategory("Decorators")]
public class BTOptional : BTDecorator
{
    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        if (Child is not { } child)
            return NPStatus.Success;
        NPStatus status = graph.ExecuteNode(child, runner);
        return status == NPStatus.Failure ? NPStatus.Optional : status;
    }
}

/// <summary>Write a value into a variable, then flow through the child (or succeed without one).</summary>
[NPName("Setter", 64)]
[NPCategory("Decorators")]
public class BTSetter : BTDecorator
{
    public NPParameter Target = new();
    public NPParameter Source = new();

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        INPBlackboard bb = graph.GraphBlackboard!;
        Target.SetValue(bb, Source.GetValue(bb));
        return Child is { } child ? graph.ExecuteNode(child, runner) : NPStatus.Success;
    }
}

/// <summary>
/// Copy variables under alternate names for the decorated branch (enter: alt←source;
/// exit: source←alt). A lightweight, localised remap for shared subtrees.
/// </summary>
[NPName("Remapper", 62)]
[NPCategory("Decorators")]
public class BTRemapper : BTDecorator
{
    /// <summary>Parallel arrays: branch-local alias ↔ graph variable name.</summary>
    public System.Collections.Generic.List<NPParameter> Aliases = new();
    public System.Collections.Generic.List<string> GraphVariables = new();

    private bool _active;

    internal override NPStatus Execute(NPBehaviourTreeAsset graph, NPRunner runner)
    {
        if (Child is not { } child)
            return NPStatus.Failure;

        if (!_active)
        {
            _active = true;
            INPBlackboard bb = graph.GraphBlackboard!;
            for (int i = 0; i < Aliases.Count && i < GraphVariables.Count; i++)
                Aliases[i].SetValue(bb, bb.GetValue<object>(GraphVariables[i], ""));
        }

        NPStatus status = graph.ExecuteNode(child, runner);
        if (status != NPStatus.Running)
            WriteBack(graph);
        return status;
    }

    internal override void OnAbort(NPBehaviourTreeAsset graph) => WriteBack(graph);

    private void WriteBack(NPBehaviourTreeAsset graph)
    {
        if (!_active)
            return;
        _active = false;
        INPBlackboard bb = graph.GraphBlackboard!;
        for (int i = 0; i < Aliases.Count && i < GraphVariables.Count; i++)
            bb.SetValue(GraphVariables[i], Aliases[i].GetValue(bb));
    }
}
