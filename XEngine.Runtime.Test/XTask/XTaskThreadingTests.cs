// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using System.Threading.Tasks;
using XEngine.Async;
using Xunit;

namespace XEngine.Runtime.Test.XTaskTests;

/// <summary>Tests the threading primitives: SwitchToMainThread, SwitchToThreadPool, Post, and the
/// opt-in <see cref="XTaskSynchronizationContext"/>.</summary>
public class XTaskThreadingTests
{
    public XTaskThreadingTests()
    {
        XTaskLoopHelper.Initialize(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void SwitchToMainThread_AlreadyOnMain_CompletesSynchronously()
    {
        bool calledIsCompleted = XTask.SwitchToMainThread().GetAwaiter().IsCompleted;
        Assert.True(calledIsCompleted); // test runs on the thread XTaskLoopHelper was initialized on.
    }

    [Fact]
    public async System.Threading.Tasks.Task SwitchToThreadPool_RunsOffMainThread()
    {
        int callerThread = Environment.CurrentManagedThreadId;
        int resumeThread = -1;

        await XTask.SwitchToThreadPool();

        resumeThread = Environment.CurrentManagedThreadId;
        Assert.NotEqual(callerThread, resumeThread);
    }

    [Fact]
    public void SwitchToMainThread_ReturnsFromThreadPool()
    {
        int mainThread = Environment.CurrentManagedThreadId;
        var done = new TaskCompletionSource<bool>();
        int observedThread = -1;
        int sawOnPool = 0;

        // Fire-and-forget the round-trip coroutine. It hops to the threadpool (runs onPool there),
        // then registers a SwitchToMainThread continuation on the Update queue. The test pumps Update
        // synchronously on this main thread (Thread.Sleep, NOT await Task.Delay which would resume on
        // a pool thread) so the SwitchToMain continuation fires here, on thread `mainThread`.
        RunRoundTrip(() => { observedThread = Environment.CurrentManagedThreadId; done.TrySetResult(true); },
                     () => Interlocked.Increment(ref sawOnPool)).Forget();

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            XTaskLoopHelper.Tick(FrameTiming.Update);
            if (!done.Task.IsCompleted)
            {
                Thread.Sleep(2); // synchronous: stays on the main thread while the pool leg runs
            }
        }

        Assert.True(done.Task.IsCompleted, "Round-trip coroutine did not complete within the timeout.");
        Assert.Equal(1, sawOnPool);
        Assert.Equal(mainThread, observedThread);
    }

    private static async XTaskVoid RunRoundTrip(Action onResumed, Action onPool)
    {
        await XTask.SwitchToThreadPool();
        onPool();
        // On the threadpool now; SwitchToMainThread registers a continuation on the Update queue.
        await XTask.SwitchToMainThread();
        onResumed();
    }

    [Fact]
    public void Post_FiresOnNextTick()
    {
        bool fired = false;
        XTask.Post(() => fired = true, FrameTiming.Update);
        Assert.False(fired);
        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.True(fired);
    }

    [Fact]
    public async System.Threading.Tasks.Task Post_RoutesToChosenPhase()
    {
        bool lateFired = false;
        XTask.Post(() => lateFired = true, FrameTiming.LateUpdate);
        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.False(lateFired); // not the LateUpdate phase yet.
        XTaskLoopHelper.Tick(FrameTiming.LateUpdate);
        Assert.True(lateFired);
    }

    [Fact]
    public async System.Threading.Tasks.Task SyncContext_Install_Post_MarshalsToFrameLoop()
    {
        // Install the context on this (main) thread, post via the standard SynchronizationContext
        // API, then pump the Update phase — the posted work should fire there.
        SynchronizationContext? previous = XTaskSynchronizationContext.Install(FrameTiming.Update);
        try
        {
            Assert.NotNull(XTaskSynchronizationContext.Installed);

            bool fired = false;
            SynchronizationContext.Current!.Post(_ => fired = true, null);
            Assert.False(fired); // queued, not yet pumped.

            XTaskLoopHelper.Tick(FrameTiming.Update);
            Assert.True(fired);
        }
        finally
        {
            XTaskSynchronizationContext.Uninstall();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void SyncContext_Uninstall_ClearsAmbient()
    {
        XTaskSynchronizationContext.Install(FrameTiming.Update);
        Assert.NotNull(XTaskSynchronizationContext.Installed);

        XTaskSynchronizationContext.Uninstall();
        Assert.Null(XTaskSynchronizationContext.Installed);
        Assert.Null(SynchronizationContext.Current);
    }
}
