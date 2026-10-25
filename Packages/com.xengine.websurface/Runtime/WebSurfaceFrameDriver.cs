using System;

namespace XEngine.WebSurface;

/// <summary>
/// Result of one driver tick (diagnostics for the live-debug overlay and the acceptance tools).
/// </summary>
public readonly record struct WebSurfaceDriverTick(
    bool Requested,
    ulong? PresentedFrameNumber,
    int BackendState);

/// <summary>
/// Frame-pacing scheduler for a <see cref="WebSurfaceSession"/>: requests production at most
/// <c>FramerateCapHz</c> times per second (accumulating time so a slow engine frame still gets
/// exactly one catch-up request, never a burst) and presents the newest frame every tick. Pure
/// logic so tests drive it without the engine loop; the <see cref="WebSurfaceDriverBehaviour"/>
/// MonoBehaviour shell calls into it from Update.
/// </summary>
public sealed class WebSurfaceFrameScheduler
{
    private readonly WebSurfaceQuality _quality;
    private float _accumulatorSeconds;

    public WebSurfaceFrameScheduler(WebSurfaceQuality quality)
    {
        _quality = quality;
        if (quality.FramerateCapHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(quality), "FramerateCapHz must be positive.");
    }

    /// <summary>Seconds between produce requests at the current cap.</summary>
    public float MinRequestIntervalSeconds => 1f / _quality.FramerateCapHz;

    /// <summary>Advances the scheduler; returns true exactly when a produce request is due.</summary>
    public bool ShouldRequest(float unscaledDeltaTimeSeconds)
    {
        _accumulatorSeconds += unscaledDeltaTimeSeconds;
        if (_accumulatorSeconds < MinRequestIntervalSeconds)
            return false;

        // One request per due tick: drop the whole accumulation (live content never queues).
        _accumulatorSeconds = 0f;
        return true;
    }

    /// <summary>Resets the accumulator (pause/resume/reload must not burst a catch-up request).</summary>
    public void Reset() => _accumulatorSeconds = 0f;
}

/// <summary>
/// Engine-facing driver for one bound session + graphic: requests frames on the scheduler and
/// presents the newest frame into the <see cref="WebSurfaceGraphic"/> every tick. Behaviour shell
/// is deliberately thin so the scheduling rules live in <see cref="WebSurfaceFrameScheduler"/>.
/// </summary>
public sealed class WebSurfaceFrameDriver
{
    private readonly WebSurfaceSession _session;
    private readonly WebSurfaceGraphic? _graphic;
    private readonly WebSurfaceFrameScheduler _scheduler;

    public WebSurfaceFrameDriver(WebSurfaceSession session, WebSurfaceGraphic? graphic)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _graphic = graphic;
        _scheduler = new WebSurfaceFrameScheduler(session.Asset?.Quality ?? WebSurfaceQuality.Default);
    }

    public WebSurfaceFrameScheduler Scheduler => _scheduler;

    /// <summary>One engine tick: at most one produce request, then one present.</summary>
    public WebSurfaceDriverTick Tick(float unscaledDeltaTimeSeconds)
    {
        bool requested = false;
        if (_session.State == WebSurfaceStateMachine.State.Running)
            requested = _session.RequestFrame();

        ulong? presented = null;
        if (_graphic is not null)
        {
            ulong frameNumber = _graphic.Present();
            if (frameNumber != ulong.MaxValue)
                presented = frameNumber;
        }
        return new WebSurfaceDriverTick(requested, presented, (int)_session.State);
    }
}
