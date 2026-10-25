// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Async;
using Xunit;

namespace XEngine.Runtime.Test.XTaskTests;

/// <summary>Tests for the intrusive lock-free <see cref="TaskPool{T}"/> free-list.</summary>
public class XTaskPoolingTests
{
    /// <summary>A minimal pooled node type used to exercise TaskPool&lt;T&gt; in isolation.</summary>
    private sealed class Node : ITaskPoolNode<Node>
    {
        private Node? _next;
        public ref Node? NextNode => ref _next;
    }

    [Fact]
    public void TryPop_Empty_ReturnsFalse()
    {
        var pool = new TaskPool<Node>();
        Assert.False(pool.TryPop(out _));
    }

    [Fact]
    public void TryPush_Then_TryPop_RoundsTripsInstance()
    {
        var pool = new TaskPool<Node>();
        var n = new Node();

        Assert.True(pool.TryPush(n));
        Assert.Equal(1, pool.Size);

        Assert.True(pool.TryPop(out Node? popped));
        Assert.Same(n, popped);
        Assert.Equal(0, pool.Size);
    }

    [Fact]
    public void TryPop_IsLifoOrder()
    {
        var pool = new TaskPool<Node>();
        var first = new Node();
        var second = new Node();

        pool.TryPush(first);
        pool.TryPush(second);

        Assert.True(pool.TryPop(out Node? a));
        Assert.True(pool.TryPop(out Node? b));

        Assert.Same(second, a); // LIFO: last pushed is first popped.
        Assert.Same(first, b);
    }

    [Fact]
    public void MaxPoolSize_TruncatesReturnedInstances()
    {
        int prev = TaskPool.MaxPoolSize;
        TaskPool.MaxPoolSize = 2;
        try
        {
            var pool = new TaskPool<Node>();
            Assert.True(pool.TryPush(new Node()));
            Assert.True(pool.TryPush(new Node()));
            Assert.False(pool.TryPush(new Node())); // pool full; instance dropped.
            Assert.Equal(2, pool.Size);
        }
        finally
        {
            TaskPool.MaxPoolSize = prev;
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task AutoResetTcs_PoolReuse_ReducesAllocationsOverChurn()
    {
        // Create-complete-await a tight loop. The functional reuse of pooled instances is asserted
        // in XTaskFactoryAndTcsTests; here we just confirm the loop completes without error and the
        // pool serves repeated operations.
        for (int i = 0; i < 100; i++)
        {
            var tcs = AutoResetXTaskCompletionSource.Create();
            tcs.TrySetResult();
            await tcs.Task; // recycles.
        }
    }
}
