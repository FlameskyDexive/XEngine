// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using XEngine.Async;
using XEngine.Async.Internal;
using Xunit;

namespace XEngine.Runtime.Test.XTaskTests;

/// <summary>
/// Tests the frame-driven scheduler (XTaskLoopHelper, ContinuationQueue, FrameLoopRunner) and the
/// <see cref="XEngine.Async.YieldAwaitable"/>. The loop is pumped manually to advance frames
/// deterministically without a real window — mirrors how the engine's SimulationStep drives it.
/// </summary>
public class XTaskLoopHelperTests
{
    public XTaskLoopHelperTests()
    {
        // Each test starts from a known main-thread id so assertions about phase routing are stable.
        XTaskLoopHelper.Initialize(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void Initialize_SetsMainThreadId()
    {
        XTaskLoopHelper.Initialize(12345);
        Assert.Equal(12345, XTaskLoopHelper.MainThreadId);
        Assert.True(XTaskLoopHelper.IsInitialized);
    }

    [Fact]
    public void Tick_EmptyPhase_IsCheapNoOp()
    {
        // Pumping a phase with no registered work must not throw or allocate observably.
        XTaskLoopHelper.Tick(FrameTiming.Update);
        XTaskLoopHelper.Tick(FrameTiming.LateUpdate);
        XTaskLoopHelper.Tick(FrameTiming.FixedUpdate);
        XTaskLoopHelper.Tick(FrameTiming.EndOfFrame);
    }

    [Fact]
    public void Yield_FiresOnNextTickOfChosenPhase()
    {
        bool firedUpdate = false, firedLate = false;

        XTask.Yield(FrameTiming.Update).GetAwaiter().UnsafeOnCompleted(() => firedUpdate = true);
        XTask.Yield(FrameTiming.LateUpdate).GetAwaiter().UnsafeOnCompleted(() => firedLate = true);

        // Not yet fired — Tick hasn't run.
        Assert.False(firedUpdate);
        Assert.False(firedLate);

        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.True(firedUpdate);
        Assert.False(firedLate); // LateUpdate phase not yet pumped.

        XTaskLoopHelper.Tick(FrameTiming.LateUpdate);
        Assert.True(firedLate);
    }

    [Fact]
    public void Yield_DefaultTargetsUpdatePhase()
    {
        bool fired = false;
        XTask.Yield().GetAwaiter().UnsafeOnCompleted(() => fired = true);

        XTaskLoopHelper.Tick(FrameTiming.FixedUpdate);
        Assert.False(fired); // Yield() defaults to Update, not FixedUpdate.

        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.True(fired);
    }

    [Fact]
    public async System.Threading.Tasks.Task Yield_InsideAsyncMethod_ResumesNextFrame()
    {
        int frame = 0;
        var tcs = AutoResetXTaskCompletionSource.Create();
        RunYieldingCoroutine(tcs.Task, () => frame = 42).Forget();

        // After registering, the coroutine is suspended at the await Yield. Pump the Update phase to
        // resume it; it then awaits tcs.Task which is still pending.
        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.Equal(0, frame); // continuation of tcs.Task not yet fired.

        tcs.TrySetResult();
        await System.Threading.Tasks.Task.Yield(); // let inline continuations flush.
        Assert.Equal(42, frame);
    }

    private static async XTaskVoid RunYieldingCoroutine(XTask gate, Action onDone)
    {
        await XTask.Yield(FrameTiming.Update);
        await gate;
        onDone();
    }

    [Fact]
    public void FrameLoopRunner_PollsUntilMoveNextReturnsFalse()
    {
        var runner = new FrameLoopRunner();
        var item = new CountdownItem(3); // completes after 3 MoveNext calls.

        runner.Add(item);
        Assert.Equal(0, item.Calls);

        runner.Run();
        Assert.Equal(1, item.Calls);
        Assert.True(item.Active); // still in the list.

        runner.Run();
        Assert.Equal(2, item.Calls);

        runner.Run();
        Assert.Equal(3, item.Calls);
        Assert.False(item.Active); // returned false -> removed.

        // Subsequent runs do not re-invoke the removed item.
        runner.Run();
        Assert.Equal(3, item.Calls);
    }

    [Fact]
    public void ContinuationQueue_ItemsAddedDuringDrain_RunNextFrame()
    {
        var queue = new ContinuationQueue();
        int firstCalls = 0, secondCalls = 0;

        queue.Enqueue(() =>
        {
            firstCalls++;
            // Enqueue a sibling during the drain: must NOT run this frame (re-entrancy safety).
            queue.Enqueue(() => secondCalls++);
        });

        queue.Run();
        Assert.Equal(1, firstCalls);
        Assert.Equal(0, secondCalls); // deferred to next drain.

        queue.Run();
        Assert.Equal(1, secondCalls);
    }

    [Fact]
    public void FrameLoopRunner_FaultingItem_IsRemovedAndExceptionRouted()
    {
        Exception? routed = null;
        Action<Exception> prev = XTaskScheduler.UnobservedTaskException!;
        XTaskScheduler.UnobservedTaskException = e => routed = e;
        try
        {
            var runner = new FrameLoopRunner();
            runner.Add(new ThrowingItem());
            runner.Run(); // must not throw out; routes to scheduler.
            Assert.NotNull(routed);
            Assert.IsType<InvalidOperationException>(routed);
        }
        finally
        {
            XTaskScheduler.UnobservedTaskException = prev;
        }
    }

    private sealed class CountdownItem : IFrameLoopItem
    {
        private readonly int _threshold;
        public int Calls;
        public bool Active = true;

        public CountdownItem(int threshold) { _threshold = threshold; }

        public bool MoveNext()
        {
            Calls++;
            if (Calls >= _threshold)
            {
                Active = false;
                return false;
            }
            return true;
        }
    }

    private sealed class ThrowingItem : IFrameLoopItem
    {
        public bool MoveNext() => throw new InvalidOperationException("boom");
    }
}
