// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading;
using XEngine.Async.Internal;

namespace XEngine.Async;

/// <summary>
/// Intrusive singly-linked free-list node. Pooled types embed a <c>nextNode</c> field and expose it
/// through this interface so <see cref="TaskPool{T}"/> can link them without allocating separate
/// nodes.
/// </summary>
public interface ITaskPoolNode<T> where T : class, ITaskPoolNode<T>
{
    ref T? NextNode { get; }
}

/// <summary>
/// A lock-free intrusive singly-linked stack of pooled objects. Push/pop are guarded by a single
/// CAS <c>gate</c>; the pooled object itself is the link node, so there are zero node allocations.
/// Each pooled type owns a <c>static TaskPool&lt;X&gt; pool</c> field.
/// </summary>
public struct TaskPool<T> where T : class, ITaskPoolNode<T>
{
    private int _gate;
    private int _size;
    private T? _root;

    public int Size => _size;

    /// <summary>Try to pop an instance. Returns false when the pool is empty.</summary>
    public bool TryPop(out T? result)
    {
        // Single CAS gate: spin if contended, take the root, relink next.
        if (Interlocked.CompareExchange(ref _gate, 1, 0) == 0)
        {
            try
            {
                T? r = _root;
                if (r != null)
                {
                    _root = r.NextNode;
                    r.NextNode = null;
                    _size--;
                    result = r;
                    return true;
                }
            }
            finally
            {
                _gate = 0;
            }
        }

        result = default;
        return false;
    }

    /// <summary>Try to return an instance. Returns false (and drops the instance) when the pool is full.</summary>
    public bool TryPush(T item)
    {
        if (item == null)
        {
            throw Error.ArgumentNull(nameof(item));
        }

        if (Interlocked.CompareExchange(ref _gate, 1, 0) == 0)
        {
            try
            {
                if (_size < TaskPool.MaxPoolSize)
                {
                    item.NextNode = _root;
                    _root = item;
                    _size++;
                    return true;
                }
            }
            finally
            {
                _gate = 0;
            }
        }

        return false;
    }
}

/// <summary>Global pool configuration shared across all <see cref="TaskPool{T}"/> instances.</summary>
public static class TaskPool
{
    private static int _maxPoolSize;
    private static readonly List<Func<int>> s_sizeGetters = new();

    static TaskPool()
    {
        // Env override: XTASK_MAX_POOLSIZE=<int>. Default unbounded.
        string? env = Environment.GetEnvironmentVariable("XTASK_MAX_POOLSIZE");
        _maxPoolSize = (env != null && int.TryParse(env, out int v)) ? v : int.MaxValue;
    }

    /// <summary>Maximum instances a single <see cref="TaskPool{T}"/> will retain. Defaults to
    /// <see cref="int.MaxValue"/> (overridable via the XTASK_MAX_POOLSIZE environment variable).</summary>
    public static int MaxPoolSize
    {
        get => _maxPoolSize;
        set => _maxPoolSize = value;
    }

    /// <summary>Programmatic override of <see cref="MaxPoolSize"/>.</summary>
    public static void SetMaxPoolSize(int maxPoolSize) => _maxPoolSize = maxPoolSize;

    /// <summary>Register a diagnostic size getter for a pooled type (used by leak trackers).</summary>
    public static void RegisterSizeGetter(Type type, Func<int> getSize)
    {
        lock (s_sizeGetters)
        {
            s_sizeGetters.Add(getSize);
        }
    }

    /// <summary>
    /// Hot-reload: size getters are registered from the static constructors of generic runner types
    /// instantiated over USER state machines (<c>AsyncXTask&lt;UserStateMachine&gt;</c>), so the
    /// lambda belongs to a collectible-ALC instantiation. An unscrubbed getter permanently pins the
    /// script AssemblyLoadContext after any user <c>async XTask</c> ran, turning every hot-reload
    /// into an editor restart. Strip collectible-bound getters; engine-owned ones survive.
    /// </summary>
    [XEngine.Runtime.OnAssemblyUnload]
    internal static void RemoveCollectibleSizeGetters()
    {
        lock (s_sizeGetters)
        {
            s_sizeGetters.RemoveAll(static getter => CollectibleBinding.IsBound(getter));
        }
    }
}
