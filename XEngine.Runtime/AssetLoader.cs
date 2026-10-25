// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace XEngine.Runtime;

/// <summary>
/// Deserializes assets off the main/render thread on desktop (one dedicated background thread),
/// so a scene can appear immediately while its meshes/textures stream in.
///
/// <para>On WASM there is no worker thread (<c>WasmEnableThreads=false</c>). Requests stay in the
/// same queues and <see cref="Tick"/> drains them with a per-frame time budget. Blocking wait on
/// the rAF thread is forbidden.</para>
///
/// <para>Requests are de-duplicated and split into a High-priority queue (blocking
/// <see cref="AssetRef{T}.EnsureLoaded"/> calls) drained before the Normal queue
/// (fire-and-forget <see cref="AssetRef{T}.Res"/> resolves).</para>
///
/// <para>The actual deserialize runs via <see cref="AssetDatabase.Get"/>. GPU resources created
/// during load are safe because <c>Graphics.Submit</c> is thread-safe and the render thread drains
/// continuously on desktop.</para>
/// </summary>
public static class AssetLoader
{
    internal const int DefaultFrameBudgetMilliseconds = 4;

    private static readonly object _lock = new();
    private static readonly Queue<Guid> _high = new();
    private static readonly Queue<Guid> _normal = new();

    // Ids currently queued or in-flight (dedup so an asset is only loaded once at a time).
    private static readonly HashSet<Guid> _queued = new();

#if !XENGINE_WEB
    // Completion events for callers blocked in LoadBlocking, keyed by asset id.
    private static readonly Dictionary<Guid, ManualResetEventSlim> _waiters = new();

    // Counts pending items so the loop sleeps when idle instead of spinning.
    private static readonly SemaphoreSlim _signal = new(0);

    private static readonly object _startLock = new();
    private static Thread? _thread;
    private static volatile bool _running;

    // True only on the loader thread itself. Lets LoadBlocking detect a re-entrant blocking
    // load (e.g. a deserialize that calls EnsureLoaded on a dependency) and load it inline
    // instead of enqueueing and waiting on the single loader thread which would deadlock.
    [ThreadStatic] private static bool _isLoaderThread;
#endif



    internal static bool UsesBackgroundThread =>
#if XENGINE_WEB
        false;
#else
        true;
#endif

    /// <summary>Queue a non-blocking background load. No-op if already cached/queued/in-flight.</summary>
    public static void Request(Guid id)
    {
        if (id == Guid.Empty) return;
        if (AssetDatabase.GetCached(id) != null) return;
        if (!TryEnqueue(id, high: false))
            return;
#if !XENGINE_WEB
        EnsureStarted();
        _signal.Release();
#endif
    }

    /// <summary>
    /// Load <paramref name="id"/> with priority and block until it finishes, returning the
    /// instance (or null if it couldn't be loaded). Used by <see cref="AssetRef{T}.EnsureLoaded"/>.
    /// On WASM this loads inline on the calling (rAF) thread — a <c>Wait</c> would freeze the frame.
    /// </summary>
    public static EngineObject? LoadBlocking(Guid id)
    {
        if (id == Guid.Empty) return null;

        var cached = AssetDatabase.GetCached(id);
        if (cached != null) return cached;

#if XENGINE_WEB
        return LoadNow(id);
#else
        // Re-entrant from the loader thread (a deserialize asked to block on a dependency):
        // load inline on this thread. Enqueueing would wait on the only thread that drains.
        if (_isLoaderThread)
            return AssetDatabase.Get(id);

        // Re-entrant while holding a backend load lock (a deserialize side effect asked to block
        // on a dependency, e.g. a component resolving its AssetRef during Scene.OnAfterDeserialize):
        // load inline here too. The background loader would need that same backend lock to drain
        // the request and would deadlock against this waiter.
        if (AssetBackendBase.LoadLockHeldByCurrentThread)
            return AssetDatabase.Get(id);

        EnsureStarted();

        ManualResetEventSlim ev;
        lock (_lock)
        {
            if (!_waiters.TryGetValue(id, out ev!))
            {
                ev = new ManualResetEventSlim(false);
                _waiters[id] = ev;
            }
            if (_queued.Add(id))
            {
                _high.Enqueue(id);
                // One semaphore permit per newly queued id, matching the drain loop's Wait().
                _signal.Release();
            }
            // else: an entry for this id is already queued with its own permit and _waiters[id]
            // is already registered (possibly by this call above). The loader sets that event
            // when the load finishes, so every waiter shares it. Enqueueing an extra copy here
            // would be permit-less: if its permit was already consumed it becomes a stale entry
            // that TryDequeue discards without ever waking the waiters — a permanent hang.
        }

        ev.Wait();
        return AssetDatabase.GetCached(id);
#endif
    }

