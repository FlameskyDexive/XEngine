// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Async.Internal;

/// <summary>
/// A polling list of <see cref="IFrameLoopItem"/> for a single <see cref="FrameTiming"/> phase.
/// <see cref="Add"/> appends to a pending buffer; <see cref="Run"/> first absorbs pending into the
/// active list (under lock) and then polls the active list outside the lock. Items whose
/// <see cref="IFrameLoopItem.MoveNext"/> returns false are removed via two-pointer compaction. Items
/// added during a poll (e.g. a completing promise that starts another Delay) land in the pending
/// buffer and are absorbed next frame — no re-entrancy hazard, no lock held across MoveNext.
/// </summary>
internal sealed class FrameLoopRunner
{
    private readonly object _gate = new();
    private IFrameLoopItem[] _active = Array.Empty<IFrameLoopItem>();
    private int _activeCount;
    private IFrameLoopItem[] _pending = new IFrameLoopItem[16];
    private int _pendingCount;

    public void Add(IFrameLoopItem item)
    {
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        lock (_gate)
        {
            if (_pendingCount == _pending.Length)
            {
                Array.Resize(ref _pending, _pending.Length * 2);
            }
            _pending[_pendingCount++] = item;
        }
    }

    /// <summary>Absorb pending items, then poll each active item once. Compacts survivors in place.</summary>
    public void Run()
    {
        // 1. Absorb pending → active under lock.
        lock (_gate)
        {
            for (int i = 0; i < _pendingCount; i++)
            {
                if (_activeCount == _active.Length)
                {
                    Array.Resize(ref _active, _active.Length == 0 ? 16 : _active.Length * 2);
                }
                _active[_activeCount++] = _pending[i];
                _pending[i] = null!;
            }
            _pendingCount = 0;
        }

        if (_activeCount == 0)
        {
            return;
        }

        // 2. Poll active outside the lock (MoveNext may re-enter via Add → pending). Two-pointer
        //    compaction keeps survivors contiguous; dropped slots are nulled for GC only AFTER the
        //    copy-forward pass (nulling during the pass would erase a survivor when i == j).
        int j = 0;
        int count = _activeCount;
        for (int i = 0; i < count; i++)
        {
            IFrameLoopItem item = _active[i];
            bool keep;
            try
            {
                keep = item.MoveNext();
            }
            catch (Exception ex)
            {
                keep = false;
                XTaskScheduler.PublishUnobservedTaskException(ex);
            }

            if (keep)
            {
                _active[j++] = item;
            }
        }

        // Clear the vacated tail so dropped items are released for GC.
        for (int k = j; k < count; k++)
        {
            _active[k] = null!;
        }

        _activeCount = j;
    }

    /// <summary>
    /// Drop pending and active items that reference a collectible (hot-reloadable) assembly so
    /// they cannot pin the script AssemblyLoadContext during a hot-reload. Items are abandoned
    /// without completion — their awaiting code belongs to an assembly being torn down.
    /// Returns the number of dropped items.
    /// </summary>
    public int RemoveCollectibleBound()
    {
        lock (_gate)
        {
            int removed = CompactCollectible(_active, _activeCount);
            _activeCount -= removed;
            int pendingRemoved = CompactCollectible(_pending, _pendingCount);
            _pendingCount -= pendingRemoved;
            return removed + pendingRemoved;
        }
    }

    private static int CompactCollectible(IFrameLoopItem[] buffer, int count)
    {
        int removed = 0;
        int j = 0;
        for (int i = 0; i < count; i++)
        {
            IFrameLoopItem item = buffer[i];
            if (item is ICollectibleBoundItem { IsCollectibleBound: true })
            {
                buffer[i] = null!;
                removed++;
                continue;
            }
            buffer[j++] = item;
        }

        for (int k = j; k < count; k++)
        {
            buffer[k] = null!;
        }

        return removed;
    }

    /// <summary>Number of active plus pending items (diagnostics/tests only).</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _activeCount + _pendingCount;
            }
        }
    }
}
