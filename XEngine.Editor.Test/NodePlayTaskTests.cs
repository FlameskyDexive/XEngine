using XEngine.NodePlay;

using Xunit;

namespace XEngine.Editor.Test;

// ---- probes ----------------------------------------------------------------

public sealed class NPProbeTimedAction : NPActionTask
{
    public int Ticks = 1;
    public bool Succeed = true;
    public int TickCount;
    public int Begins;
    public bool WasInterrupted;

    protected override void OnExecute()
    {
        Begins++;
        TickCount++;
        if (Ticks <= 1)
            EndAction(Succeed);
    }

    protected override void OnUpdate()
    {
        TickCount++;
        if (TickCount >= Ticks)
            EndAction(Succeed);
    }

    protected override void OnStop(bool interrupted) => WasInterrupted = interrupted;

    protected override void OnReset()
    {
        TickCount = 0;
        Begins = 0;
        WasInterrupted = false;
    }

    /// <summary>Drive until terminal, capped.</summary>
    public void Run(int cap = 16)
    {
        for (int i = 0; i < cap && Result == null; i++)
            Tick();
    }
}

public sealed class NPProbeBoolCondition : NPConditionTask
{
    public bool Answer = true;
    public int CheckCount;

    protected override bool OnCheck()
    {
        CheckCount++;
        return Answer;
    }
}

[NPName("Probe Bool", -9)]
public sealed class NPProbeNamedCondition : NPConditionTask
{
    protected override bool OnCheck() => true;
}

/// <summary>M2: cooperative action contract, condition contract, lists, init pipeline.</summary>
public sealed class NodePlayTaskTests
{
    [Fact]
    public void TimedAction_LifecycleSpansTicksThenSucceeds()
    {
        var task = new NPProbeTimedAction { Ticks = 3 };
        Assert.Null(task.Bind(null, null!, null!));

        Assert.False(task.IsRunning);
        task.Tick();                      // first tick -> OnExecute
        Assert.True(task.IsRunning);
        Assert.Null(task.Result);

        task.Tick();                      // still one short
        Assert.True(task.IsRunning);

        task.Tick();                      // reaches threshold -> Success
        Assert.False(task.IsRunning);
        Assert.True(task.Result);
        Assert.Equal(1, task.Begins);     // OnExecute exactly once
        Assert.Equal(3, task.TickCount);  // three graph ticks total
        Assert.False(task.WasInterrupted);

        // Reuse re-arms cleanly.
        task.ResetForReuse();
        task.Run();
        Assert.True(task.Result);
    }

    [Fact]
    public void InstantAction_EndsOnFirstTickAndAbortMarksInterrupted()
    {
        var instant = new NPProbeTimedAction { Ticks = 1 };
        instant.Tick();
        Assert.True(instant.Result);

        var aborted = new NPProbeTimedAction { Ticks = 5 };
        aborted.Bind(null, null!, null!);
        aborted.Tick();
        aborted.Abort();
        Assert.False(aborted.IsRunning);
        Assert.False(aborted.Result);
        Assert.True(aborted.WasInterrupted);

        // Double-abort is a no-op, does not fire OnStop again (WasInterrupted idempotent flag stays).
        aborted.Abort();
        Assert.False(aborted.Result);
    }

    [Fact]
    public void DisplayName_AttributesAndLabel()
    {
        Assert.Equal("Probe Bool", new NPProbeNamedCondition().DisplayName);
        Assert.Equal("NPProbe Bool Condition", new NPProbeBoolCondition().DisplayName);
        var labeled = new NPProbeTimedAction { Label = "Wait Ledge" };
        Assert.Equal("Wait Ledge", labeled.DisplayName);
        Assert.Equal("Utility", new NPActionList().Category);
    }

    [Fact]
    public void ConditionList_AllMustPass_InvertFlipsAndShortCircuits()
    {
        var yes = new NPProbeBoolCondition { Answer = true };
        var no = new NPProbeBoolCondition { Answer = false };
        var tail = new NPProbeBoolCondition();

        Assert.True(yes.Check());

        var all = new NPConditionList(yes, no, tail);
        Assert.Null(all.Bind(null, null!, null!));
        Assert.False(all.Check());            // fails at 'no'
        Assert.Equal(0, tail.CheckCount);     // never evaluated

        // Short circuit: a fresh list stops evaluating after the first failure.
        var head = new NPProbeBoolCondition { Answer = false };
        var behind = new NPProbeBoolCondition();
        Assert.False(new NPConditionList(head, behind).Check());
        Assert.Equal(0, behind.CheckCount);

        var passing = new NPConditionList(new NPProbeBoolCondition(), yes);
        Assert.True(passing.Check());

        var inverted = new NPConditionList(no) { Invert = true };
        Assert.True(inverted.Check());
    }

    [Fact]
    public void ActionList_SequentialChildrenWithFailurePropagation()
    {
        var first = new NPProbeTimedAction { Ticks = 2 };
        var boom = new NPProbeTimedAction { Ticks = 1, Succeed = false };
        var neverReached = new NPProbeTimedAction { Ticks = 1 };
        var list = new NPActionList { Actions = { first, boom, neverReached } };

        Assert.Null(list.Bind(null, null!, null!));
        Assert.Same(list.Agent, first.Agent);     // children inherit binding via list walk

        list.Tick();                              // starts first (running)
        Assert.True(first.IsRunning);
        Assert.True(list.IsRunning);

        first.Run();                              // finish first out-of-band is fine; next Tick advances
        list.ResetForReuse();
        for (int i = 0; i < 16 && list.Result == null; i++)
            list.Tick();                          // full pass: first succeeds, boom fails -> list fails
        Assert.False(list.Result);
        Assert.Equal(1, boom.Begins);
        Assert.Equal(0, neverReached.Begins);
    }
}
