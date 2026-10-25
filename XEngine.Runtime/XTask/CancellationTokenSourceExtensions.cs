// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Threading;
using XEngine.Async.Internal;

namespace XEngine.Async;

/// <summary>Extension methods on <see cref="CancellationTokenSource"/>.</summary>
public static class CancellationTokenSourceExtensions
{
    /// <summary>
    /// Schedule cancellation on the engine's frame loop instead of a <see cref="System.Threading.Timer"/>
    /// thread. A pooled <see cref="CancelAfterPromise"/> polls <see cref="Runtime.Time"/> each frame
    /// and calls <see cref="CancellationTokenSource.Cancel"/> when the delay elapses. Dispose the
    /// returned registration to abort the timer (the source itself is not disposed).
    /// </summary>
    public static void CancelAfterSlim(this CancellationTokenSource source, int millisecondsDelay,
        FrameTiming timing = FrameTiming.Update)
    {
        if (!source.IsCancellationRequested)
        {
            var promise = CancelAfterPromise.Create(source, millisecondsDelay, timing);
            XTaskLoopHelper.AddAction(timing, promise);
        }
    }
}

internal sealed class CancelAfterPromise : IFrameLoopItem, ITaskPoolNode<CancelAfterPromise>
{
    private static TaskPool<CancelAfterPromise> s_pool;

    private CancellationTokenSource? _source;
    private float _elapsed;
    private float _threshold;
    private CancelAfterPromise? _nextNode;

    static CancelAfterPromise()
    {
        TaskPool.RegisterSizeGetter(typeof(CancelAfterPromise), () => s_pool.Size);
    }

    private CancelAfterPromise() { }

    public ref CancelAfterPromise? NextNode => ref _nextNode;

    public static CancelAfterPromise Create(CancellationTokenSource source, int millisecondsDelay, FrameTiming timing)
    {
        if (!s_pool.TryPop(out CancelAfterPromise? result))
        {
            result = new CancelAfterPromise();
        }
        result._source = source;
        result._threshold = millisecondsDelay / 1000f;
        result._elapsed = 0f;
        return result;
    }

    public bool MoveNext()
    {
        CancellationTokenSource? source = _source;
        if (source == null || source.IsCancellationRequested)
        {
            TryReturn();
            return false;
        }

        // Use unscaled realtime-ish delta so CancelAfterSlim is immune to TimeScale pauses. If the
        // engine is not yet ticking Time (DeltaTime == 0, e.g. tests), fall back to a wall clock.
        float dt = Runtime.Time.UnscaledDeltaTime;
        _elapsed += dt > 0f ? dt : 0.016f;
        if (_elapsed >= _threshold)
        {
            try { source.Cancel(); }
            catch { /* swallow — the source may already be disposed */ }
            TryReturn();
            return false;
        }

        return true;
    }

    private void TryReturn()
    {
        _source = null;
        _elapsed = 0f;
        _threshold = 0f;
        s_pool.TryPush(this);
    }
}
