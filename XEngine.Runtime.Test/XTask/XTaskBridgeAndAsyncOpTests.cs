// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading.Tasks;
using XEngine.Async;
using XEngine.Runtime.AssetBundles;
using Xunit;

namespace XEngine.Runtime.Test.XTaskTests;

/// <summary>Tests the Task/ValueTask bridges and the AsyncOperationBase.AsXTask() integration.</summary>
public class XTaskBridgeAndAsyncOpTests
{
    public XTaskBridgeAndAsyncOpTests()
    {
        XTaskLoopHelper.Initialize(Environment.CurrentManagedThreadId);
    }

    // ---- XTask -> Task / ValueTask ----

    [Fact]
    public async Task AsTask_CompletedXTask_YieldsCompletedTask()
    {
        Task t = XTask.CompletedTask.AsTask();
        Assert.True(t.IsCompleted);
        await t;
    }

    [Fact]
    public async Task AsTask_PendingXTask_CompletesWhenXTaskDoes()
    {
        var xcs = AutoResetXTaskCompletionSource.Create();
        Task t = xcs.Task.AsTask();
        Assert.False(t.IsCompleted);

        xcs.TrySetResult();
        await t; // completes.
    }

    [Fact]
    public async Task AsTask_FaultedXTask_PropagatesException()
    {
        var xcs = AutoResetXTaskCompletionSource.Create();
        Task t = xcs.Task.AsTask();
        xcs.TrySetException(new InvalidOperationException("bridge"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await t);
    }

    [Fact]
    public async Task AsValueTask_AlreadyCompleted_ReturnsCompletedValueTask()
    {
        ValueTask vt = XTask.CompletedTask.AsValueTask();
        Assert.True(vt.IsCompleted);
        await vt;
    }

    // ---- Task / ValueTask -> XTask ----

    [Fact]
    public async System.Threading.Tasks.Task AsXTask_FromCompletedTask_Completes()
    {
        XTask x = Task.CompletedTask.AsXTask();
        Assert.True(x.IsCompleted);
        await x;
    }

    [Fact]
    public async System.Threading.Tasks.Task AsXTask_FromFaultedTask_PropagatesException()
    {
        Task faulted = Task.FromException(new FormatException("src"));
        XTask x = faulted.AsXTask();
        Assert.True(x.IsCompleted);
        await Assert.ThrowsAsync<FormatException>(async () => await x);
    }

    [Fact]
    public async System.Threading.Tasks.Task AsXTask_FromValueTask_Completes()
    {
        XTask x = ValueTask.CompletedTask.AsXTask();
        Assert.True(x.IsCompleted);
        await x;
    }

    // ---- AsyncOperationBase.AsXTask() ----

    [Fact]
    public async System.Threading.Tasks.Task AsXTask_AlreadySucceeded_CompletesImmediately()
    {
        var op = new SyncSucceedOp();
        op.StartOperation();
        op.UpdateOperation(); // completes synchronously.

        XTask x = op.AsXTask();
        Assert.True(x.IsCompleted);
        await x;
    }

    [Fact]
    public async System.Threading.Tasks.Task AsXTask_AlreadyFailed_FaultsImmediately()
    {
        var op = new SyncFailOp("nope");
        op.StartOperation();
        op.UpdateOperation();

        XTask x = op.AsXTask();
        Assert.True(x.IsCompleted);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await x);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task AsXTask_PendingOp_CompletesWhenOpDoes()
    {
        var op = new CountingOp(3);
        op.StartOperation();

        XTask x = op.AsXTask();
        Assert.False(x.IsCompleted);

        op.UpdateOperation();
        Assert.False(x.IsCompleted);
        op.UpdateOperation();
        Assert.False(x.IsCompleted);
        op.UpdateOperation();
        Assert.True(x.IsCompleted); // 3rd update sets result.

        await x;
    }

    private sealed class SyncSucceedOp : AsyncOperationBase
    {
        protected override void InternalStart() { }
        protected override void InternalUpdate() => SetResult();
    }

    private sealed class SyncFailOp : AsyncOperationBase
    {
        private readonly string _error;
        public SyncFailOp(string error) { _error = error; }
        protected override void InternalStart() { }
        protected override void InternalUpdate() => SetError(_error);
    }

    private sealed class CountingOp : AsyncOperationBase
    {
        private int _remaining;
        public CountingOp(int updates) { _remaining = updates; }
        protected override void InternalStart() { }
        protected override void InternalUpdate()
        {
            if (--_remaining <= 0)
            {
                SetResult();
            }
        }
    }
}