    /// <summary>
    /// Drain queued loads. Desktop: no-op (the background thread owns the queue). WASM: process
    /// high then normal until the millisecond budget is exhausted, then return to rAF.
    /// </summary>
    public static void Tick(int budgetMilliseconds = DefaultFrameBudgetMilliseconds)
    {
#if XENGINE_WEB
        Drain(budgetMilliseconds);
#else
        _ = budgetMilliseconds;
#endif
    }

#if !XENGINE_WEB
    private static void EnsureStarted()
    {
        if (_running) return;
        lock (_startLock)
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "XEngine Asset Loader",
            };
            _thread.Start();
        }
    }

    private static void Loop()
    {
        _isLoaderThread = true;
        while (_running)
        {
            _signal.Wait();
            if (!_running) break;

            if (!TryDequeue(out Guid id))
                continue;

            LoadNow(id);
        }
    }
#endif

#if XENGINE_WEB
    private static void Drain(int budgetMilliseconds)
    {
        int budget = Math.Max(0, budgetMilliseconds);
        long start = Stopwatch.GetTimestamp();
        long budgetTicks = budget <= 0 ? 0 : budget * Stopwatch.Frequency / 1000L;
        while (TryDequeue(out Guid id))
        {
            LoadNow(id);
            if (budgetTicks == 0 || Stopwatch.GetTimestamp() - start >= budgetTicks)
                break;
        }
    }
#endif

    private static bool TryEnqueue(Guid id, bool high)
    {
        lock (_lock)
        {
            if (!_queued.Add(id))
                return false;
            if (high)
                _high.Enqueue(id);
            else
                _normal.Enqueue(id);
            return true;
        }
    }

    private static bool TryDequeue(out Guid id)
    {
        lock (_lock)
        {
            while (true)
            {
                if (_high.Count > 0)
                    id = _high.Dequeue();
                else if (_normal.Count > 0)
                    id = _normal.Dequeue();
                else
                {
                    id = Guid.Empty;
                    return false;
                }

                // LoadBlocking / a previous drain may have completed this id already.
                if (_queued.Contains(id))
                    return true;

                // Stale duplicate entry: the load finished through another path. Wake any waiters
                // registered on this id so they re-check the cache instead of waiting forever on
                // an event this discarded entry would otherwise never set.
                if (_waiters.Remove(id, out ManualResetEventSlim? stale))
                    stale.Set();
            }
        }
    }

    private static EngineObject? LoadNow(Guid id)
    {
        EngineObject? result = null;
        try { result = AssetDatabase.Get(id); }
        catch (Exception ex) { Debug.LogError($"[AssetLoader] Failed to load asset {id}: {ex}"); }

        lock (_lock)
        {
            _queued.Remove(id);
#if !XENGINE_WEB
            if (_waiters.TryGetValue(id, out ManualResetEventSlim? ev))
            {
                _waiters.Remove(id);
                ev.Set();
            }
#endif
        }
        return result;
    }

    /// <summary>Stop the loader thread (desktop) and release any blocked callers. Called at shutdown.</summary>
    public static void Stop()
    {
#if !XENGINE_WEB
        if (_running)
        {
            _running = false;
            _signal.Release();
            var t = _thread;
            _thread = null;
            try { t?.Join(2000); } catch { }
        }
#endif

        lock (_lock)
        {
            _high.Clear();
            _normal.Clear();
            _queued.Clear();
#if !XENGINE_WEB
            foreach (var ev in _waiters.Values) ev.Set();
            _waiters.Clear();
#endif
        }
    }
}
