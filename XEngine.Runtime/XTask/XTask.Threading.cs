// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace XEngine.Async;

public readonly partial struct XTask
{
    /// <summary>
    /// An awaitable that, when the caller is NOT on the main thread, suspends and resumes on the
    /// next <paramref name="timing"/> tick (i.e. on the main thread). If the caller is already on
    /// the main thread it completes synchronously with no suspension.
    /// </summary>
    public static SwitchToMainThreadAwaitable SwitchToMainThread(FrameTiming timing = FrameTiming.Update,
        CancellationToken cancellationToken = default)
        => new SwitchToMainThreadAwaitable(timing, cancellationToken);

    /// <summary>An awaitable that suspends and resumes on the threadpool.</summary>
    public static SwitchToThreadPoolAwaitable SwitchToThreadPool() => default;

    /// <summary>Enqueue <paramref name="action"/> to run on the next <paramref name="timing"/> tick.</summary>
    public static void Post(Action action, FrameTiming timing = FrameTiming.Update)
        => XTaskLoopHelper.AddContinuation(timing, action);
}

/// <summary>See <see cref="XTask.SwitchToMainThread"/>.</summary>
public readonly struct SwitchToMainThreadAwaitable
{
    private readonly FrameTiming _timing;
    private readonly CancellationToken _cancellationToken;

    internal SwitchToMainThreadAwaitable(FrameTiming timing, CancellationToken cancellationToken)
    {
        _timing = timing;
        _cancellationToken = cancellationToken;
    }

    public Awaiter GetAwaiter() => new Awaiter(this);

    public readonly struct Awaiter : ICriticalNotifyCompletion
    {
        private readonly FrameTiming _timing;
        private readonly CancellationToken _cancellationToken;

        internal Awaiter(SwitchToMainThreadAwaitable owner)
        {
            _timing = owner._timing;
            _cancellationToken = owner._cancellationToken;
        }

        /// <summary>True when already on the main thread (no suspension needed).</summary>
        public bool IsCompleted
            => XTaskLoopHelper.IsInitialized && Environment.CurrentManagedThreadId == XTaskLoopHelper.MainThreadId;

        public void GetResult() => _cancellationToken.ThrowIfCancellationRequested();

        public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation)
            => XTaskLoopHelper.AddContinuation(_timing, continuation);
    }
}

/// <summary>See <see cref="XTask.SwitchToThreadPool"/>.</summary>
public readonly struct SwitchToThreadPoolAwaitable
{
    public Awaiter GetAwaiter() => default;

    public readonly struct Awaiter : ICriticalNotifyCompletion
    {
        // Always suspend: the whole point is to leave the current thread.
        public bool IsCompleted => false;

        public void GetResult() { }

        public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation)
        {
            // UnsafeQueueUserWorkItem avoids ExecutionContext capture; the static lambda is cached by
            // the compiler so there is no per-await closure allocation.
            ThreadPool.UnsafeQueueUserWorkItem(static state => ((Action)state!).Invoke(), continuation);
        }
    }
}
