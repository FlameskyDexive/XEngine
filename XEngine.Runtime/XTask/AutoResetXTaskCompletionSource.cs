// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

namespace XEngine.Async;

// NOTE: AutoResetXTaskCompletionSource and AutoResetXTaskCompletionSource<T> are pooled, single-use
// completion sources backed by XTaskCompletionSourceCore. Create() pops from a per-type
// TaskPool<T>; GetResult returns the instance to the pool after the consumer has read the result
// (deferred auto-reset). Because each instance accepts exactly one continuation, awaiters must
// await exactly once — re-awaiting after GetResult throws (version-token mismatch).

/// <summary>
/// Pooled, single-use completion source for void-producing XTasks. Create with
/// <see cref="Create"/>, complete with <see cref="TrySetResult"/>/<see cref="TrySetException"/>/
/// <see cref="TrySetCanceled"/>, expose <see cref="Task"/> to awaiters.
/// </summary>
public sealed class AutoResetXTaskCompletionSource : IXTaskSource, ITaskPoolNode<AutoResetXTaskCompletionSource>, Internal.IChainCollectibleSource
{
    private static TaskPool<AutoResetXTaskCompletionSource> s_pool;

    private XTaskCompletionSourceCore<AsyncUnit> _core;
    private AutoResetXTaskCompletionSource? _nextNode;
    private short _version;

    static AutoResetXTaskCompletionSource()
    {
        TaskPool.RegisterSizeGetter(typeof(AutoResetXTaskCompletionSource), () => s_pool.Size);
    }

    private AutoResetXTaskCompletionSource() { }

    public ref AutoResetXTaskCompletionSource? NextNode => ref _nextNode;

    public static AutoResetXTaskCompletionSource Create()
    {
        if (!s_pool.TryPop(out AutoResetXTaskCompletionSource? result))
        {
            result = new AutoResetXTaskCompletionSource();
        }
        result._version = result._core.Version;
        return result;
    }

    /// <summary>Create an already-canceled source (no timer registration, no pool churn).</summary>
    public static AutoResetXTaskCompletionSource CreateFromCanceled(CancellationToken cancellationToken, out short token)
    {
        AutoResetXTaskCompletionSource result = Create();
        token = result._version;
        result._core.TrySetCanceled(cancellationToken);
        return result;
    }

    /// <summary>An <see cref="XTask"/> that resolves to this source at its current version.</summary>
    public XTask Task => new(this, _version);

    public bool TrySetResult() => _core.TrySetResult(AsyncUnit.Default);

    public bool TrySetException(Exception exception) => _core.TrySetException(exception);

    public bool TrySetCanceled(CancellationToken cancellationToken = default) => _core.TrySetCanceled(cancellationToken);

    public XTaskStatus GetStatus(short token) => _core.GetStatus(token);

    public XTaskStatus UnsafeGetStatus() => _core.UnsafeGetStatus();

    public void OnCompleted(Action<object> continuation, object state, short token)
        => _core.OnCompleted(continuation, state, token);

    public void GetResult(short token)
    {
        try
        {
            _core.GetResult(token);
        }
        finally
        {
            TryReturn();
        }
    }

    private bool TryReturn()
    {
        _core.Reset();
        return s_pool.TryPush(this);
    }

    /// <summary>Hot-reload: the ultimate awaiter of this source may be a user state machine (e.g.
    /// script code awaiting <c>op.AsXTask()</c>); exposed so scrubs can see through the engine-side
    /// closure that completes this source.</summary>
    bool Internal.IChainCollectibleSource.HasCollectibleAwaiter => _core.IsAwaiterCollectible;

    /// <summary>
    /// Hot-reload: drop the pending awaiter's continuation WITHOUT completing the source and without
    /// returning the instance to the pool. Used by the BCL Task bridge: the owning Task keeps the
    /// source alive until it finishes, so the only way to unpin the script ALC mid-wait is to detach
    /// the user continuation. A late Task completion then lands on a version-bumped, unawaited
    /// source, which is harmless.
    /// </summary>
    internal void DetachForReload() => _core.Reset();
}

/// <summary>
/// Pooled, single-use completion source for typed XTasks. See
/// <see cref="AutoResetXTaskCompletionSource"/> for the void-producing variant.
/// </summary>
public sealed class AutoResetXTaskCompletionSource<T> : IXTaskSource<T>, ITaskPoolNode<AutoResetXTaskCompletionSource<T>>, Internal.IChainCollectibleSource
{
    private static TaskPool<AutoResetXTaskCompletionSource<T>> s_pool;

    private XTaskCompletionSourceCore<T> _core;
    private AutoResetXTaskCompletionSource<T>? _nextNode;
    private short _version;

    static AutoResetXTaskCompletionSource()
    {
        TaskPool.RegisterSizeGetter(typeof(AutoResetXTaskCompletionSource<T>), () => s_pool.Size);
    }

    private AutoResetXTaskCompletionSource() { }

    public ref AutoResetXTaskCompletionSource<T>? NextNode => ref _nextNode;

    public static AutoResetXTaskCompletionSource<T> Create()
    {
        if (!s_pool.TryPop(out AutoResetXTaskCompletionSource<T>? result))
        {
            result = new AutoResetXTaskCompletionSource<T>();
        }
        result._version = result._core.Version;
        return result;
    }

    public static AutoResetXTaskCompletionSource<T> CreateFromCanceled(CancellationToken cancellationToken, out short token)
    {
        AutoResetXTaskCompletionSource<T> result = Create();
        token = result._version;
        result._core.TrySetCanceled(cancellationToken);
        return result;
    }

    public static AutoResetXTaskCompletionSource<T> CreateFromResult(T value, out short token)
    {
        AutoResetXTaskCompletionSource<T> result = Create();
        token = result._version;
        result._core.TrySetResult(value);
        return result;
    }

    public XTask<T> Task => new(this, _version);

    public bool TrySetResult(T value) => _core.TrySetResult(value);

    public bool TrySetException(Exception exception) => _core.TrySetException(exception);

    public bool TrySetCanceled(CancellationToken cancellationToken = default) => _core.TrySetCanceled(cancellationToken);

    public XTaskStatus GetStatus(short token) => _core.GetStatus(token);

    public XTaskStatus UnsafeGetStatus() => _core.UnsafeGetStatus();

    public void OnCompleted(Action<object> continuation, object state, short token)
        => _core.OnCompleted(continuation, state, token);

    public T GetResult(short token)
    {
        try
        {
            return _core.GetResult(token);
        }
        finally
        {
            TryReturn();
        }
    }

    private bool TryReturn()
    {
        _core.Reset();
        return s_pool.TryPush(this);
    }

    /// <summary>Hot-reload: see <see cref="Internal.IChainCollectibleSource"/> on the non-generic variant.</summary>
    bool Internal.IChainCollectibleSource.HasCollectibleAwaiter => _core.IsAwaiterCollectible;
}
