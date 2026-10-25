using System;

namespace XEngine.WebSurface;

/// <summary>Everything a recorded click is bound to (the plan's camera/projection/plane/generation
/// snapshot). Any component change invalidates the click except ordinary camera motion, which
/// re-issues the probe against the current view instead of starving it.</summary>
public readonly record struct WebSurfaceClickSnapshot(
    ulong SurfaceGeneration,
    long CameraId,
    long ProjectionStamp,
    long PlaneStamp)
{
    /// <summary>True when the click must be dropped outright (page/size changes): the surface is
    /// no longer the one the user clicked on. World-quad changes surface through PlaneStamp and
    /// only re-issue the probe (ordinary view/panel motion must not starve the click).</summary>
    public bool HardInvalidatedBy(WebSurfaceClickSnapshot current) =>
        SurfaceGeneration != current.SurfaceGeneration;

    /// <summary>True when only the view changed (camera/projection/plane motion): the click is
    /// not dropped — it is re-issued as a fresh probe against the current snapshot.</summary>
    public bool ViewMovedSince(WebSurfaceClickSnapshot current) =>
        CameraId != current.CameraId || ProjectionStamp != current.ProjectionStamp || PlaneStamp != current.PlaneStamp;
}

/// <summary>What the gate decided about the pending click for this tick.</summary>
public enum WebSurfaceClickGateAction : byte
{
    /// <summary>Nothing to do (no pending click, or the issued probe is still awaiting a verdict).</summary>
    Idle = 0,
    /// <summary>Record an occlusion probe this frame (fields carry the screen pixel + query id).</summary>
    IssueProbe = 1,
    /// <summary>The probe passed: forward the click to the surface now.</summary>
    Deliver = 2,
    /// <summary>The probe failed (panel occluded) or the click was invalidated: drop it.</summary>
    Drop = 3,
}

/// <summary>One gate tick's decision with its payload.</summary>
public readonly record struct WebSurfaceClickGateTick(
    WebSurfaceClickGateAction Action,
    ulong QueryId,
    int PixelX,
    int PixelY);

/// <summary>
/// B7b click gate: turns a world-panel click into a GPU visibility probe and only forwards the
/// click when the panel actually was visible at that pixel (occluded clicks are dropped).
/// Contract details: the click is bound to a snapshot; view motion (camera/projection/plane)
/// re-issues the probe at the recomputed pixel so ordinary camera movement never starves it;
/// surface/panel generation changes drop it outright. Probes are bounded (one issue per tick,
/// <paramref name="maxIssues"/> issues total) so a permanently occluded click cannot spin the
/// probe forever.
/// </summary>
public sealed class WebSurfaceClickGate
{
    private readonly int _maxIssues;
    private WebSurfaceClickSnapshot _pendingSnapshot;
    private int _pendingPixelX;
    private int _pendingPixelY;
    private int _issuedCount;
    private ulong _issuedQueryId;
    private bool _hasPending;
    private bool _issued;

    public WebSurfaceClickGate(int maxIssues = 60)
    {
        if (maxIssues < 1)
            throw new ArgumentOutOfRangeException(nameof(maxIssues));
        _maxIssues = maxIssues;
    }

    public bool HasPendingClick => _hasPending;
    public ulong IssuedQueryId => _issuedQueryId;
    public int IssuedCount => _issuedCount;

    private ulong _nextQueryId = 1;

    /// <summary>Queues a click. Replaces any pending click (a newer click wins — the surface
    /// only forwards one pointer stream).</summary>
    public void Submit(int pixelX, int pixelY, WebSurfaceClickSnapshot snapshot)
    {
        _pendingPixelX = pixelX;
        _pendingPixelY = pixelY;
        _pendingSnapshot = snapshot;
        _issued = false;
        _issuedCount = 0;
        _hasPending = true;
    }

    /// <summary>Drops any pending click (surface disposed, page reloaded, input cancelled).</summary>
    public void Cancel()
    {
        _hasPending = false;
        _issued = false;
    }

    /// <summary>
    /// Advances the gate one engine tick. <paramref name="probePixel"/>=false means the panel
    /// is off-screen for the current viewer (the probe cannot be issued this tick).
    /// <paramref name="resultAvailable"/>/<paramref name="passed"/> carry the device's verdict
    /// for the last issued query id.
    /// </summary>
    public WebSurfaceClickGateTick Tick(
        WebSurfaceClickSnapshot current,
        bool probePixelAvailable,
        int pixelX,
        int pixelY,
        bool resultAvailable,
        bool passed)
    {
        if (!_hasPending)
            return new WebSurfaceClickGateTick(WebSurfaceClickGateAction.Idle, 0, 0, 0);

        // Page/size/panel changes kill the click outright.
        if (_pendingSnapshot.HardInvalidatedBy(current))
        {
            Cancel();
            return new WebSurfaceClickGateTick(WebSurfaceClickGateAction.Drop, 0, 0, 0);
        }

        if (_issued)
        {
            // Verdict for the last issued probe.
            if (resultAvailable)
            {
                bool deliver = passed;
                Cancel();
                return new WebSurfaceClickGateTick(
                    deliver ? WebSurfaceClickGateAction.Deliver : WebSurfaceClickGateAction.Drop, 0, 0, 0);
            }

            // No verdict yet: allow one settle tick, then re-issue when the view moved the
            // panel's pixel (ordinary camera motion must not starve the click).
            bool viewMoved = _pendingSnapshot.ViewMovedSince(current) || pixelX != _pendingPixelX || pixelY != _pendingPixelY;
            if (!viewMoved)
                return new WebSurfaceClickGateTick(WebSurfaceClickGateAction.Idle, 0, 0, 0);
        }

        if (!probePixelAvailable)
            return new WebSurfaceClickGateTick(WebSurfaceClickGateAction.Idle, 0, 0, 0);

        if (_issuedCount >= _maxIssues)
        {
            Cancel();
            return new WebSurfaceClickGateTick(WebSurfaceClickGateAction.Drop, 0, 0, 0);
        }

        // (Re-)issue the probe at the current pixel.
        _pendingSnapshot = current;
        _pendingPixelX = pixelX;
        _pendingPixelY = pixelY;
        _issued = true;
        _issuedCount++;
        _issuedQueryId = _nextQueryId++;
        return new WebSurfaceClickGateTick(WebSurfaceClickGateAction.IssueProbe, _issuedQueryId, pixelX, pixelY);
    }
}
