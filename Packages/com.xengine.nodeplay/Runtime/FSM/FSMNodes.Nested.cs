using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.NodePlay.BT;

namespace XEngine.NodePlay.FSM;

/// <summary>A transition: optional condition + call mode. Condition-less = "OnFinish" edge.</summary>
[NPName("Transition", 60)]
public class FSMConnection : NPConnection
{
    public NPConditionTask? Condition;
    public NPTransitionCallMode CallMode = NPTransitionCallMode.Normal;
}

/// <summary>Runs a nested graph (FSM/BT/Dialogue) alongside every state tick.</summary>
[NPName("Concurrent Sub Graph", 60)]
[NPCategory("FSM")]
public class FSMConcurrentState : FSMNode
{
    public NPNestedRuntime Nested = new();

    internal void Tick(NPFSMAsset fsm, NPRunner runner, float delta)
    {
        if (Nested.InstanceOf(fsm) == null)
            Nested.GetOrStartInstance(fsm, fsm);   // lazy start alongside the FSM
        Nested.TickInstances(fsm, delta);
    }
}

/// <summary>Runs a nested FSM as a state; forwards Finish into its own completion.</summary>
[NPName("Nested FSM", 58)]
[NPCategory("FSM")]
public class FSMNestedState : FSMState
{
    public NPNestedRuntime Nested = new();

    internal NPGraphAsset? Instance;

    internal new NPFSMAsset Owner => (NPFSMAsset)Graph;

    internal override void Enter(NPRunner runner)
    {
        base.Enter(runner);
        Instance = Nested.GetOrStartInstance(Graph, Graph);
    }

    internal override void Update(NPRunner runner)
    {
        Nested.TickInstances(Graph, Graph.ElapsedDeltaSafe());
        Finished = !Nested.IsRunning(Graph);
    }

    internal override void Exit(NPRunner runner, bool clean)
    {
        Nested.ReleaseFor(Graph);
        Instance = null;
        base.Exit(runner, clean);
    }
}

/// <summary>
/// Runs a behaviour tree as a state; on natural tree settle (non-repeat child) the state
/// finishes and can fire success/failure named events into the FSM.
/// </summary>
[NPName("Nested BT", 56)]
[NPCategory("FSM")]
public class FSMNestedBTState : FSMState
{
    public NPNestedRuntime Nested = new();
    public string SuccessEvent = "";
    public string FailureEvent = "";
    public bool StopOnExit = true;

    internal NPGraphAsset? Instance;

    internal override void Enter(NPRunner runner)
    {
        base.Enter(runner);
        Instance = Nested.GetOrStartInstance(Graph, Graph);
    }

    internal override void Update(NPRunner runner)
    {
        Nested.TickInstances(Graph, Graph.ElapsedDeltaSafe());
        if (Instance is NPBehaviourTreeAsset bt && !bt.Repeat
            && bt.LastRootStatus is NPStatus.Success or NPStatus.Failure)
        {
            Finished = true;
            string evt = bt.LastRootStatus == NPStatus.Success ? SuccessEvent : FailureEvent;
            if (evt.Length > 0)
                NPEvents.SendGlobalEvent(evt);
        }
    }

    internal override void Exit(NPRunner runner, bool clean)
    {
        if (StopOnExit)
            Nested.ReleaseFor(Graph);
        Instance = null;
        base.Exit(runner, clean);
    }
}

/// <summary>Runs a dialogue tree as a state (dialogue module lands in its own milestone).</summary>
[NPName("Nested Dialogue", 54)]
[NPCategory("FSM")]
public class FSMNestedDTState : FSMState
{
    public NPNestedRuntime Nested = new();

    internal override void Enter(NPRunner runner)
    {
        base.Enter(runner);
        Nested.GetOrStartInstance(Graph, Graph);
    }

    internal override void Update(NPRunner runner)
    {
        Nested.TickInstances(Graph, Graph.ElapsedDeltaSafe());
    }

    internal override void Exit(NPRunner runner, bool clean)
    {
        Nested.ReleaseFor(Graph);
        base.Exit(runner, clean);
    }
}
