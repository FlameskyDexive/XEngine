// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using XEngine.Async;
using Xunit;

namespace XEngine.Runtime.Test.XTaskTests;

/// <summary>
/// Tests the time-based and condition XTask operators (NextFrame, DelayFrame, Delay, WaitUntil,
/// WaitWhile, WaitUntilCanceled, CancelAfterSlim). The frame loop is pumped manually and
/// <see cref="Runtime.Time.TimeStack"/> is populated where DeltaTime-based timing matters.
/// </summary>
public class XTaskDelayAndConditionTests
{
    public XTaskDelayAndConditionTests()
    {
        XTaskLoopHelper.Initialize(Environment.CurrentManagedThreadId);
    }

    // ---- NextFrame ----

    [Fact]
    public void NextFrame_CompletesOnFirstTickOfChosenPhase()
    {
        XTask t = XTask.NextFrame(FrameTiming.Update);
        Assert.False(t.IsCompleted);

        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.True(t.IsCompleted);
    }

    [Fact]
    public async System.Threading.Tasks.Task NextFrame_AwaitedInCoroutine_ResumesNextFrame()
    {
        var tcs = AutoResetXTaskCompletionSource.Create();
        int after = 0;
        NextFrameCoroutine(tcs, () => after = 1).Forget();

        XTaskLoopHelper.Tick(FrameTiming.Update); // resumes past NextFrame, now awaiting tcs.
        Assert.Equal(0, after);

        tcs.TrySetResult();
        await System.Threading.Tasks.Task.Yield();
        Assert.Equal(1, after);
    }

    private static async XTaskVoid NextFrameCoroutine(AutoResetXTaskCompletionSource gate, Action onDone)
    {
        await XTask.NextFrame(FrameTiming.Update);
        await gate.Task;
        onDone();
    }

    // ---- DelayFrame ----

    [Fact]
    public void DelayFrame_CompletesAfterFrameCount()
    {
        XTask t = XTask.DelayFrame(3, FrameTiming.Update);
        Assert.False(t.IsCompleted);

        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.False(t.IsCompleted); // 2 remaining.
        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.False(t.IsCompleted); // 1 remaining.
        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.True(t.IsCompleted);  // 0 remaining → done.
    }

    [Fact]
    public void DelayFrame_ZeroOrNegative_CompletesSynchronously()
    {
        Assert.True(XTask.DelayFrame(0).IsCompleted);
        Assert.True(XTask.DelayFrame(-1).IsCompleted);
    }

    // ---- Delay (Realtime — no engine Time dependency, testable directly) ----

    [Fact]
    public async System.Threading.Tasks.Task Delay_Realtime_ElapsesAfterWallClockTime()
    {
        XTask t = XTask.Delay(TimeSpan.FromMilliseconds(60), DelayType.Realtime, FrameTiming.Update);
        Assert.False(t.IsCompleted);

        // Pump frames until it elapses (the loop polls the stopwatch each tick; ticks here are
        // synthetic but the stopwatch measures real time, so a brief wait + a few ticks completes it).
        for (int i = 0; i < 50 && !t.IsCompleted; i++)
        {
            await System.Threading.Tasks.Task.Delay(5);
            XTaskLoopHelper.Tick(FrameTiming.Update);
        }

        Assert.True(t.IsCompleted);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delay_DeltaTime_AccumulatesFromTimeStack()
    {
        // Push a fake TimeData so Time.DeltaTime returns 0.05s per frame.
        var time = new Runtime.TimeData { DeltaTime = 0.05f };
        Runtime.Time.TimeStack.Push(time);
        try
        {
            XTask t = XTask.Delay(TimeSpan.FromSeconds(0.2), DelayType.DeltaTime, FrameTiming.Update);
            Assert.False(t.IsCompleted);

            XTaskLoopHelper.Tick(FrameTiming.Update); // 0.05
            Assert.False(t.IsCompleted);
            XTaskLoopHelper.Tick(FrameTiming.Update); // 0.10
            Assert.False(t.IsCompleted);
            XTaskLoopHelper.Tick(FrameTiming.Update); // 0.15
            Assert.False(t.IsCompleted);
            XTaskLoopHelper.Tick(FrameTiming.Update); // 0.20 → done
            Assert.True(t.IsCompleted);
        }
        finally
        {
            Runtime.Time.TimeStack.Pop();
        }
    }

    // ---- WaitUntil / WaitWhile ----

    [Fact]
    public void WaitUntil_CompletesWhenPredicateTrue()
    {
        int threshold = 3;
        int counter = 0;
        XTask t = XTask.WaitUntil(() => counter >= threshold, FrameTiming.Update);

        for (int i = 0; i < threshold - 1; i++)
        {
            counter++;
            XTaskLoopHelper.Tick(FrameTiming.Update);
            Assert.False(t.IsCompleted);
        }

        counter++;
        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.True(t.IsCompleted);
    }

    [Fact]
    public void WaitWhile_CompletesWhenPredicateFalse()
    {
        bool busy = true;
        XTask t = XTask.WaitWhile(() => busy, FrameTiming.Update);

        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.False(t.IsCompleted);

        busy = false;
        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.True(t.IsCompleted);
    }

    // ---- Cancellation ----

    [Fact]
    public async System.Threading.Tasks.Task Delay_CancelViaToken_ThrowsOCE()
    {
        using var cts = new CancellationTokenSource();
        XTask t = XTask.DelayFrame(100, FrameTiming.Update, cts.Token);

        cts.Cancel();
        XTaskLoopHelper.Tick(FrameTiming.Update); // promise observes cancellation on next poll.

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await t);
    }

    [Fact]
    public async System.Threading.Tasks.Task Delay_AlreadyCanceledToken_CompletesSynchronouslyCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        XTask t = XTask.DelayFrame(5, FrameTiming.Update, cts.Token);
        Assert.True(t.IsCompleted);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await t);
    }

    [Fact]
    public void WaitUntilCanceled_Polling_CompletesWhenTokenCancels()
    {
        using var cts = new CancellationTokenSource();
        XTask t = XTask.WaitUntilCanceled(cts.Token, FrameTiming.Update);
        Assert.False(t.IsCompleted);

        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.False(t.IsCompleted);

        cts.Cancel();
        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.True(t.IsCompleted);
    }

    [Fact]
    public async System.Threading.Tasks.Task CancelAfterSlim_FiresOnFrameLoop()
    {
        // CancelAfterSlim uses UnscaledDeltaTime; when the engine isn't ticking Time that is 0, and
        // the promise falls back to a 16ms-per-frame clock. A 1-frame-equivalent delay (>= 16ms) with
        // a couple of synthetic ticks triggers cancel reliably.
        using var cts = new CancellationTokenSource();
        cts.CancelAfterSlim(16, FrameTiming.Update);

        Assert.False(cts.IsCancellationRequested);
        XTaskLoopHelper.Tick(FrameTiming.Update);
        // First tick advances the synthetic clock by ~16ms; a second guarantees crossing threshold.
        if (!cts.IsCancellationRequested) XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.True(cts.IsCancellationRequested);
    }
}
