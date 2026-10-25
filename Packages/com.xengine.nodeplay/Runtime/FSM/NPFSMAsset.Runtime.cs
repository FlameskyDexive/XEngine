using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.NodePlay.FSM;

/// <summary>
/// Hierarchical state machine runtime: explicit start state, reactive/OnFinish transitions with
/// Normal/Stacked/Clean call modes (stack = push current, resume on pop), Any-State polling,
/// OnEnter/OnUpdate/OnExit pseudo-states, concurrent nested graphs and cross-module nesting.
/// </summary>
public partial class NPFSMAsset : NPGraphAsset
{
    [SerializeIgnore] public FSMState? CurrentState;
    [SerializeIgnore] public FSMState? PreviousState;
    [SerializeIgnore] public FSMState? LastEnteredState;
    [SerializeIgnore] private readonly List<FSMState> _stack = new();
    [SerializeIgnore] private readonly List<FSMConcurrentState> _concurrent = new();

    /// <summary>Raised on every state transition (args: from, to).</summary>
    public static event Action<NPFSMAsset, FSMState?, FSMState>? OnStateTransition;

    protected internal override NPConnection CreateConnection() => new FSMConnection();

    // ---- public control -----------------------------------------------------------------

    public void TriggerState(string stateName)
    {
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (Nodes[i] is FSMState state
                && (string.Equals(state.DisplayName, stateName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state.Name, stateName, StringComparison.OrdinalIgnoreCase)))
            {
                EnterStateById(state.Id, NPTransitionCallMode.Normal);
                return;
            }
        }
    }

    public FSMState? PeekStack() => _stack.Count > 0 ? _stack[^1] : null;

    // ---- lifecycle ----------------------------------------------------------------------

    protected internal override void OnStarted(NPRunner runner)
    {
        base.OnStarted(runner);
        if (runner != null)
            OwnerRunner = runner;              // direct-start path (tests/manual stepping)
        GraphBlackboard ??= LocalBlackboard;
        _stack.Clear();
        ConcurrentCache(_concurrent);

        FSMState? start = null;
        if (!string.IsNullOrEmpty(StartStateId))
            start = GetNode<FSMState>(StartStateId);
        start ??= FindFirst<FSMState>();
        if (start != null)
            EnterState(start, NPTransitionCallMode.Normal);
    }

    protected internal override void OnStopped(NPRunner runner, bool success)
    {
        base.OnStopped(runner, success);
        if (CurrentState != null)
        {
            CurrentState.Exit(runner, clean: false);
            CurrentState = null;
        }

        _stack.Clear();
        foreach (FSMConcurrentState concurrent in _concurrent)
            concurrent.Nested.ReleaseAll();
    }

    protected internal override void TickRunner(float delta)
    {
        LastDelta = delta;                     // direct-start path has no runner.Step to set it
        float prevElapsed = ElapsedTime;
        foreach (FSMNode node in EnumerateNodes<FSMNode>())
        {
            switch (node)
            {
                case FSMAnyState any:
                    any.Poll(this, OwnerRunner!);
                    break;
                case FSMOnUpdateState onUpdate:
                    onUpdate.ConsumedThisTick = false;
                    break;
            }
        }

        // OnUpdate pseudo states fire alongside the current state
        foreach (FSMNode node in EnumerateNodes<FSMNode>())
        {
            if (node is FSMOnUpdateState onUpdate && !onUpdate.ConsumedThisTick)
                onUpdate.Update(OwnerRunner!);
        }

        foreach (FSMConcurrentState concurrent in _concurrent)
            concurrent.Tick(this, OwnerRunner!, delta);

        CurrentState?.Update(OwnerRunner!);
        CheckTransitions(CurrentState, OwnerRunner!);
        _ = prevElapsed;
    }

    protected internal override void HandleEvent(string name, object? value)
    {
        base.HandleEvent(name, value);
        // events double as TriggerState aliases
        TriggerState(name);
    }

    // ---- internals ----------------------------------------------------------------------

    internal void EnterStateById(string targetId, NPTransitionCallMode mode)
    {
        if (GetNode<FSMState>(targetId) is not { } target)
            return;
        EnterState(target, mode);
    }

    internal void EnterState(FSMState target, NPTransitionCallMode mode)
    {
        if (ReferenceEquals(CurrentState, target))
            return;

        FSMState? leaving = CurrentState;
        if (mode == NPTransitionCallMode.Stacked && leaving != null)
        {
            leaving.Exit(OwnerRunner!, clean: false);
            _stack.Add(leaving);
        }
        else
        {
            leaving?.Exit(OwnerRunner!, clean: mode == NPTransitionCallMode.Clean);
            if (mode != NPTransitionCallMode.Stacked)
                _stack.Clear();
        }

        PreviousState = leaving;
        CurrentState = target;
        LastEnteredState = target;
        target.Enter(OwnerRunner!);
        OnStateTransition?.Invoke(this, leaving, target);
        NPGraphConsole.Log(this, "state", $"enter '{target.DisplayName}'", target.Id);
    }

    private void CheckTransitions(FSMState? state, NPRunner runner)
    {
        if (state == null)
            return;

        bool finished = state.Finished;
        bool mayCheck = state.TransitionEvaluation switch
        {
            NPTransitionEvaluation.CheckContinuously => true,
            NPTransitionEvaluation.CheckAfterStateFinished => finished,
            _ => false,
        };

        // OnFinish edges (no condition) only fire when the state finished
        for (int i = 0; i < state.OutConnections.Count; i++)
        {
            if (state.OutConnections[i] is not FSMConnection wire || wire.Disabled)
                continue;

            if (wire.Condition == null)
            {
                if (finished && GetNode<FSMState>(wire.ToNodeId) is { } onFinishTarget)
                {
                    EnterState(onFinishTarget, wire.CallMode);
                    return;
                }

                continue;
            }

            if (!mayCheck)
                continue;
            if (wire.Condition.Bind(runner, GraphBlackboard, this) != null)
                continue;
            if (!wire.Condition.Check())
                continue;

            EnterStateById(wire.ToNodeId, wire.CallMode);
            return;
        }

        // a Stacked state finished → pop back
        if (finished && _stack.Count > 0)
        {
            FSMState resumed = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            CurrentState!.Exit(OwnerRunner!, clean: false);
            CurrentState = resumed;
            resumed.Enter(OwnerRunner!);
        }
    }

    internal void RaiseStateEntered(FSMState state)
    {
        if (state is FSMOnExitState exitPseudo)
            exitPseudo.Fire(OwnerRunner!);
    }

    internal void RaiseStateExited(FSMState state)
    {
        // route to OnExit pseudo states connected FROM the exited state's graph region:
        // simple convention — an OnExit pseudo-state fires when ANY state exits.
        foreach (FSMNode node in EnumerateNodes<FSMNode>())
        {
            if (node is FSMOnExitState exit)
                exit.Fire(OwnerRunner!);
        }
    }

    private void ConcurrentCache(List<FSMConcurrentState> into)
    {
        into.Clear();
        foreach (FSMNode node in EnumerateNodes<FSMNode>())
        {
            if (node is FSMConcurrentState concurrent)
                into.Add(concurrent);
        }
    }

    private IEnumerable<T> EnumerateNodes<T>() where T : NPNode
    {
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (Nodes[i] is T typed)
                yield return typed;
        }
    }

    private T? FindFirst<T>() where T : NPNode
    {
        foreach (T typed in EnumerateNodes<T>())
            return typed;
        return null;
    }
}
