// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace XEngine.Runtime.Jobs;

/// <summary>
/// One unit of scheduled work. Batches are pooled, so every access from a worker has to prove it
/// is still looking at the generation it was handed. <see cref="Claim"/> packs the generation and
/// the chunk cursor into one 64-bit interlocked word precisely so that proof and the claim happen
/// in a single atomic step: a worker that wakes up late on a recycled batch fails the version
/// check inside the CAS and leaves without touching anything.
/// </summary>
internal sealed class JobBatch
{
    public Action<int, int>? RangeBody;
    public Action? Body;
    public int Count;
    public int BatchSize;
    public int ChunkCount;

    /// <summary>High 32 bits = generation, low 32 bits = next unclaimed chunk index.</summary>
    public long Claim;

    public int Version;
    public int RemainingChunks;
    public bool Dispatched;

    public readonly ManualResetEventSlim Done = new(false);
    public ExceptionDispatchInfo? Error;

    public int PendingDependencies;
    public readonly List<JobBatch> Dependents = new();
    public readonly object DependencyGate = new();

    public bool IsCompleted(int version) => Volatile.Read(ref Version) != version || Done.IsSet;

    public void Reset(int version)
    {
        RangeBody = null;
        Body = null;
        Count = 0;
        BatchSize = 0;
        ChunkCount = 0;
        RemainingChunks = 0;
        Error = null;
        PendingDependencies = 0;
        Dependents.Clear();
        Dispatched = false;
        Version = version;
        Interlocked.Exchange(ref Claim, (long)version << 32);
        Done.Reset();
    }
}

/// <summary>
/// Minimal data-parallel job system: a reusable worker pool, a chunked parallel-for, dependency
/// handles, and a main-thread <c>Complete()</c> that helps drain instead of idling.
///
/// <para>Deliberately not a general task scheduler. There is one global FIFO of batches, workers
/// claim chunks out of a batch with an interlocked cursor, and the thread that calls
/// <see cref="Complete"/> joins in. That keeps the whole thing small enough to reason about for
/// the render-loop consumers it exists for (frustum culling, skinning matrices, animation graph
/// evaluation), all of which are "split a flat index range, write disjoint slots" shaped.</para>
///
/// <para><see cref="Enabled"/> (or <c>XENGINE_JOBS=0</c>) forces every schedule to run inline on
/// the caller: same results, no threads. That makes "is this a parallelism bug?" a one-flag
/// experiment and gives the parity tests a serial reference to compare against.</para>
/// </summary>
public static class JobSystem
{
    private readonly record struct QueueEntry(JobBatch Batch, int Version);

    private static readonly ConcurrentQueue<QueueEntry> s_queue = new();
    private static readonly SemaphoreSlim s_signal = new(0);
    private static readonly ConcurrentBag<JobBatch> s_pool = new();
    private static readonly object s_workerGate = new();
    private static Thread[]? s_workers;
    private static int s_nextVersion;
    private static bool? s_enabled;
    private static volatile bool s_shutdown;
    private static long s_workerChunks;
    private static long s_callerChunks;

    /// <summary>
    /// Master switch. Defaults to on when the machine has more than one core, unless
    /// <c>XENGINE_JOBS=0</c> is set. Turning it off makes every schedule run inline on the
    /// calling thread.
    /// </summary>
    public static bool Enabled
    {
        get => s_enabled ??= ResolveDefaultEnabled();
        set => s_enabled = value;
    }

    /// <summary>Worker threads in the pool. The thread calling <see cref="Complete"/> helps too.</summary>
    public static int WorkerCount { get; private set; }

    /// <summary>Chunks executed by worker threads (diagnostics / tests).</summary>
    public static long WorkerChunksExecuted => Interlocked.Read(ref s_workerChunks);

    /// <summary>Chunks executed by the thread that called <see cref="Complete"/>.</summary>
    public static long CallerChunksExecuted => Interlocked.Read(ref s_callerChunks);

    internal static void ResetCountersForTests()
    {
        Interlocked.Exchange(ref s_workerChunks, 0);
        Interlocked.Exchange(ref s_callerChunks, 0);
    }

    private static bool ResolveDefaultEnabled()
    {
        try
        {
            string? env = Environment.GetEnvironmentVariable("XENGINE_JOBS");
            if (env != null)
            {
                switch (env.Trim().ToLowerInvariant())
                {
                    case "0" or "off" or "false" or "no": return false;
                    case "1" or "on" or "true" or "yes": return true;
                }
            }
        }
        catch (Exception)
        {
            // Restricted hosts fall through to the core-count default.
        }
        return Environment.ProcessorCount > 1;
    }

