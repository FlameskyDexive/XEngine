// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// AsyncOperationSystem - the scheduler that drives all AsyncOperationBase instances. Update() is
// called once per engine frame from XAssets.Tick (wired into the Game.cs main loop). It bounds the
// time spent on async work per frame (MaxTimeSliceMs) so asset loading never stalls the render
// thread, and re-sorts its queue when an op's priority changes (IsDirty).
//
// Independent rewrite informed by YooAsset's AsyncOperationSystem (Apache-2.0).

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Static scheduler for <see cref="AsyncOperationBase"/>. <see cref="Update"/> is called each frame
/// from <c>XAssets.Tick</c>; it pumps active ops until either all are done or the per-frame time
/// slice is exhausted, at which point remaining work is deferred to the next frame.
/// </summary>
public static class AsyncOperationSystem
{
    private static readonly List<AsyncOperationBase> _operations = new();
    private static readonly object _gate = new();

    private static long _maxTimeSliceTicks = TimeSpan.FromMilliseconds(2).Ticks;
    private static long _sliceDeadlineTicks;

    /// <summary>True when the current frame's async time slice has been used up. AsyncOperationBase
    /// exposes this to subclasses via <c>IsBusy</c> so they can yield mid-op.</summary>
    public static bool IsBusy { get; private set; }

    /// <summary>Maximum wall-clock time spent on async ops per frame. Default 2 ms.</summary>
    public static TimeSpan MaxTimeSlice
    {
        get => TimeSpan.FromTicks(_maxTimeSliceTicks);
        set => _maxTimeSliceTicks = value.Ticks;
    }

    /// <summary>Initialize the scheduler. Safe to call repeatedly.</summary>
    public static void Initialize() { }

    /// <summary>Enqueue an operation so the scheduler starts and drives it.</summary>
    public static void StartOperation(AsyncOperationBase operation)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        operation.StartOperation();
        lock (_gate) _operations.Add(operation);
    }

    /// <summary>Per-frame pump. Drains the operation queue, respecting the time slice.</summary>
    public static void Update()
    {
        AsyncOperationBase[] buffer;
        int count;
        lock (_gate)
        {
            count = _operations.Count;
            if (count == 0) return;
            buffer = ArrayPool<AsyncOperationBase>.Shared.Rent(count);
            _operations.CopyTo(buffer);
        }
        try
        {

            _sliceDeadlineTicks = Stopwatch.GetTimestamp() + _maxTimeSliceTicks;
            IsBusy = false;

            // Snapshot to a buffer so operations can add new operations during iteration (e.g. a provider
            // spawning bundle loaders) without invalidating the enumerator.
            for (int i = 0; i < count; i++)
            {
                var op = buffer[i];
                if (op.IsFinalized)
                {
                    lock (_gate) _operations.Remove(op);
                    continue;
                }

                op.UpdateOperation();

                if (op.IsFinalized)
                {
                    lock (_gate) _operations.Remove(op);
                }

                // Honor the time slice: if we've overrun, defer the rest to next frame. Check cheaply
                // (timestamp compare) rather than allocating a timer.
                if (op.IsDirty || (i + 1 < count && Stopwatch.GetTimestamp() >= _sliceDeadlineTicks))
                {
                    IsBusy = true;
                    op.IsDirty = false;
                }
            }

            // If any ops flagged dirty (priority change), re-sort the live list by priority desc.
            lock (_gate)
            {
                if (_operations.Count > 1)
                {
                    bool anyDirty = false;
                    for (int i = 0; i < _operations.Count; i++)
                        if (_operations[i].IsDirty) { anyDirty = true; break; }
                    if (anyDirty)
                    {
                        _operations.Sort(); // AsyncOperationBase.CompareTo orders by priority desc
                        foreach (var op in _operations) op.IsDirty = false;
                    }
                }
            }
        }
        finally { ArrayPool<AsyncOperationBase>.Shared.Return(buffer, clearArray: true); }
    }

    /// <summary>Number of operations currently being driven.</summary>
    public static int ActiveCount { get { lock (_gate) return _operations.Count; } }

    /// <summary>Clear all operations (used on teardown / package destroy).</summary>
    public static void Clear()
    {
        AsyncOperationBase[] pending;
        lock (_gate)
        {
            pending = _operations.ToArray();
            _operations.Clear();
        }
        foreach (var op in pending) op.AbortOperation();
    }

    /// <summary>
    /// Hot-reload: this static operation list long-outlives the script AssemblyLoadContext, and a
    /// pending op's Completed handlers can hold script-bound awaiter chains (op.AsXTask()). Strip
    /// those subscriptions (without aborting engine ops) so the context can unload.
    /// </summary>
    [XEngine.Runtime.OnAssemblyUnload]
    private static void RemoveCollectibleBoundHandlers()
    {
        lock (_gate)
            foreach (var op in _operations)
                op.RemoveCollectibleCompletedHandlers();
    }
}
