using System.Collections.Generic;

using XEngine.NodePlay;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>Probe module: counts lifecycle hooks so runner semantics are observable headlessly.</summary>
public sealed class NPProbeRuntimeGraph : NPGraphAsset
{
    public int StartedCount;
    public int PausedFlips;
    public int StoppedCount;
    public int TickCount;
    public readonly List<string> HandledEvents = new();

    public NPProbeRuntimeGraph()
    {
        Module = "bt";
        GraphName = "Probe Runtime";
        AddNode(new NPRelayNode { Name = "Root" });
        LocalBlackboard.Add("speed", 1f, isPublic: true);
    }

    protected internal override void OnStarted(NPRunner runner) => StartedCount++;
    protected internal override void OnPaused(NPRunner runner, bool paused) => PausedFlips++;
    protected internal override void OnStopped(NPRunner runner, bool success) => StoppedCount++;
    protected internal override void TickRunner(float delta) => TickCount++;
    protected internal override void HandleEvent(string name, object? value) => HandledEvents.Add(name);
}

/// <summary>M4: owner lifecycle, manual ticking, event queueing/broadcast.</summary>
public sealed class NodePlayRunnerTests
{
    private static NPRunner NewRunner(out NPProbeRuntimeGraph graph)
    {
        var go = new GameObject("RunnerProbe");
        var runner = go.AddComponent<NPRunner>();
        runner.OnEnableAction = NPRunAction.DoNothing;   // drive everything manually
        runner.OnDisableAction = NPRunAction.DoNothing;
        graph = new NPProbeRuntimeGraph();
        runner.SetOverrideGraph(graph);
        return runner;
    }

    [Fact]
    public void Lifecycle_StartTickPauseResumeStop()
    {
        NPRunner runner = NewRunner(out NPProbeRuntimeGraph graph);

        Assert.False(runner.IsRunning);
        Assert.True(runner.StartBehaviour());

        // Override sources run as per-agent CLONES; lifecycle hooks fire on the running instance.
        var inst = (NPProbeRuntimeGraph)runner.RunningGraph!;
        Assert.NotSame(graph, inst);
        Assert.Equal(1, inst.StartedCount);

        runner.UpdateBehaviour(1f);
        runner.UpdateBehaviour(1f);
        Assert.Equal(2, inst.TickCount);
        Assert.Same(runner, inst.OwnerRunner);

        runner.PauseBehaviour();
        runner.UpdateBehaviour(1f);
        Assert.Equal(2, inst.TickCount);           // paused gate holds
        Assert.Equal(1, inst.PausedFlips);

        runner.ResumeBehaviour();
        runner.UpdateBehaviour(1f);
        Assert.Equal(3, inst.TickCount);
        Assert.Equal(2, inst.PausedFlips);         // paused=false flip counted too

        runner.StopBehaviour();
        Assert.False(runner.IsRunning);
        Assert.Null(runner.RunningGraph);
        Assert.Equal(1, inst.StoppedCount);
        Assert.NotSame(inst, runner.RunningGraph);
    }

    [Fact]
    public void Events_QueuedUntilNextTickAndGlobalFanOut()
    {
        NPRunner a = NewRunner(out NPProbeRuntimeGraph ga);
        NPRunner b = NewRunner(out NPProbeRuntimeGraph gb);
        a.StartBehaviour();
        b.StartBehaviour();
        var ia = (NPProbeRuntimeGraph)a.RunningGraph!;
        var ib = (NPProbeRuntimeGraph)b.RunningGraph!;

        a.SendEvent("local", null);
        Assert.Empty(ia.HandledEvents);            // consumed at step boundary, not immediately

        a.UpdateBehaviour(0f);
        b.UpdateBehaviour(0f);
        Assert.Single(ia.HandledEvents);

        NPEvents.SendGlobalEvent("alarm");
        a.UpdateBehaviour(0f);
        b.UpdateBehaviour(0f);
        Assert.Contains("alarm", ia.HandledEvents);
        Assert.Contains("alarm", ib.HandledEvents);
    }

    [Fact]
    public void Restart_ReplacesOverrideInstanceCleanly()
    {
        NPRunner runner = NewRunner(out NPProbeRuntimeGraph first);
        runner.StartBehaviour();
        runner.StopBehaviour();

        var second = new NPProbeRuntimeGraph();
        runner.SetOverrideGraph(second);           // swaps source between runs
        runner.StartBehaviour();
        var inst = runner.RunningGraph!;
        Assert.IsType<NPProbeRuntimeGraph>(inst);
        Assert.NotSame(first, second);
        Assert.NotSame(second, inst);              // still a fresh clone of the new source
    }

    [Fact]
    public void ParameterOverrides_PushIntoGraphScopeAtStart()
    {
        NPRunner runner = NewRunner(out _);
        runner.ParameterOverrides.Add(new NPParameterOverride { VariableName = "speed", Value = 9f });
        runner.ParameterOverrides.Add(new NPParameterOverride { VariableName = "ghost", Value = 1f }); // warns, ignored

        runner.StartBehaviour();
        var inst = (NPProbeRuntimeGraph)runner.RunningGraph!;
        Assert.Equal(9f, inst.LocalBlackboard.GetValue("speed", 0f));  // preset applied over asset default(1)
    }
}
