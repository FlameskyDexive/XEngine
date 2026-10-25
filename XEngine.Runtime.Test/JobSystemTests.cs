// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading;

using XEngine.Runtime.Jobs;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// P7 stage C: contract for the minimal data-parallel job system. Everything here is
/// deterministic — the parallel-for either covers every index exactly once or it does not.
/// </summary>
[Collection("JobSystem")]
public class JobSystemTests : IDisposable
{
    private readonly bool _wasEnabled = JobSystem.Enabled;

    public void Dispose() => JobSystem.Enabled = _wasEnabled;

    [Fact]
    public void ParallelFor_Visits_Every_Index_Exactly_Once()
    {
        JobSystem.Enabled = true;
        const int count = 10_000;
        int[] visits = new int[count];

        JobSystem.ParallelFor(count, 128, (start, end) =>
        {
            for (int i = start; i < end; i++)
                Interlocked.Increment(ref visits[i]);
        }).Complete();

        for (int i = 0; i < count; i++)
            Assert.Equal(1, visits[i]);
    }

    [Fact]
    public void ParallelFor_Chunks_Are_Contiguous_And_Disjoint()
    {
        JobSystem.Enabled = true;
        const int count = 5_000;
        const int batch = 300;
        var ranges = new List<(int Start, int End)>();

        JobSystem.ParallelFor(count, batch, (start, end) =>
        {
            lock (ranges)
                ranges.Add((start, end));
        }).Complete();

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        Assert.Equal((count + batch - 1) / batch, ranges.Count);
        int cursor = 0;
        foreach ((int start, int end) in ranges)
        {
            Assert.Equal(cursor, start);
            Assert.True(end > start);
            cursor = end;
        }
        Assert.Equal(count, cursor);
    }

    [Fact]
    public void ParallelFor_Actually_Uses_Worker_Threads()
    {
        if (Environment.ProcessorCount < 2)
            return;
        JobSystem.Enabled = true;
        var threads = new HashSet<int>();

        // Each chunk parks briefly so the pool has a reason to spread the work out.
        JobSystem.ParallelFor(4096, 64, (start, end) =>
        {
            lock (threads)
                threads.Add(Environment.CurrentManagedThreadId);
            Thread.SpinWait(2000);
        }).Complete();

        Assert.True(threads.Count > 1, $"All chunks ran on one thread ({threads.Count}).");
    }

    [Fact]
    public void Disabled_JobSystem_Runs_Inline_On_The_Caller()
    {
        JobSystem.Enabled = false;
        int callerThread = Environment.CurrentManagedThreadId;
        var observed = new HashSet<int>();

        JobHandle handle = JobSystem.ParallelFor(4096, 16, (start, end) =>
        {
            lock (observed)
                observed.Add(Environment.CurrentManagedThreadId);
        });

        Assert.Equal(JobHandle.Null, handle);
        Assert.Equal(new[] { callerThread }, observed);
    }

    [Fact]
    public void Single_Chunk_Work_Skips_The_Scheduler()
    {
        JobSystem.Enabled = true;
        int callerThread = Environment.CurrentManagedThreadId;
        int bodyThread = -1;

        JobHandle handle = JobSystem.ParallelFor(50, 128, (_, _) => bodyThread = Environment.CurrentManagedThreadId);

        Assert.Equal(JobHandle.Null, handle);
        Assert.Equal(callerThread, bodyThread);
    }

    [Fact]
    public void Complete_Rethrows_The_First_Worker_Exception()
    {
        JobSystem.Enabled = true;
        JobHandle handle = JobSystem.ParallelFor(4096, 64, (start, _) =>
        {
            if (start == 0)
                throw new InvalidOperationException("chunk zero exploded");
        });

        InvalidOperationException raised = Assert.Throws<InvalidOperationException>(handle.Complete);
        Assert.Contains("chunk zero exploded", raised.Message);
    }

    [Fact]
    public void Completing_A_Handle_Twice_Is_A_No_Op()
    {
        JobSystem.Enabled = true;
        int runs = 0;
        JobHandle handle = JobSystem.ParallelFor(2048, 64, (_, _) => Interlocked.Increment(ref runs));
        handle.Complete();
        int after = runs;
        handle.Complete();
        Assert.Equal(after, runs);
        Assert.True(handle.IsComplete);
    }