    private static void EnsureWorkers()
    {
        if (s_workers != null)
            return;
        lock (s_workerGate)
        {
            if (s_workers != null)
                return;
            int count = Math.Max(1, Environment.ProcessorCount - 1);
            var workers = new Thread[count];
            WorkerCount = count;
            for (int i = 0; i < count; i++)
            {
                workers[i] = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = $"XEngine Job Worker {i}",
                };
                workers[i].Start();
            }
            s_workers = workers;
        }
    }

    /// <summary>
    /// Splits <paramref name="count"/> indices into chunks of at most <paramref name="batchSize"/>
    /// and runs <paramref name="body"/> once per chunk with its half-open <c>[start, end)</c>
    /// range. The body must only touch state private to its own range; the render-loop consumers
    /// all write disjoint array slots, which is race-free in the CLR object model even for
    /// <c>bool[]</c> (byte-granular writes never disturb neighbours).
    /// </summary>
    /// <param name="dependency">Optional handle this work must not start before.</param>
    public static JobHandle ParallelFor(
        int count,
        int batchSize,
        Action<int, int> body,
        JobHandle dependency = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (count <= 0)
            return JobHandle.Null;
        if (batchSize <= 0)
            batchSize = count;

        int chunkCount = (count + batchSize - 1) / batchSize;

        // One chunk or jobs disabled: run here. Scheduling would cost more than the work, and
        // inline keeps the failure mode simple.
        if (chunkCount <= 1 || !Enabled)
        {
            Complete(dependency);
            body(0, count);
            return JobHandle.Null;
        }

        EnsureWorkers();
        JobBatch batch = RentBatch();
        batch.RangeBody = body;
        batch.Count = count;
        batch.BatchSize = batchSize;
        batch.ChunkCount = chunkCount;
        batch.RemainingChunks = chunkCount;
        var handle = new JobHandle(batch, batch.Version);
        Dispatch(batch, dependency);
        return handle;
    }

    /// <summary>Runs one delegate on a worker thread, after <paramref name="dependency"/> finishes.</summary>
    public static JobHandle Schedule(Action body, JobHandle dependency = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Enabled)
        {
            Complete(dependency);
            body();
            return JobHandle.Null;
        }

        EnsureWorkers();
        JobBatch batch = RentBatch();
        batch.Body = body;
        batch.Count = 1;
        batch.BatchSize = 1;
        batch.ChunkCount = 1;
        batch.RemainingChunks = 1;
        var handle = new JobHandle(batch, batch.Version);
        Dispatch(batch, dependency);
        return handle;
    }

    private static readonly Action s_noop = static () => { };

    internal static JobHandle Combine(JobHandle a, JobHandle b)
    {
        // A no-op batch that depends on both. Enough for the handful of fan-ins the render loop
        // needs; a real dependency graph is out of scope for the minimal system.
        EnsureWorkers();
        JobBatch batch = RentBatch();
        batch.Body = s_noop;
        batch.Count = 1;
        batch.BatchSize = 1;
        batch.ChunkCount = 1;
        batch.RemainingChunks = 1;
        var handle = new JobHandle(batch, batch.Version);

        Interlocked.Add(ref batch.PendingDependencies, 2);
        int resolved = 0;
        if (!TryAddDependent(a, batch)) resolved++;
        if (!TryAddDependent(b, batch)) resolved++;
        if (resolved > 0 && Interlocked.Add(ref batch.PendingDependencies, -resolved) == 0)
            Enqueue(batch);
        return handle;
    }

    private static void Dispatch(JobBatch batch, JobHandle dependency)
    {
        if (dependency.Batch == null)
        {
            Enqueue(batch);
            return;
        }

        Interlocked.Increment(ref batch.PendingDependencies);
        if (!TryAddDependent(dependency, batch)
            && Interlocked.Decrement(ref batch.PendingDependencies) == 0)
        {
            // The dependency finished between the handle check and the registration.
            Enqueue(batch);
        }
    }

    /// <summary>Registers <paramref name="dependent"/> on <paramref name="handle"/>. False when the
    /// dependency already completed, so the caller must not wait on it.</summary>
    private static bool TryAddDependent(JobHandle handle, JobBatch dependent)
    {
        JobBatch? source = handle.Batch;
        if (source == null)
            return false;
        lock (source.DependencyGate)
        {
            if (source.IsCompleted(handle.Version))
                return false;
            source.Dependents.Add(dependent);
            return true;
        }
    }

    private static void Enqueue(JobBatch batch)
    {
        batch.Dispatched = true;
        // Publish once per worker we could usefully wake, bounded by the chunk count. Extra
        // copies are harmless: a worker that finds the cursor exhausted just moves on.
        int copies = Math.Min(Math.Max(WorkerCount, 1), batch.ChunkCount);
        var entry = new QueueEntry(batch, batch.Version);
        for (int i = 0; i < copies; i++)
            s_queue.Enqueue(entry);
        s_signal.Release(copies);
    }

    private static JobBatch RentBatch()
    {
        if (!s_pool.TryTake(out JobBatch? batch))
            batch = new JobBatch();
        batch.Reset(Interlocked.Increment(ref s_nextVersion));
        return batch;
    }

    private static void WorkerLoop()
    {
        while (!s_shutdown)
        {
            try { s_signal.Wait(); }
            catch (ObjectDisposedException) { return; }
            if (s_shutdown)
                return;
            if (s_queue.TryDequeue(out QueueEntry entry))
                RunChunks(entry.Batch, entry.Version, worker: true);
        }
    }

    /// <summary>
    /// Claims and runs chunks until this batch generation has none left. Safe to call
    /// concurrently from any number of threads, including the one waiting in
    /// <see cref="Complete"/>, and safe to call on a batch that has already been recycled
    /// (the version-tagged CAS turns that into an immediate return).
    /// </summary>
    private static void RunChunks(JobBatch batch, int version, bool worker)
    {
        while (true)
        {
            int chunk;
            while (true)
            {
                long snapshot = Interlocked.Read(ref batch.Claim);
                if ((int)(snapshot >> 32) != version)
                    return;                             // recycled under us
                int next = (int)(uint)snapshot;
                if (next >= batch.ChunkCount)
                    return;                             // drained
                long desired = ((long)version << 32) | (uint)(next + 1);
                if (Interlocked.CompareExchange(ref batch.Claim, desired, snapshot) == snapshot)
                {
                    chunk = next;
                    break;
                }
            }

            try
            {
                if (batch.RangeBody is { } range)
                {
                    int start = chunk * batch.BatchSize;
                    int end = Math.Min(start + batch.BatchSize, batch.Count);
                    range(start, end);
                }
                else
                {
                    batch.Body?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref batch.Error, ExceptionDispatchInfo.Capture(ex), null);
            }
            finally
            {
                if (worker)
                    Interlocked.Increment(ref s_workerChunks);
                else
                    Interlocked.Increment(ref s_callerChunks);
                if (Interlocked.Decrement(ref batch.RemainingChunks) == 0)
                    FinishBatch(batch);
            }
        }
    }

    private static void FinishBatch(JobBatch batch)
    {
        List<JobBatch>? ready = null;
        lock (batch.DependencyGate)
        {
            batch.Done.Set();
            if (batch.Dependents.Count > 0)
            {
                ready = new List<JobBatch>(batch.Dependents);
                batch.Dependents.Clear();
            }
        }

        if (ready == null)
            return;
        for (int i = 0; i < ready.Count; i++)
        {
            if (Interlocked.Decrement(ref ready[i].PendingDependencies) == 0)
                Enqueue(ready[i]);
        }
    }

    /// <summary>
    /// Blocks until the handle's batch has finished, running its outstanding chunks on the
    /// calling thread first so a waiting main thread does useful work instead of spinning.
    /// Rethrows the first worker exception. Idempotent for a retired handle.
    /// </summary>
    public static void Complete(JobHandle handle)
    {
        JobBatch? batch = handle.Batch;
        if (batch == null)
            return;
        if (Volatile.Read(ref batch.Version) != handle.Version)
            return; // Already completed and recycled by an earlier Complete().

        if (Volatile.Read(ref batch.PendingDependencies) == 0 && batch.Dispatched)
            RunChunks(batch, handle.Version, worker: false);

        batch.Done.Wait();
        if (Interlocked.CompareExchange(ref batch.Version, handle.Version + 1, handle.Version) != handle.Version)
            return; // Another Complete() on a copy of this handle already retired the batch.

        // Bumping the generation also invalidates the packed claim word, so any worker still
        // spinning on a stale queue entry bails out before it can touch the recycled batch.
        Interlocked.Exchange(ref batch.Claim, (long)(handle.Version + 1) << 32);
        ExceptionDispatchInfo? error = batch.Error;
        batch.RangeBody = null;
        batch.Body = null;
        batch.Error = null;
        s_pool.Add(batch);
        error?.Throw();
    }

    /// <summary>Stops the worker threads. Only used by hosts tearing the runtime down.</summary>
    public static void Shutdown()
    {
        Thread[]? workers;
        lock (s_workerGate)
        {
            workers = s_workers;
            if (workers == null)
                return;
            s_shutdown = true;
            s_signal.Release(workers.Length);
            s_workers = null;
            WorkerCount = 0;
        }
        for (int i = 0; i < workers.Length; i++)
        {
            if (!workers[i].Join(TimeSpan.FromSeconds(2)))
                Debug.LogWarning($"Job worker '{workers[i].Name}' did not exit in time.");
        }
        s_shutdown = false;
    }
}
