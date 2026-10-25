// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using XEngine.Async.Internal;

namespace XEngine.Async;

/// <summary>The clock a <see cref="XTask.Delay(TimeSpan, DelayType, FrameTiming, CancellationToken, bool)"/> accumulates.</summary>
public enum DelayType : byte
{
    /// <summary>Use <see cref="Runtime.Time.DeltaTime"/> (scaled by <see cref="Runtime.Time.TimeScale"/>).</summary>
    DeltaTime = 0,

    /// <summary>Use <see cref="Runtime.Time.UnscaledDeltaTime"/> (ignores TimeScale, e.g. UI pauses).</summary>
    IgnoreTimeScale = 1,

    /// <summary>Use a wall-clock <see cref="ValueStopwatch"/> (real time, engine-clock-independent).</summary>
    Realtime = 2,
}

public readonly partial struct XTask
{
    /// <summary>Suspend until the first <paramref name="timing"/> phase of the next frame.</summary>
    public static XTask NextFrame(FrameTiming timing = FrameTiming.Update, CancellationToken cancellationToken = default, bool cancelImmediately = false)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return XTask.FromCanceled(cancellationToken);
        }

        var promise = NextFramePromise.Create(timing, cancellationToken, cancelImmediately, out short token);
        return new XTask(promise, token);
    }

    /// <summary>Suspend until <paramref name="delayFrameCount"/> frames have elapsed at <paramref name="timing"/>.</summary>
    public static XTask DelayFrame(int delayFrameCount, FrameTiming timing = FrameTiming.Update,
        CancellationToken cancellationToken = default, bool cancelImmediately = false)
    {
        if (delayFrameCount <= 0)
        {
            return XTask.CompletedTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return XTask.FromCanceled(cancellationToken);
        }

        var promise = DelayFramePromise.Create(delayFrameCount, timing, cancellationToken, cancelImmediately, out short token);
        return new XTask(promise, token);
    }

    /// <summary>Suspend for <paramref name="delayTimeSpan"/>, accumulating the chosen <paramref name="delayType"/> clock each frame.</summary>
    public static XTask Delay(TimeSpan delayTimeSpan, DelayType delayType = DelayType.DeltaTime,
        FrameTiming timing = FrameTiming.Update, CancellationToken cancellationToken = default, bool cancelImmediately = false)
    {
        float seconds = (float)delayTimeSpan.TotalSeconds;
        if (seconds <= 0f)
        {
            return XTask.CompletedTask;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return XTask.FromCanceled(cancellationToken);
        }

        var promise = DelayPromise.Create(seconds, delayType, timing, cancellationToken, cancelImmediately, out short token);
        return new XTask(promise, token);
    }

    /// <summary>Suspend for <paramref name="millisecondsDelay"/> milliseconds.</summary>
    public static XTask Delay(int millisecondsDelay, bool ignoreTimeScale = false,
        FrameTiming timing = FrameTiming.Update, CancellationToken cancellationToken = default, bool cancelImmediately = false)
    {
        return Delay(TimeSpan.FromMilliseconds(millisecondsDelay),
            ignoreTimeScale ? DelayType.IgnoreTimeScale : DelayType.DeltaTime,
            timing, cancellationToken, cancelImmediately);
    }
}

// ---- Pooled promises ----

internal sealed class NextFramePromise : IXTaskSource, IFrameLoopItem, ITaskPoolNode<NextFramePromise>, ICollectibleBoundItem
{
    private static TaskPool<NextFramePromise> s_pool;
    private static readonly Action<object?> s_cancelDelegate = static state => ((NextFramePromise)state!).OnCancel();

    private XTaskCompletionSourceCore<AsyncUnit> _core;
    private NextFramePromise? _nextNode;
    private short _version;
    private CancellationToken _cancellationToken;
    private CancellationTokenRegistration _registration;

    static NextFramePromise() { TaskPool.RegisterSizeGetter(typeof(NextFramePromise), () => s_pool.Size); }
    private NextFramePromise() { }

    public ref NextFramePromise? NextNode => ref _nextNode;

    bool ICollectibleBoundItem.IsCollectibleBound => _core.IsAwaiterCollectible;

