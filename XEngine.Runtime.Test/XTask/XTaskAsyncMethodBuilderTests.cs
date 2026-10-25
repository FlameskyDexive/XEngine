// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using XEngine.Async;
using Xunit;

namespace XEngine.Runtime.Test.XTaskTests;

/// <summary>
/// Tests the compiler-driven async XTask / XTask&lt;T&gt; / XTaskVoid plumbing: synchronous
/// completion is zero-allocation, suspension copies the state machine into a pooled runner, and
/// exceptions propagate correctly.
/// </summary>
public class XTaskAsyncMethodBuilderTests
{
    // ---- Synchronous-completion helpers (no await / no suspension) ----

    private static async XTask SyncVoidAsync()
    {
        // No await — runs to completion synchronously, never allocates a runner.
    }

    private static async XTask<int> SyncValueAsync()
    {
        return 42;
    }

    // ---- Suspending helpers ----

    private static async XTask SuspendingAsync(XTask inner)
    {
        await inner;
    }

    private static async XTask<int> SuspendingTypedAsync(XTask<int> inner)
    {
        int v = await inner;
        return v + 1;
    }

    private static async XTaskVoid FireAndForgetAsync(XTask inner, Action? onDone = null)
    {
        await inner;
        onDone?.Invoke();
    }

    private static async XTask ThrowingAsync()
    {
        await XTask.CompletedTask;
        throw new InvalidOperationException("from-async");
    }

    // ---- Tests ----

    [Fact]
    public async System.Threading.Tasks.Task SyncVoid_ReturnsCompletedTask()
    {
        XTask t = SyncVoidAsync();
        Assert.True(t.IsCompleted);
        await t;
    }

    [Fact]
    public async System.Threading.Tasks.Task SyncValue_ReturnsValue()
    {
        XTask<int> t = SyncValueAsync();
        Assert.True(t.IsCompleted);
        Assert.Equal(42, await t);
    }

    [Fact]
    public void SyncVoid_IsZeroAllocation()
    {
        // Use explicit GetAwaiter().GetResult() so the measurement isolates the XTask sync path
        // from the async-Task test-harness machinery. Warm up first.
        SyncVoidAsync().GetAwaiter().GetResult();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            SyncVoidAsync().GetAwaiter().GetResult();
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        // The zero-allocation property is a Release/JIT-inlined property. The Debug JIT boxes the
        // constrained interface call into the state machine and does not inline, so it allocates;
        // verify strictly only in Release.
#if !DEBUG
        Assert.Equal(0, after - before);
#endif
    }

    [Fact]
    public void SyncValue_IsZeroAllocation()
    {
        // Warm up.
        SyncValueAsync().GetAwaiter().GetResult();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            _ = SyncValueAsync().GetAwaiter().GetResult();
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

#if !DEBUG
        Assert.Equal(0, after - before);
#endif
    }

    [Fact]
    public async System.Threading.Tasks.Task Suspending_CompletesWhenInnerCompletes()
    {
        var tcs = AutoResetXTaskCompletionSource.Create();
        XTask outer = SuspendingAsync(tcs.Task);
        Assert.False(outer.IsCompleted);

        tcs.TrySetResult(); // fires the continuation inline, resuming the state machine.

        Assert.True(outer.IsCompleted);
        await outer;
    }

    [Fact]
    public async System.Threading.Tasks.Task SuspendingTyped_ReturnsTransformedValue()
    {
        var tcs = AutoResetXTaskCompletionSource<int>.Create();
        XTask<int> outer = SuspendingTypedAsync(tcs.Task);
        Assert.False(outer.IsCompleted);

        tcs.TrySetResult(41);

        Assert.True(outer.IsCompleted);
        Assert.Equal(42, await outer);
    }

    [Fact]
    public async System.Threading.Tasks.Task AsyncMethod_PropagatesException()
    {
        XTask t = ThrowingAsync();
        // The throw happens after the first await resumes; the resulting XTask faults.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await t);
        Assert.Equal("from-async", ex.Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task XTaskVoid_RunsAndRoutesExceptions()
    {
        Exception? routed = null;
        Action<Exception> prev = XTaskScheduler.UnobservedTaskException!;
        XTaskScheduler.UnobservedTaskException = e => routed = e;
        try
        {
            var tcs = AutoResetXTaskCompletionSource.Create();
            bool done = false;
            FireAndForgetAsync(tcs.Task, () => done = true).Forget();
            tcs.TrySetResult();

            // continuation fires inline on TrySetResult.
            Assert.True(done);
        }
        finally
        {
            XTaskScheduler.UnobservedTaskException = prev;
        }
        Assert.Null(routed);
    }

    [Fact]
    public async System.Threading.Tasks.Task XTaskVoid_SyncException_RoutedToScheduler()
    {
        Exception? routed = null;
        Action<Exception> prev = XTaskScheduler.UnobservedTaskException!;
        XTaskScheduler.UnobservedTaskException = e => routed = e;
        try
        {
            ThrowImmediatelyVoid().Forget();
        }
        finally
        {
            XTaskScheduler.UnobservedTaskException = prev;
        }
        Assert.NotNull(routed);
        Assert.IsType<InvalidOperationException>(routed);
    }

    private static async XTaskVoid ThrowImmediatelyVoid()
    {
        throw new InvalidOperationException("immediate");
    }

    [Fact]
    public async System.Threading.Tasks.Task ChainedAwaits_CompleteCorrectly()
    {
        var a = AutoResetXTaskCompletionSource.Create();
        var b = AutoResetXTaskCompletionSource.Create();

        XTask chained = ChainAsync(a.Task, b.Task);
        Assert.False(chained.IsCompleted);

        a.TrySetResult();
        Assert.False(chained.IsCompleted); // still waiting on b.

        b.TrySetResult();
        Assert.True(chained.IsCompleted);
        await chained;
    }

    private static async XTask ChainAsync(XTask a, XTask b)
    {
        await a;
        await b;
    }
}
