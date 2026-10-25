using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.NodePlay.FSM;

/// <summary>How a transition enters the target when its condition passes.</summary>
public enum NPTransitionCallMode
{
    /// <summary>Exit current, enter target (default).</summary>
    Normal,
    /// <summary>Push current; when the target finishes, the pushed state resumes.</summary>
    Stacked,
    /// <summary>Exit current WITHOUT running its exit list, enter target fresh.</summary>
    Clean,
}

/// <summary>When a state evaluates its outgoing conditional transitions.</summary>
public enum NPTransitionEvaluation
{
    /// <summary>Every tick while the state runs (reactive FSM).</summary>
    CheckContinuously,
    /// <summary>Only after the state's update list finished (classic FSM).</summary>
    CheckAfterStateFinished,
    /// <summary>Only via TriggerState / SendEvent from outside.</summary>
    CheckManually,
}

/// <summary>Receiver components on the agent get state lifecycle callbacks.</summary>
public interface INPStateCallbackReceiver
{
    void OnStateEnter(string stateName);
    void OnStateUpdate(string stateName);
    void OnStateExit(string stateName);
}

/// <summary>Base of all FSM graph nodes (states, pseudo-states, any-state).</summary>
[NPCategory("FSM")]
public abstract class FSMNode : NPNode
{
    /// <summary>FSM graph owns execution; nodes expose hook points instead.</summary>
}

/// <summary>
/// A state: enter/update/exit action lists, transition evaluation mode, and the Finished flag
/// that condition-less ("OnFinish") transitions wait for.
/// </summary>
[NPName("State", 90)]
[NPCategory("FSM")]
public class FSMState : FSMNode
{
    public List<NPActionTask> OnEnterActions = new();
    public List<NPActionTask> OnUpdateActions = new();
    public List<NPActionTask> OnExitActions = new();
    public NPTransitionEvaluation TransitionEvaluation = NPTransitionEvaluation.CheckContinuously;
    public bool RepeatStateActions;

    [SerializeIgnore] public bool Entered;
    [SerializeIgnore] public bool Finished;

    internal NPFSMAsset Owner => (NPFSMAsset)Graph;

    internal virtual void Enter(NPRunner runner)
    {
        Entered = true;
        Finished = false;
        for (int i = 0; i < OnUpdateActions.Count; i++)
            OnUpdateActions[i].ResetForReuse();
        RunList(OnEnterActions, runner, fresh: true);
        Notify(runner, receiver => receiver.OnStateEnter(DisplayName));
        Owner.RaiseStateEntered(this);
    }

    internal virtual void Update(NPRunner runner)
    {
        bool listsDone = RunList(OnUpdateActions, runner, fresh: false);
        if (!RepeatStateActions && listsDone && !Finished && OnUpdateActions.Count > 0)
            Finished = true;
        if (OnUpdateActions.Count == 0)
            Finished = true;
        Notify(runner, receiver => receiver.OnStateUpdate(DisplayName));
    }

    internal virtual void Exit(NPRunner runner, bool clean)
    {
        if (!clean)
            RunList(OnExitActions, runner, fresh: false);
        Notify(runner, receiver => receiver.OnStateExit(DisplayName));
        Entered = false;
        Finished = false;
        Owner.RaiseStateExited(this);
    }

    protected bool RunList(List<NPActionTask> tasks, NPRunner runner, bool fresh)
    {
        bool allDone = true;
        for (int i = 0; i < tasks.Count; i++)
        {
            NPActionTask task = tasks[i];
            if (fresh && task.Result != null)
                task.ResetForReuse();
            if (task.Bind(runner, Graph.GraphBlackboard, Graph) != null)
            {
                NPGraphConsole.Warn(Graph, $"'{DisplayName}' action init: {task.InitError}", Id);
                continue;
            }

            if (task.Result == null)
            {
                task.Tick();
                if (task.Result == null)
                    allDone = false;
            }
        }

        return allDone;
    }

    private void Notify(NPRunner runner, Action<INPStateCallbackReceiver> call)
    {
        if (runner == null)
            return;
        foreach (MonoBehaviour component in runner.GetComponents(typeof(INPStateCallbackReceiver)))
        {
            if (component is INPStateCallbackReceiver receiver)
                call(receiver);
        }
    }
}

/// <summary>Does nothing, finishes immediately — pure routing hub.</summary>
[NPName("Pass", 70)]
[NPCategory("FSM")]
public class FSMEmptyState : FSMState
{
}

/// <summary>
/// Global transition poller: its outgoing conditional transitions are evaluated every tick no
/// matter which state is current; an unmatched pass leaves the FSM alone.
/// </summary>
[NPName("Any State", 88)]
[NPCategory("FSM")]
public class FSMAnyState : FSMNode
{
    public bool DontRetriggerSameState = true;

    internal void Poll(NPFSMAsset fsm, NPRunner runner)
    {
        for (int i = 0; i < OutConnections.Count; i++)
        {
            var wire = OutConnections[i] as FSMConnection;
            if (wire == null || wire.Disabled || wire.Condition == null)
                continue;
            if (wire.Condition.Bind(runner, fsm.GraphBlackboard, fsm) != null)
                continue;
            if (!wire.Condition.Check())
                continue;
            if (DontRetriggerSameState && fsm.CurrentState != null
                && fsm.CurrentState.Id == wire.ToNodeId)
                continue;

            fsm.EnterStateById(wire.ToNodeId, wire.CallMode);
            return;
        }
    }
}

/// <summary>Pseudo-states run an action list when their lifecycle moment fires.</summary>
[NPName("On Enter FSM", 86)]
[NPCategory("FSM")]
public class FSMOnEnterState : FSMState
{
    internal override void Enter(NPRunner runner)
    {
        base.Enter(runner);
        // run once now; the pseudo state is not a place to linger
        Owner.EnterStateById(FirstTarget(), NPTransitionCallMode.Normal);
    }

    internal string? FirstTarget()
    {
        for (int i = 0; i < OutConnections.Count; i++)
        {
            if (!OutConnections[i].Disabled)
                return OutConnections[i].ToNodeId;
        }

        return null;
    }
}

[NPName("On Update FSM", 84)]
[NPCategory("FSM")]
public class FSMOnUpdateState : FSMState
{
    internal bool ConsumedThisTick;

    internal override void Update(NPRunner runner)
    {
        ConsumedThisTick = true;
        RunList(OnUpdateActions, runner, fresh: !Entered);
    }
}

[NPName("On Exit FSM", 82)]
[NPCategory("FSM")]
public class FSMOnExitState : FSMState
{
    internal void Fire(NPRunner runner)
    {
        RunList(OnUpdateActions, runner, fresh: true);
    }
}