    [Fact]
    public void Dependencies_Order_Two_Batches()
    {
        JobSystem.Enabled = true;
        var order = new List<string>();
        var gate = new ManualResetEventSlim(false);

        JobHandle first = JobSystem.ParallelFor(2048, 64, (_, _) =>
        {
            gate.Wait();
            lock (order)
                if (order.Count == 0 || order[^1] != "first") order.Add("first");
        });
        JobHandle second = JobSystem.ParallelFor(2048, 64, (_, _) =>
        {
            lock (order)
                if (order.Count == 0 || order[^1] != "second") order.Add("second");
        }, dependency: first);

        // The dependent batch must not have started while `first` is parked on the gate.
        Thread.Sleep(60);
        lock (order)
            Assert.Empty(order);

        gate.Set();
        second.Complete();
        Assert.Equal(new[] { "first", "second" }, order);
        first.Complete();
        gate.Dispose();
    }

    [Fact]
    public void CombineDependencies_Waits_For_Both_Sides()
    {
        JobSystem.Enabled = true;
        int a = 0, b = 0;
        JobHandle first = JobSystem.ParallelFor(4096, 64, (_, _) => { Thread.SpinWait(5000); Interlocked.Increment(ref a); });
        JobHandle second = JobSystem.ParallelFor(4096, 64, (_, _) => { Thread.SpinWait(5000); Interlocked.Increment(ref b); });

        JobHandle both = JobHandle.CombineDependencies(first, second);
        both.Complete();

        Assert.Equal(64, a);
        Assert.Equal(64, b);
        Assert.True(first.IsComplete);
        Assert.True(second.IsComplete);
    }

    [Fact]
    public void CombineDependencies_With_Null_Returns_The_Other_Side()
    {
        Assert.Equal(JobHandle.Null, JobHandle.CombineDependencies(JobHandle.Null, JobHandle.Null));
        JobSystem.Enabled = true;
        JobHandle real = JobSystem.ParallelFor(2048, 64, (_, _) => { });
        Assert.Equal(real, JobHandle.CombineDependencies(real, JobHandle.Null));
        Assert.Equal(real, JobHandle.CombineDependencies(JobHandle.Null, real));
        real.Complete();
    }

    [Fact]
    public void The_Completing_Thread_Helps_Drain_Chunks()
    {
        if (Environment.ProcessorCount < 2)
            return;
        JobSystem.Enabled = true;
        JobSystem.ResetCountersForTests();

        JobSystem.ParallelFor(20_000, 32, (start, end) =>
        {
            for (int i = start; i < end; i++)
                Thread.SpinWait(20);
        }).Complete();

        Assert.True(JobSystem.CallerChunksExecuted > 0,
            "Complete() idled instead of running outstanding chunks.");
        Assert.True(JobSystem.WorkerChunksExecuted > 0,
            "No chunk reached a worker thread.");
    }

    [Fact]
    public void Batches_Are_Recycled_Without_Cross_Talk()
    {
        JobSystem.Enabled = true;
        // Hammer schedule/complete so the pool churns; each round must still cover its range
        // exactly once even though a previous round's queue entries may still be draining.
        for (int round = 0; round < 200; round++)
        {
            int[] visits = new int[2048];
            JobSystem.ParallelFor(visits.Length, 64, (start, end) =>
            {
                for (int i = start; i < end; i++)
                    Interlocked.Increment(ref visits[i]);
            }).Complete();
            for (int i = 0; i < visits.Length; i++)
                Assert.Equal(1, visits[i]);
        }
    }

    [Fact]
    public void Schedule_Runs_A_Single_Delegate()
    {
        JobSystem.Enabled = true;
        int value = 0;
        JobSystem.Schedule(() => Interlocked.Exchange(ref value, 42)).Complete();
        Assert.Equal(42, value);
    }

    [Fact]
    public void Empty_Range_Returns_A_Null_Handle()
    {
        JobSystem.Enabled = true;
        bool ran = false;
        JobHandle handle = JobSystem.ParallelFor(0, 16, (_, _) => ran = true);
        Assert.Equal(JobHandle.Null, handle);
        Assert.True(handle.IsComplete);
        handle.Complete();
        Assert.False(ran);
    }
}
