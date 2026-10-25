// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using XEngine.Async;
using Xunit;

namespace XEngine.Runtime.Test.XTaskTests;

/// <summary>Tests for <see cref="XTask.WhenAll"/> and <see cref="XTask.WhenAny"/>.</summary>
public class XTaskCombinatorTests
{
    public XTaskCombinatorTests()
    {
        XTaskLoopHelper.Initialize(Environment.CurrentManagedThreadId);
    }

    // ---- WhenAll ----

    [Fact]
    public async System.Threading.Tasks.Task WhenAll_Empty_CompletesSynchronously()
    {
        XTask t = XTask.WhenAll();
        Assert.True(t.IsCompleted);
        await t;
    }

    [Fact]
    public async System.Threading.Tasks.Task WhenAll_AllSynchronous_CompletesImmediately()
    {
        XTask t = XTask.WhenAll(XTask.CompletedTask, XTask.CompletedTask, XTask.CompletedTask);
        Assert.True(t.IsCompleted);
        await t;
    }

    [Fact]
    public async System.Threading.Tasks.Task WhenAll_Typed_ReturnsAllResultsInOrder()
    {
        XTask<int[]> t = XTask.WhenAll(XTask.FromResult(1), XTask.FromResult(2), XTask.FromResult(3));
        Assert.True(t.IsCompleted);

        int[] results = await t;
        Assert.Equal(new[] { 1, 2, 3 }, results);
    }

    [Fact]
    public async System.Threading.Tasks.Task WhenAll_WaitsForAllPending_ToComplete()
    {
        var a = AutoResetXTaskCompletionSource.Create();
        var b = AutoResetXTaskCompletionSource.Create();
        XTask all = XTask.WhenAll(a.Task, b.Task);

        Assert.False(all.IsCompleted);
        a.TrySetResult();
        Assert.False(all.IsCompleted); // still waiting on b.
        b.TrySetResult();
        Assert.True(all.IsCompleted);
        await all;
    }

    [Fact]
    public async System.Threading.Tasks.Task WhenAll_PropagatesFirstException()
    {
        var boom = new InvalidOperationException("boom");
        XTask t = XTask.WhenAll(XTask.CompletedTask, XTask.FromException(boom));
        Assert.True(t.IsCompleted);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await t);
        Assert.Same(boom, thrown);
    }

    [Fact]
    public async System.Threading.Tasks.Task WhenAll_MixedSyncAndPending_CompletesWhenAllDone()
    {
        var pending = AutoResetXTaskCompletionSource<int>.Create();
        XTask<int[]> t = XTask.WhenAll(XTask.FromResult(10), pending.Task, XTask.FromResult(30));
        Assert.False(t.IsCompleted);

        pending.TrySetResult(20);
        Assert.True(t.IsCompleted);
        int[] results = await t;
        Assert.Equal(new[] { 10, 20, 30 }, results);
    }

    // ---- WhenAny ----

    [Fact]
    public async System.Threading.Tasks.Task WhenAny_AllSynchronous_ReturnsFirstIndex()
    {
        XTask<int> t = XTask.WhenAny(XTask.CompletedTask, XTask.CompletedTask);
        Assert.True(t.IsCompleted);
        int winner = await t;
        Assert.Equal(0, winner); // first synchronous task wins.
    }

    [Fact]
    public async System.Threading.Tasks.Task WhenAny_Typed_ReturnsWinnerIndexAndResult()
    {
        var slow = AutoResetXTaskCompletionSource<int>.Create();
        var fast = AutoResetXTaskCompletionSource<int>.Create();
        XTask<WhenAnyResult<int>> t = XTask.WhenAny(slow.Task, fast.Task);

        Assert.False(t.IsCompleted);
        fast.TrySetResult(42);
        Assert.True(t.IsCompleted);

        WhenAnyResult<int> result = await t;
        Assert.Equal(1, result.Index); // fast (index 1) won.
        Assert.Equal(42, result.Result);
    }

    [Fact]
    public async System.Threading.Tasks.Task WhenAny_SecondCompleterIsIgnored()
    {
        var first = AutoResetXTaskCompletionSource.Create();
        var second = AutoResetXTaskCompletionSource.Create();
        XTask<int> t = XTask.WhenAny(first.Task, second.Task);

        first.TrySetResult();
        int winner = await t;
        Assert.Equal(0, winner);

        // Second completing later must not change the result.
        second.TrySetResult();
        Assert.True(t.IsCompleted);
    }

    [Fact]
    public async System.Threading.Tasks.Task WhenAny_FaultedTask_WinsWithException()
    {
        var slow = AutoResetXTaskCompletionSource.Create();
        XTask<int> t = XTask.WhenAny(slow.Task, XTask.FromException(new FormatException("x")));

        Assert.True(t.IsCompleted);
        await Assert.ThrowsAsync<FormatException>(async () => await t);
    }

    [Fact]
    public async System.Threading.Tasks.Task WhenAny_WithDelay_RacePattern()
    {
        using var cts = new CancellationTokenSource();
        var gate = AutoResetXTaskCompletionSource.Create();

        // Classic timeout pattern: race a gate against a timeout delay. The delay (Realtime, tiny)
        // wins because the gate never completes.
        XTask<int> any = XTask.WhenAny(gate.Task, XTask.Delay(TimeSpan.FromMilliseconds(30), DelayType.Realtime));

        for (int i = 0; i < 60 && !any.IsCompleted; i++)
        {
            Thread.Sleep(2);
            XTaskLoopHelper.Tick(FrameTiming.Update);
        }

        Assert.True(any.IsCompleted);
        int winner = await any;
        Assert.Equal(1, winner); // the delay (index 1) won.
    }
}
