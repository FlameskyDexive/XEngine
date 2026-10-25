// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Stopwatch = System.Diagnostics.Stopwatch;

namespace XEngine.Runtime;

public class TimeData
{
    public float UnscaledDeltaTime;
    public float UnscaledTotalTime;
    public float DeltaTime;
    public float Time;
    public float SmoothUnscaledDeltaTime;
    public float SmoothDeltaTime;

    private Stopwatch _stopwatch;

    public TimeData() { }

    public long FrameCount;

    public float TimeScale = 1f;
    public float TimeSmoothFactor = .25f;

    public void Update()
    {
        _stopwatch ??= Stopwatch.StartNew();

        float dt = (float)_stopwatch.Elapsed.TotalMilliseconds / 1000.0f;

        FrameCount++;

        UnscaledDeltaTime = dt;
        UnscaledTotalTime += UnscaledDeltaTime;

        DeltaTime = dt * TimeScale;
        Time += DeltaTime;

        SmoothUnscaledDeltaTime += (dt - SmoothUnscaledDeltaTime) * TimeSmoothFactor;
        SmoothDeltaTime = SmoothUnscaledDeltaTime * TimeScale;

        _stopwatch.Restart();
    }
}

public static class Time
{
    private static readonly TimeData s_defaultTime = new();

    public static Stack<TimeData> TimeStack { get; } = new();

    public static TimeData CurrentTime => TimeStack.Count > 0 ? TimeStack.Peek() : s_defaultTime;

    public static float UnscaledDeltaTime => CurrentTime.UnscaledDeltaTime;
    public static float UnscaledTotalTime => CurrentTime.UnscaledTotalTime;

    public static float DeltaTime => CurrentTime.DeltaTime;
    public static float FixedDeltaTime = 1.0f / 60.0f;
    public static int MaxFixedIterations = 3;
    public static float TimeSinceStartup => CurrentTime.Time;

    /// <summary>
    /// How far the current frame has advanced past the most recent fixed step, as a fraction of
    /// <see cref="FixedDeltaTime"/> in the range [0, 1).
    /// </summary>
    /// <remarks>
    /// The leftover of the fixed-step accumulator, published once per frame after the fixed loop and
    /// before the update loop. This is the interpolation factor for anything that has to present a
    /// simulation running at a different rate than the display — <see cref="RigidbodyInterpolation"/>
    /// is the one consumer today.
    /// <para>
    /// Zero when nothing drives a fixed loop, which is the case in tests that call
    /// <c>Scene.FixedUpdate</c> directly. Interpolated bodies then read exactly the last stepped pose,
    /// which keeps such tests deterministic.
    /// </para>
    /// </remarks>
    public static float FixedAlpha { get; internal set; }

    public static float SmoothUnscaledDeltaTime => CurrentTime.SmoothUnscaledDeltaTime;
    public static float SmoothDeltaTime => CurrentTime.SmoothDeltaTime;

    public static long FrameCount => CurrentTime.FrameCount;

    public static float TimeScale
    {
        get => CurrentTime.TimeScale;
        set => CurrentTime.TimeScale = value;
    }

    public static float TimeSmoothFactor
    {
        get => CurrentTime.TimeSmoothFactor;
        set => CurrentTime.TimeSmoothFactor = value;
    }
}
