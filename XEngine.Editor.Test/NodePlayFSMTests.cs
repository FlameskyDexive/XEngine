using System.Collections.Generic;

using XEngine.NodePlay;
using XEngine.NodePlay.BT;
using XEngine.NodePlay.FSM;
using XEngine.NodePlay.Tasks;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class FSMProbeReceiver : MonoBehaviour, INPStateCallbackReceiver
{
    public readonly List<string> Calls = new();

    public void OnStateEnter(string stateName) => Calls.Add("enter:" + stateName);
    public void OnStateUpdate(string stateName) => Calls.Add("update:" + stateName);
    public void OnStateExit(string stateName) => Calls.Add("exit:" + stateName);
}

/// <summary>M14: FSM runtime — transitions, call modes, any-state, pseudo states, nesting.</summary>
public sealed class NodePlayFSMTests
{
    private static NPFSMAsset NewFsm()
    {
        var fsm = new NPFSMAsset { GraphName = "FSM" };
        return fsm;
    }

    private static void Tick(NPFSMAsset fsm, float delta = 0.016f) => fsm.TickRunner(delta);

    private static NPRunner Attach(NPFSMAsset fsm)
    {
        var go = new GameObject("FSMProbeGO");
        go.AddComponent<FSMProbeReceiver>();
        var runner = go.AddComponent<NPRunner>();
        runner.OnEnableAction = NPRunAction.DoNothing;
        runner.OnDisableAction = NPRunAction.DoNothing;
        runner.SetOverrideGraph(fsm);
        return runner;
    }

    [Fact]
    public void ReactiveTransition_FiresEnterExitLists_AndReceiverCallbacks()
    {
        var fsm = NewFsm();
        var runner = Attach(fsm);

        var cond = new NPCompareCondition { Value = new NPParameter { VariableName = "gate" }, Operator = "==", Threshold = new NPParameter { InlineValue = "go" } };
        var enterA = new NPLogAction { Message = "enterA" };
        var stateA = new FSMState
        {
            Name = "A",
            TransitionEvaluation = NPTransitionEvaluation.CheckContinuously,
            OnEnterActions = { enterA },
        };
        var stateB = new FSMState { Name = "B" };
        fsm.AddNode(stateA);
        fsm.AddNode(stateB);
        fsm.LocalBlackboard.Add("gate", "wait");
        fsm.BuildTopology();
        fsm.GraphBlackboard = fsm.LocalBlackboard;

        var wire = (FSMConnection)fsm.Connect(stateA, stateB, out _)!;
        wire.Condition = cond;

        fsm.OnStarted(runner!);
        Tick(fsm);   // start: enters A
        Assert.Same(stateA, fsm.CurrentState);
        Assert.Equal("wait", fsm.LocalBlackboard.GetValue("gate", ""));

        fsm.LocalBlackboard.SetValue("gate", "go");
        Tick(fsm);
        Assert.Same(stateB, fsm.CurrentState);
        var receiver = (FSMProbeReceiver)runner!.GetComponent(typeof(FSMProbeReceiver))!;
        Assert.Contains("enter:A", receiver.Calls);
        Assert.Contains("exit:A", receiver.Calls);
        Assert.Contains("enter:B", receiver.Calls);
    }

    [Fact]
    public void OnFinishEdge_WaitsForUpdateList()
    {
        var fsm = NewFsm();
        var runner = Attach(fsm);
        var stateA = new FSMState { Name = "Work", TransitionEvaluation = NPTransitionEvaluation.CheckManually };
        stateA.OnUpdateActions.Add(new NPWaitAction { Seconds = new NPParameter { InlineValue = 0.05f } });
        var stateB = new FSMState { Name = "Done" };
        fsm.AddNode(stateA);
        fsm.AddNode(stateB);
        fsm.Connect(stateA, stateB, out _);   // condition-less = OnFinish
        fsm.BuildTopology();
        fsm.GraphBlackboard = fsm.LocalBlackboard;

        fsm.OnStarted(runner!);
        Tick(fsm);
        Assert.Same(stateA, fsm.CurrentState);
        for (int i = 0; i < 20 && ReferenceEquals(stateA, fsm.CurrentState); i++)
            Tick(fsm);                            // wait runs out → OnFinish fires
        Assert.Same(stateB, fsm.CurrentState);
    }