    public static NextFramePromise Create(FrameTiming timing, CancellationToken ct, bool cancelImmediately, out short token)
    {
        if (!s_pool.TryPop(out NextFramePromise? result)) { result = new NextFramePromise(); }
        result._version = result._core.Version;
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

        _core.TrySetResult(AsyncUnit.Default); // first poll == next frame.
        return false;
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
        _core.Reset();
        return s_pool.TryPush(this);
    }
}

internal sealed class DelayFramePromise : IXTaskSource, IFrameLoopItem, ITaskPoolNode<DelayFramePromise>, ICollectibleBoundItem
{
    private static TaskPool<DelayFramePromise> s_pool;
    private static readonly Action<object?> s_cancelDelegate = static state => ((DelayFramePromise)state!).OnCancel();

    private XTaskCompletionSourceCore<AsyncUnit> _core;
    private DelayFramePromise? _nextNode;
    private short _version;
    private CancellationToken _cancellationToken;
    private CancellationTokenRegistration _registration;
    private int _remainingFrames;

    static DelayFramePromise() { TaskPool.RegisterSizeGetter(typeof(DelayFramePromise), () => s_pool.Size); }
    private DelayFramePromise() { }

    public ref DelayFramePromise? NextNode => ref _nextNode;

    bool ICollectibleBoundItem.IsCollectibleBound => _core.IsAwaiterCollectible;

    public static DelayFramePromise Create(int frames, FrameTiming timing, CancellationToken ct, bool cancelImmediately, out short token)
    {
        if (!s_pool.TryPop(out DelayFramePromise? result)) { result = new DelayFramePromise(); }
        result._version = result._core.Version;
        result._remainingFrames = frames;
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

        if (--_remainingFrames <= 0)
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
        _remainingFrames = 0;
        _core.Reset();
        return s_pool.TryPush(this);
    }
}

internal sealed class DelayPromise : IXTaskSource, IFrameLoopItem, ITaskPoolNode<DelayPromise>, ICollectibleBoundItem
{
    private static TaskPool<DelayPromise> s_pool;
    private static readonly Action<object?> s_cancelDelegate = static state => ((DelayPromise)state!).OnCancel();

    private XTaskCompletionSourceCore<AsyncUnit> _core;
    private DelayPromise? _nextNode;
    private short _version;
    private CancellationToken _cancellationToken;
    private CancellationTokenRegistration _registration;
    private float _threshold;
    private float _elapsed;
    private DelayType _delayType;
    private ValueStopwatch _stopwatch; // for Realtime

    static DelayPromise() { TaskPool.RegisterSizeGetter(typeof(DelayPromise), () => s_pool.Size); }
    private DelayPromise() { }

    public ref DelayPromise? NextNode => ref _nextNode;

    bool ICollectibleBoundItem.IsCollectibleBound => _core.IsAwaiterCollectible;

    public static DelayPromise Create(float seconds, DelayType type, FrameTiming timing, CancellationToken ct, bool cancelImmediately, out short token)
    {
        if (!s_pool.TryPop(out DelayPromise? result)) { result = new DelayPromise(); }
        result._version = result._core.Version;
        result._threshold = seconds;
        result._elapsed = 0f;
        result._delayType = type;
        result._stopwatch = type == DelayType.Realtime ? ValueStopwatch.StartNew() : default;
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

        float dt = _delayType switch
        {
            DelayType.DeltaTime => Runtime.Time.DeltaTime,
            DelayType.IgnoreTimeScale => Runtime.Time.UnscaledDeltaTime,
            _ => (float)_stopwatch.Elapsed.TotalSeconds, // Realtime: total since start
        };

        if (_delayType == DelayType.Realtime)
        {
            // _stopwatch.Elapsed is total elapsed; compare directly to threshold.
            if (dt >= _threshold)
            {
                _core.TrySetResult(AsyncUnit.Default);
                return false;
            }
        }
        else
        {
            _elapsed += dt;
            if (_elapsed >= _threshold)
            {
                _core.TrySetResult(AsyncUnit.Default);
                return false;
            }
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
        _threshold = 0f; _elapsed = 0f; _stopwatch = default;
        _core.Reset();
        return s_pool.TryPush(this);
    }
}
