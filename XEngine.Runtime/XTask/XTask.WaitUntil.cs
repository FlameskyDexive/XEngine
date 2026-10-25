// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using XEngine.Async.Internal;

namespace XEngine.Async;

public readonly partial struct XTask
{
    /// <summary>Suspend until <paramref name="predicate"/> returns true. Polled once per frame at <paramref name="timing"/>.</summary>
    public static XTask WaitUntil(Func<bool> predicate, FrameTiming timing = FrameTiming.Update,
        CancellationToken cancellationToken = default, bool cancelImmediately = false)
    {
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        if (cancellationToken.IsCancellationRequested) return XTask.FromCanceled(cancellationToken);

        var promise = WaitUntilPromise.Create(predicate, true, timing, cancellationToken, cancelImmediately, out short token);
        return new XTask(promise, token);
    }

    /// <summary>Suspend until <paramref name="predicate"/> returns false.</summary>
    public static XTask WaitWhile(Func<bool> predicate, FrameTiming timing = FrameTiming.Update,
        CancellationToken cancellationToken = default, bool cancelImmediately = false)
    {
        if (predicate == null) throw new ArgumentNullException(nameof(predicate));
        if (cancellationToken.IsCancellationRequested) return XTask.FromCanceled(cancellationToken);

        // WaitWhile(p) == WaitUntil(!p); implement by inverting the predicate via a flag.
        var promise = WaitUntilPromise.Create(predicate, false, timing, cancellationToken, cancelImmediately, out short token);
        return new XTask(promise, token);
    }

    /// <summary>Suspend until <paramref name="cancellationToken"/> is canceled.</summary>
    public static XTask WaitUntilCanceled(CancellationToken cancellationToken, FrameTiming timing = FrameTiming.Update,
        bool completeImmediately = false)
    {
        if (!cancellationToken.CanBeCanceled) return XTask.Never;
        if (cancellationToken.IsCancellationRequested) return XTask.CompletedTask;

        if (completeImmediately)
        {
            // Register on the token directly (fires on whatever thread cancels) rather than polling.
            return cancellationToken.WaitUntilCanceled();
        }

        var promise = WaitUntilCanceledPromise.Create(cancellationToken, timing, out short token);
        return new XTask(promise, token);
    }
}

internal sealed class WaitUntilPromise : IXTaskSource, IFrameLoopItem, ITaskPoolNode<WaitUntilPromise>, ICollectibleBoundItem
{
    private static TaskPool<WaitUntilPromise> s_pool;
    private static readonly Action<object?> s_cancelDelegate = static state => ((WaitUntilPromise)state!).OnCancel();

    private XTaskCompletionSourceCore<AsyncUnit> _core;
    private WaitUntilPromise? _nextNode;
    private short _version;
    private CancellationToken _cancellationToken;
    private CancellationTokenRegistration _registration;
    private Func<bool>? _predicate;
    private bool _completeWhenTrue;

    static WaitUntilPromise() { TaskPool.RegisterSizeGetter(typeof(WaitUntilPromise), () => s_pool.Size); }
    private WaitUntilPromise() { }

    public ref WaitUntilPromise? NextNode => ref _nextNode;

    bool ICollectibleBoundItem.IsCollectibleBound
        => _core.IsAwaiterCollectible || CollectibleBinding.IsBound(_predicate);

    public static WaitUntilPromise Create(Func<bool> predicate, bool completeWhenTrue, FrameTiming timing,
        CancellationToken ct, bool cancelImmediately, out short token)
    {
        if (!s_pool.TryPop(out WaitUntilPromise? result)) { result = new WaitUntilPromise(); }
        result._version = result._core.Version;
        result._predicate = predicate;
        result._completeWhenTrue = completeWhenTrue;
        result._cancellationToken = ct;
        token = result._version;
        if (cancelImmediately && ct.CanBeCanceled)
        {
            result._registration = ct.RegisterWithoutCaptureExecutionContext(s_cancelDelegate, result);
        }
        XTaskLoopHelper.AddAction(timing, result);
        return result;
    }

    private void OnCancel() => _core.TrySetCanceled(_cancellationToken);

    public bool MoveNext()
    {
        if (_cancellationToken.IsCancellationRequested)
        {
            _core.TrySetCanceled(_cancellationToken);
            return false;
        }

        // completeWhenTrue: WaitUntil. !completeWhenTrue: WaitWhile.
        bool done = _completeWhenTrue ? _predicate!() : !_predicate!();
        if (done)
        {
            _core.TrySetResult(AsyncUnit.Default);
            return false;
        }

        return true;
    }

    public XTaskStatus GetStatus(short token) => _core.GetStatus(token);
    public XTaskStatus UnsafeGetStatus() => _core.UnsafeGetStatus();
    public void OnCompleted(Action<object> continuation, object state, short token) => _core.OnCompleted(continuation, state, token);

    public void GetResult(short token)
    {
        try { _core.GetResult(token); }
        finally { TryReturn(); }
    }

    private bool TryReturn()
    {
        _registration.Dispose(); _registration = default;
        _cancellationToken = default;
        _predicate = null;
        _core.Reset();
        return s_pool.TryPush(this);
    }
}

internal sealed class WaitUntilCanceledPromise : IXTaskSource, IFrameLoopItem, ITaskPoolNode<WaitUntilCanceledPromise>, ICollectibleBoundItem
{
    private static TaskPool<WaitUntilCanceledPromise> s_pool;

    private XTaskCompletionSourceCore<AsyncUnit> _core;
    private WaitUntilCanceledPromise? _nextNode;
    private short _version;
    private CancellationToken _cancellationToken;

    static WaitUntilCanceledPromise() { TaskPool.RegisterSizeGetter(typeof(WaitUntilCanceledPromise), () => s_pool.Size); }
    private WaitUntilCanceledPromise() { }

    public ref WaitUntilCanceledPromise? NextNode => ref _nextNode;

    bool ICollectibleBoundItem.IsCollectibleBound => _core.IsAwaiterCollectible;

    public static WaitUntilCanceledPromise Create(CancellationToken ct, FrameTiming timing, out short token)
    {
        if (!s_pool.TryPop(out WaitUntilCanceledPromise? result)) { result = new WaitUntilCanceledPromise(); }
        result._version = result._core.Version;
        result._cancellationToken = ct;
        token = result._version;
        XTaskLoopHelper.AddAction(timing, result);
        return result;
    }

    public bool MoveNext()
    {
        if (_cancellationToken.IsCancellationRequested)
        {
            _core.TrySetResult(AsyncUnit.Default); // canceled → complete successfully.
            return false;
        }
        return true;
    }

    public XTaskStatus GetStatus(short token) => _core.GetStatus(token);
    public XTaskStatus UnsafeGetStatus() => _core.UnsafeGetStatus();
    public void OnCompleted(Action<object> continuation, object state, short token) => _core.OnCompleted(continuation, state, token);

    public void GetResult(short token)
    {
        try { _core.GetResult(token); }
        finally { TryReturn(); }
    }

    private bool TryReturn()
    {
        _cancellationToken = default;
        _core.Reset();
        return s_pool.TryPush(this);
    }
}
