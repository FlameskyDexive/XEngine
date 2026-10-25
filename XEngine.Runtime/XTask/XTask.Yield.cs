// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace XEngine.Async;

// The cheapest "yield one frame" path: a struct YieldAwaitable whose awaiter enqueues the
// continuation directly onto the ContinuationQueue for the chosen FrameTiming. No IXTaskSource, no
// pooling — the continuation simply runs once on the next Tick of that phase.

/// <summary>
/// An awaitable that suspends the caller and resumes it on the next <see cref="XTaskLoopHelper.Tick"/>
/// of a chosen <see cref="FrameTiming"/>. Returned by <see cref="XTask.Yield()"/>.
/// </summary>
public readonly struct YieldAwaitable
{
    private readonly FrameTiming _timing;

    internal YieldAwaitable(FrameTiming timing)
    {
        _timing = timing;
    }

    public Awaiter GetAwaiter() => new Awaiter(this);

    public readonly struct Awaiter : ICriticalNotifyCompletion
    {
        private readonly FrameTiming _timing;

        internal Awaiter(YieldAwaitable owner)
        {
            _timing = owner._timing;
        }

        /// <summary>Always false: a yield always suspends so the continuation runs next frame.</summary>
        public bool IsCompleted => false;

        public void GetResult() { }

        public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation)
            => XTaskLoopHelper.AddContinuation(_timing, continuation);
    }
}

public readonly partial struct XTask
{
    /// <summary>Suspend until the next <see cref="FrameTiming.Update"/> phase.</summary>
    public static YieldAwaitable Yield() => new YieldAwaitable(FrameTiming.Update);

    /// <summary>Suspend until the next <paramref name="timing"/> phase.</summary>
    public static YieldAwaitable Yield(FrameTiming timing) => new YieldAwaitable(timing);
}