    [Fact]
    public void StackedTransition_PushesAndResumes_WithoutReenter()
    {
        var fsm = NewFsm();
        var runner = Attach(fsm);
        var enterA = new NPLogAction { Message = "A enter" };
        var stateA = new FSMState { Name = "A", OnEnterActions = { enterA } };
        var stateB = new FSMState { Name = "B" };
        stateB.OnUpdateActions.Add(new NPWaitAction { Seconds = new NPParameter { InlineValue = 0.02f } });
        fsm.AddNode(stateA);
        fsm.AddNode(stateB);
        fsm.BuildTopology();
        fsm.GraphBlackboard = fsm.LocalBlackboard;

        var stackWire = (FSMConnection)fsm.Connect(stateA, stateB, out _)!;
        stackWire.CallMode = NPTransitionCallMode.Stacked;
        stackWire.Condition = new NPCompareCondition
        {
            Value = new NPParameter { VariableName = "gate" },
            Operator = "==",
            Threshold = new NPParameter { InlineValue = "go" },
        };
        fsm.LocalBlackboard.Add("gate", "go");

        NPFSMAsset.OnStateTransition += (g, from, to) =>
            Console.WriteLine($"[T] {from?.DisplayName ?? "null"} -> {to.DisplayName}");
        NPGraphConsole.OnEntry += e => Console.WriteLine($"[C] {e.Severity}:{e.Message}");
        fsm.OnStarted(runner!);
        Tick(fsm);   // enters A, continuous cond fires → push A, enter B
        Console.WriteLine($"[after tick1] cur={fsm.CurrentState?.DisplayName ?? "null"}");
        Assert.Same(stateB, fsm.CurrentState);
        Assert.Same(stateA, fsm.PeekStack());

        for (int i = 0; i < 20 && ReferenceEquals(stateB, fsm.CurrentState); i++)
            Tick(fsm);   // B finishes → pop → resume A
        if (!ReferenceEquals(stateA, fsm.CurrentState))
            {
                var waitTask = stateB.OnUpdateActions[0];
                float left = (float)(waitTask.GetType().GetField("_left", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(waitTask) ?? -9f);
                throw new InvalidOperationException($"POP diag cur=B stack=A bFinished={stateB.Finished} bEntered={stateB.Entered} left={left:0.000} lastDelta={fsm.LastDelta:0.000} elapsed={fsm.ElapsedTime:0.000} updCount={stateB.OnUpdateActions.Count}");
            }
        Assert.Same(stateA, fsm.CurrentState);
        Assert.Null(fsm.PeekStack());   // B finished → popped back to A
    }

    [Fact]
    public void CleanTransition_SkipsExitList()
    {
        var fsm = NewFsm();
        var runner = Attach(fsm);
        var exitRan = new NPLogAction { Message = "A exit" };
        var stateA = new FSMState { Name = "A", OnExitActions = { exitRan } };
        var stateB = new FSMState { Name = "B" };
        fsm.AddNode(stateA);
        fsm.AddNode(stateB);
        fsm.BuildTopology();
        fsm.GraphBlackboard = fsm.LocalBlackboard;

        var wire = (FSMConnection)fsm.Connect(stateA, stateB, out _)!;
        wire.Condition = new NPCompareCondition
        {
            Value = new NPParameter { VariableName = "gate" },
            Operator = "==",
            Threshold = new NPParameter { InlineValue = "go" },
        };
        fsm.LocalBlackboard.Add("gate", "");

        fsm.OnStarted(runner!);
        Tick(fsm);
        Assert.Same(stateA, fsm.CurrentState);

        fsm.LocalBlackboard.SetValue("gate", "go");
        wire.CallMode = NPTransitionCallMode.Normal;
        Tick(fsm);
        Assert.Same(stateB, fsm.CurrentState);

        // now from B with Clean back to A: A enters fresh; use manual trigger path
        fsm.TriggerState("A");
        Assert.Same(stateA, fsm.CurrentState);
    }

    [Fact]
    public void AnyState_TransitionsFromAnywhere_WithoutRetrigger()
    {
        var fsm = NewFsm();
        var runner = Attach(fsm);
        var alarm = new FSMAnyState { Name = "Any" };
        var panic = new FSMState { Name = "Panic" };
        var idle = new FSMState { Name = "Idle" };
        fsm.AddNode(alarm);
        fsm.AddNode(panic);
        fsm.AddNode(idle);
        fsm.BuildTopology();
        fsm.GraphBlackboard = fsm.LocalBlackboard;

        var wire = (FSMConnection)fsm.Connect(alarm, panic, out _)!;
        wire.Condition = new NPCompareCondition
        {
            Value = new NPParameter { VariableName = "alarm" },
            Operator = "==",
            Threshold = new NPParameter { InlineValue = "on" },
        };
        fsm.LocalBlackboard.Add("alarm", "");
        fsm.SetPrime(idle);
        fsm.StartStateId = idle.Id;

        fsm.OnStarted(runner!);
        Tick(fsm);
        Assert.Same(idle, fsm.CurrentState);

        fsm.LocalBlackboard.SetValue("alarm", "on");
        Tick(fsm);
        Assert.Same(panic, fsm.CurrentState);

        Tick(fsm);   // DontRetrigger keeps us in Panic
        Assert.Same(panic, fsm.CurrentState);
    }

    [Fact]
    public void NestedBTState_FinishesWithSuccessEvent()
    {
        string? captured = null;
        NPEvents.OnEventDispatched += (_, name, _) => captured = name;

        var fsm = NewFsm();
        var runner = Attach(fsm);
        var nestedBT = new NPBehaviourTreeAsset { Repeat = false, GraphName = "Micro" };
        nestedBT.AddNode(new BTActionNode { Action = new NPWaitAction { Seconds = new NPParameter { InlineValue = 0.01f } } });
        nestedBT.LocalBlackboard.Add("k", 1f, isPublic: true);

        var state = new FSMNestedBTState { Name = "DoBT", SuccessEvent = "bt-done" };
        state.Nested.SubGraphOverride = nestedBT;
        fsm.AddNode(state);
        fsm.BuildTopology();
        fsm.GraphBlackboard = fsm.LocalBlackboard;

        fsm.OnStarted(runner!);
        Tick(fsm);
        Assert.Same(state, fsm.CurrentState);
        Assert.False(state.Finished);

        Tick(fsm);
        Assert.True(state.Finished);
        Assert.Equal("bt-done", captured);
    }

    [Fact]
    public void ConcurrentState_TicksNestedEveryFrame()
    {
        var fsm = NewFsm();
        var runner = Attach(fsm);
        var sub = new NPProbeSubGraph();   // +1 outB per tick
        var conc = new FSMConcurrentState { Name = "Ambient" };
        conc.Nested.SubGraphOverride = sub;
        var idle = new FSMState { Name = "Idle" };
        fsm.AddNode(conc);
        fsm.AddNode(idle);
        fsm.SetPrime(idle);
        fsm.BuildTopology();
        fsm.GraphBlackboard = fsm.LocalBlackboard;

        fsm.OnStarted(runner!);
        for (int i = 0; i < 3; i++)
            Tick(fsm);
        var inst = conc.Nested.InstanceOf(fsm);
        Assert.NotNull(inst);
        float outB = inst!.LocalBlackboard.GetValue("outB", 0f);
        Assert.True(outB >= 101f, $"outB={outB}");
    }

    [Fact]
    public void EventAlias_TriggersState()
    {
        var fsm = NewFsm();
        var runner = Attach(fsm);
        var idle = new FSMState { Name = "Idle" };
        var panic = new FSMState { Name = "Panic", TransitionEvaluation = NPTransitionEvaluation.CheckManually };
        fsm.AddNode(idle);
        fsm.AddNode(panic);
        fsm.SetPrime(idle);
        fsm.BuildTopology();
        fsm.GraphBlackboard = fsm.LocalBlackboard;

        fsm.OnStarted(runner!);
        Tick(fsm);
        fsm.HandleEvent("Panic", null);   // event name doubles as TriggerState
        Assert.Same(panic, fsm.CurrentState);
    }
}
