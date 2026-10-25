// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Playables;

/// <summary>
/// Time source for a <see cref="PlayableGraph"/>. Owns the global playhead position, a time scale,
/// a wrap mode, and an optional bounded duration. The graph advances the clock each frame via
/// <see cref="Advance"/>; scrubbing is performed by setting <see cref="CurrentTime"/> directly and
/// then calling <see cref="PlayableGraph.Sample"/>.
/// </summary>
/// <remarks>
/// Two concrete clocks cover the two consumers:
/// <list type="bullet">
///   <item><see cref="GameClock"/> — an unbounded accumulator driven by per-frame deltas. Used by
///   the Animator, where each state advances its own local time and the global clock only provides
///   a stable dt source.</item>
///   <item><see cref="ManualClock"/> — a bounded, scrubable playhead. Used by the Timeline
///   PlayableDirector, where the global time is the source of truth and every clip's local time is
///   derived from it.</item>
/// </list>
/// </remarks>
public abstract class Clock
{
    private double _currentTime;
    private int _pingPongDirection = 1; // +1 forward, -1 backward; only meaningful when Duration > 0.

    /// <summary>
    /// Current playhead position in seconds. Setting this directly performs a scrub (the graph
    /// picks it up on the next <see cref="PlayableGraph.Sample"/> or <see cref="PlayableGraph.Evaluate"/>).
    /// </summary>
    public double CurrentTime
    {
        get => _currentTime;
        set => _currentTime = ClampTime(value);
    }

    /// <summary>The scaled delta produced by the most recent <see cref="Advance"/> call.</summary>
    public float DeltaTime { get; private set; }

    /// <summary>Multiplier applied to incoming raw delta in <see cref="Advance"/>. Defaults to 1.</summary>
    public float TimeScale { get; set; } = 1f;

    /// <summary>How time behaves at the duration bounds. Defaults to <see cref="ClockWrapMode.Clamp"/>.</summary>
    public ClockWrapMode Wrap { get; set; } = ClockWrapMode.Clamp;

    /// <summary>
    /// Length of the clock's range in seconds. A value &lt;= 0 means unbounded: <see cref="Advance"/>
    /// just accumulates and <see cref="Wrap"/> is ignored.
    /// </summary>
    public double Duration { get; set; } = -1d;

    /// <summary>
    /// Hook invoked by the <see cref="CurrentTime"/> setter. Base implementation is a no-op;
    /// <see cref="ManualClock"/> overrides it to clamp scrubs into the configured range.
    /// </summary>
    protected virtual double ClampTime(double t) => t;

    /// <summary>
    /// Advances the playhead by <paramref name="rawDelta"/> seconds (scaled by
    /// <see cref="TimeScale"/>), applying the configured <see cref="Wrap"/> mode when bounded.
    /// Returns the scaled delta actually applied.
    /// </summary>
    public float Advance(float rawDelta)
    {
        float scaled = rawDelta * TimeScale;
        DeltaTime = scaled;

        if (Duration <= 0d)
        {
            _currentTime += scaled;
        }
        else
        {
            _currentTime = AdvanceBounded(_currentTime, scaled);
        }

        return scaled;
    }

    private double AdvanceBounded(double t, float delta)
    {
        t += delta * _pingPongDirection;

        switch (Wrap)
        {
            case ClockWrapMode.Loop:
                // Multi-subtract handles large catch-up deltas in one step.
                while (t >= Duration) t -= Duration;
                while (t < 0d) t += Duration;
                return t;

            case ClockWrapMode.PingPong:
                if (t >= Duration)
                {
                    _pingPongDirection = -1;
                    t = Duration + (Duration - t); // reflect around the upper bound
                }
                else if (t < 0d)
                {
                    _pingPongDirection = 1;
                    t = -t; // reflect around the lower bound
                }
                // A single step cannot legally bounce twice at frame-rate deltas; clamp the rest.
                if (t > Duration) t = Duration;
                else if (t < 0d) t = 0d;
                return t;

            case ClockWrapMode.Clamp:
            default:
                if (t > Duration) return Duration;
                if (t < 0d) return 0d;
                return t;
        }
    }
}

/// <summary>
/// An unbounded clock that accumulates per-frame deltas. The default for Animator-driven graphs,
/// where the global clock is only a stable dt source and each state owns its local time.
/// </summary>
public sealed class GameClock : Clock
{
    // No behavioural additions: an unbounded accumulator with Wrap.Clamp / Duration -1 by default.
}

/// <summary>
/// A bounded, scrubable clock. The Timeline PlayableDirector's playhead: scrubbing via
/// <see cref="Clock.CurrentTime"/> is clamped into [0, Duration], and the wrap mode controls
/// playback past the ends.
/// </summary>
public sealed class ManualClock : Clock
{
    /// <inheritdoc />
    protected override double ClampTime(double t) =>
        Duration > 0d ? Math.Clamp(t, 0d, Duration) : t;
}
