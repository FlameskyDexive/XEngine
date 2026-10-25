// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;

namespace XEngine.Async.Internal;

/// <summary>
/// A lightweight value-type stopwatch built on <see cref="Stopwatch"/>. Used for realtime
/// (time-scale-independent) delays where <see cref="Runtime.Time"/> is not appropriate.
/// </summary>
internal readonly struct ValueStopwatch
{
    private readonly long _startTicks;

    private ValueStopwatch(long startTicks)
    {
        _startTicks = startTicks;
    }

    public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());

    /// <summary>True when the stopwatch was never started (default-constructed).</summary>
    public bool IsInvalid => _startTicks == 0;

    public TimeSpan Elapsed
    {
        get
        {
            if (IsInvalid)
            {
                throw Error.InvalidOperation("ValueStopwatch was not initialized; call StartNew().");
            }

            long elapsedTicks = Stopwatch.GetTimestamp() - _startTicks;
            double seconds = (double)elapsedTicks / Stopwatch.Frequency;
            return TimeSpan.FromSeconds(seconds);
        }
    }
}
