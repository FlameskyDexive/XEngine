// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using XEngine.Async;
using Xunit;

namespace XEngine.Runtime.Test.XTaskTests;

/// <summary>Tests for the pooled AutoReset TCS, the multi-await TCS, and the XTask factory helpers.</summary>
public class XTaskFactoryAndTcsTests
{
    // ---- AutoResetXTaskCompletionSource ----

    [Fact]
    public async System.Threading.Tasks.Task AutoReset_TrySetResult_AwaitsSuccessfully()
    {
        var tcs = AutoResetXTaskCompletionSource.Create();
        XTask task = tcs.Task;

        Assert.False(task.IsCompleted);
        tcs.TrySetResult();
        Assert.True(task.IsCompleted);

        await task; // consumes the result and recycles.
    }

    [Fact]
    public async System.Threading.Tasks.Task AutoReset_Typed_TrySetResult_ReturnsValue()
    {
        var tcs = AutoResetXTaskCompletionSource<string>.Create();
        tcs.TrySetResult("hello");
        string result = await tcs.Task;
        Assert.Equal("hello", result);
    }

    [Fact]
    public async System.Threading.Tasks.Task AutoReset_PoolReuse_ReturnsSameInstance()
    {
        AutoResetXTaskCompletionSource first = AutoResetXTaskCompletionSource.Create();
        first.TrySetResult();
        await first.Task; // recycles `first` back into the pool.

        AutoResetXTaskCompletionSource second = AutoResetXTaskCompletionSource.Create();
        Assert.Same(first, second); // pooled reuse.
    }

    [Fact]
    public async System.Threading.Tasks.Task AutoReset_RecycledInstance_WorksForFreshOperation()
    {
        var tcs = AutoResetXTaskCompletionSource.Create();
        tcs.TrySetResult();
        await tcs.Task; // recycles.

        // The recycled instance gets a fresh version token and serves a brand-new operation.
        var fresh = AutoResetXTaskCompletionSource.Create();
        Assert.Same(tcs, fresh); // pooled reuse.
        XTask task = fresh.Task;
        Assert.False(task.IsCompleted);
        fresh.TrySetResult();
        Assert.True(task.IsCompleted);
        await task;
    }

    [Fact]
    public async System.Threading.Tasks.Task AutoReset_CreateFromCanceled_YieldsCanceledTask()
    {
        var tcs = AutoResetXTaskCompletionSource.CreateFromCanceled(default, out _);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await tcs.Task);
    }

    // ---- XTaskCompletionSource (multi-await) ----

    [Fact]
    public async System.Threading.Tasks.Task MultiAwait_MultipleAwaitersAllFire()
    {
        var tcs = new XTaskCompletionSource();
        XTask a = tcs.Task;
        XTask b = tcs.Task;

        bool aDone = false, bDone = false;
        System.Threading.Tasks.Task ta = System.Threading.Tasks.Task.Run(async () => { await a; aDone = true; });
        System.Threading.Tasks.Task tb = System.Threading.Tasks.Task.Run(async () => { await b; bDone = true; });

        tcs.TrySetResult();
        await System.Threading.Tasks.Task.WhenAll(ta, tb);

        Assert.True(aDone);
        Assert.True(bDone);
    }

    [Fact]
    public async System.Threading.Tasks.Task MultiAwait_Typed_ReturnsValue()
    {
        var tcs = new XTaskCompletionSource<int>();
        tcs.TrySetResult(99);
        int v = await tcs.Task;
        Assert.Equal(99, v);
    }

    [Fact]
    public void MultiAwait_TrySetResult_Twice_SecondReturnsFalse()
    {
        var tcs = new XTaskCompletionSource();
        Assert.True(tcs.TrySetResult());
        Assert.False(tcs.TrySetResult());
    }

    // ---- Factory helpers ----

    [Fact]
    public void CompletedTask_IsCompleted()
    {
        Assert.True(XTask.CompletedTask.IsCompleted);
        Assert.Equal(XTaskStatus.Succeeded, XTask.CompletedTask.Status);
    }

    [Fact]
    public async System.Threading.Tasks.Task FromResult_HoldsValue_Synchronously()
    {
        XTask<int> t = XTask.FromResult(123);
        Assert.True(t.IsCompleted);
        Assert.Equal(123, await t);
    }

    [Fact]
    public async System.Threading.Tasks.Task FromException_Throws()
    {
        XTask t = XTask.FromException(new ApplicationException("x"));
        Assert.True(t.IsCompleted);
        await Assert.ThrowsAsync<ApplicationException>(async () => await t);
    }

    [Fact]
    public async System.Threading.Tasks.Task FromException_Typed_Throws()
    {
        XTask<int> t = XTask<int>.FromException(new ApplicationException("x"));
        Assert.True(t.IsCompleted);
        await Assert.ThrowsAsync<ApplicationException>(async () => await t);
    }

    [Fact]
    public async System.Threading.Tasks.Task FromCanceled_ThrowsOCE()
    {
        XTask t = XTask.FromCanceled();
        Assert.True(t.IsCompleted);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await t);
    }

    [Fact]
    public void Never_IsNeverCompleted()
    {
        Assert.False(XTask.Never.IsCompleted);
        Assert.Equal(XTaskStatus.Pending, XTask.Never.Status);
    }
}
