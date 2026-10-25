// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Async.Internal;

/// <summary>
/// A double-buffered queue of one-shot <see cref="Action"/> continuations for a single
/// <see cref="FrameTiming"/> phase. <see cref="Enqueue"/> appends to the waiting buffer (under a
/// lock); <see cref="Run"/> swaps waiting→active and drains the active buffer outside the lock.
/// Continuations enqueued during a drain land in the (now empty) waiting buffer and fire next frame,
/// which makes the drain re-entrancy-safe.
/// </summary>
internal sealed class ContinuationQueue
{
    private readonly object _gate = new();
    private Action[] _active = Array.Empty<Action>();
    private int _activeCount;
    private Action[] _waiting = new Action[16];
    private int _waitingCount;

    public void Enqueue(Action continuation)
    {
        if (continuation == null)
        {
            throw new ArgumentNullException(nameof(continuation));
        }

        lock (_gate)
        {
            if (_waitingCount == _waiting.Length)
            {
                Array.Resize(ref _waiting, _waiting.Length * 2);
            }
            _waiting[_waitingCount++] = continuation;
        }
    }

    /// <summary>Swap the waiting buffer into active and invoke every continuation. Cheap no-op when empty.</summary>
    public void Run()
    {
        Action[] active;
        int count;

        lock (_gate)
        {
            if (_waitingCount == 0)
            {
                return; // nothing pending this phase
            }

            // Swap: the freshly-filled waiting buffer becomes the active buffer we drain; the old
            // active buffer (now empty or stale) is recycled as the next waiting buffer.
            active = _waiting;
            count = _waitingCount;
            _waiting = _active.Length > 0 ? _active : new Action[16];
            _waitingCount = 0;
            _active = active;
        }

        for (int i = 0; i < count; i++)
        {
            try
            {
                active[i].Invoke();
            }
            catch (Exception ex)
            {
                // A faulting continuation must not break sibling continuations.
                XTaskScheduler.PublishUnobservedTaskException(ex);
            }
            active[i] = null!; // release for GC; the slot may be reused next swap
        }
    }

    /// <summary>
    /// Drop queued continuations that reference a collectible (hot-reloadable) assembly so they
    /// cannot pin the script AssemblyLoadContext during a hot-reload. The continuations are simply
    /// abandoned — their awaiting code belongs to an assembly that is being torn down.
    /// Returns the number of dropped entries.
    /// </summary>
    public int RemoveCollectibleBound()
    {
        lock (_gate)
        {
            int removed = 0;
            int j = 0;
            for (int i = 0; i < _waitingCount; i++)
            {
                Action continuation = _waiting[i];
                if (CollectibleBinding.IsBound(continuation))
                {
                    removed++;
                    continue;
                }
                _waiting[j++] = continuation;
            }

            for (int k = j; k < _waitingCount; k++)
            {
                _waiting[k] = null!;
            }

            _waitingCount = j;
            return removed;
        }
    }

    /// <summary>Number of queued continuations (diagnostics/tests only).</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _waitingCount;
            }
        }
    }
}
