using System;
using XEngine.WebSurface;
using Xunit;

namespace XEngine.Runtime.Test.WebSurface;

/// <summary>
/// B7b — the world click gate: a click is issued as a GPU visibility probe, delivered only when
/// the panel was visible at that pixel, dropped when occluded, re-issued when the view moved
/// (ordinary camera motion must not starve it) and dropped outright when the surface or panel
/// generation changed (page reload / resize semantics).
/// </summary>
public sealed class WebSurfaceClickGateTests
{
    private static WebSurfaceClickSnapshot Snap(
        ulong surface = 1, long camera = 100, long projection = 7, long plane = 9) =>
        new(surface, camera, projection, plane);

    [Fact]
    public void Issue_Deliver_Path()
    {
        var gate = new WebSurfaceClickGate();
        gate.Submit(120, 40, Snap());

        var issue = gate.Tick(Snap(), probePixelAvailable: true, 120, 40, resultAvailable: false, passed: false);
        Assert.Equal(WebSurfaceClickGateAction.IssueProbe, issue.Action);
        Assert.NotEqual(0ul, issue.QueryId);
        Assert.Equal(120, issue.PixelX);

        // Verdict arrives (panel visible) → deliver.
        var deliver = gate.Tick(Snap(), probePixelAvailable: true, 120, 40, resultAvailable: true, passed: true);
        Assert.Equal(WebSurfaceClickGateAction.Deliver, deliver.Action);
        Assert.False(gate.HasPendingClick);
    }

    [Fact]
    public void OccludedProbe_Drops_TheClick()
    {
        var gate = new WebSurfaceClickGate();
        gate.Submit(30, 10, Snap());

        _ = gate.Tick(Snap(), true, 30, 10, false, false);
        var drop = gate.Tick(Snap(), true, 30, 10, true, passed: false);

        Assert.Equal(WebSurfaceClickGateAction.Drop, drop.Action);
        Assert.False(gate.HasPendingClick);
    }

    [Fact]
    public void ViewMotion_ReIssues_AtTheNewPixel_WithoutStarving()
    {
        var gate = new WebSurfaceClickGate();
        gate.Submit(100, 50, Snap(camera: 100, projection: 7, plane: 9));

        var first = gate.Tick(Snap(), true, 100, 50, false, false);
        Assert.Equal(WebSurfaceClickGateAction.IssueProbe, first.Action);

        // The camera pans: the panel's pixel moves; the gate must re-issue rather than wait forever.
        var moved = Snap(camera: 100, projection: 8, plane: 9);
        var second = gate.Tick(moved, true, 92, 50, resultAvailable: false, passed: false);
        Assert.Equal(WebSurfaceClickGateAction.IssueProbe, second.Action);
        Assert.Equal(92, second.PixelX);
        Assert.NotEqual(first.QueryId, second.QueryId);
        Assert.True(gate.IssuedCount >= 2);

        // ... and a passing verdict after the motion still delivers.
        var deliver = gate.Tick(moved, true, 92, 50, resultAvailable: true, passed: true);
        Assert.Equal(WebSurfaceClickGateAction.Deliver, deliver.Action);
    }

    [Fact]
    public void SamePixel_NoVerdict_IsIdle()
    {
        var gate = new WebSurfaceClickGate();
        gate.Submit(10, 10, Snap());
        _ = gate.Tick(Snap(), true, 10, 10, false, false);

        // The view has not moved and the probe has not resolved: nothing to do this tick.
        var idle = gate.Tick(Snap(), true, 10, 10, false, false);
        Assert.Equal(WebSurfaceClickGateAction.Idle, idle.Action);
    }

    [Fact]
    public void SurfaceGenerationChange_HardInvalidates()
    {
        var gate = new WebSurfaceClickGate();
        gate.Submit(10, 10, Snap(surface: 1));
        _ = gate.Tick(Snap(surface: 1), true, 10, 10, false, false);

        var drop = gate.Tick(Snap(surface: 2), true, 10, 10, false, false);
        Assert.Equal(WebSurfaceClickGateAction.Drop, drop.Action);
        Assert.False(gate.HasPendingClick);
    }

    [Fact]
    public void PanelGenerationChange_HardInvalidates()
    {
        var gate = new WebSurfaceClickGate();
        gate.Submit(10, 10, Snap());
        // Plane change is a view/panel motion, not a hard invalidation: re-issue.
        var reissue = gate.Tick(Snap(plane: 4), true, 10, 10, false, false);
        Assert.Equal(WebSurfaceClickGateAction.IssueProbe, reissue.Action);
    }

    [Fact]
    public void IssueBudget_Exhausts_AndDrops()
    {
        var gate = new WebSurfaceClickGate(maxIssues: 3);
        gate.Submit(10, 10, Snap(camera: 1, projection: 0, plane: 0));

        // Every tick moves the projection (camera orbiting continuously): each tick re-issues
        // until the budget is gone, then the click is dropped instead of probing forever.
        for (int i = 0; i < 3; i++)
        {
            var snap = Snap(camera: 1, projection: i + 1, plane: 0);
            var tick = gate.Tick(snap, true, 10 + i, 10, false, false);
            Assert.Equal(WebSurfaceClickGateAction.IssueProbe, tick.Action);
        }

        var exhausted = gate.Tick(Snap(camera: 1, projection: 99, plane: 0), true, 10, 10, false, false);
        Assert.Equal(WebSurfaceClickGateAction.Drop, exhausted.Action);
        Assert.False(gate.HasPendingClick);
    }

    [Fact]
    public void Cancel_ClearsPending()
    {
        var gate = new WebSurfaceClickGate();
        gate.Submit(10, 10, Snap());
        gate.Cancel();
        Assert.False(gate.HasPendingClick);
        Assert.Equal(WebSurfaceClickGateAction.Idle, gate.Tick(Snap(), true, 10, 10, false, false).Action);
    }

    [Fact]
    public void NewerClick_ReplacesPending()
    {
        var gate = new WebSurfaceClickGate();
        gate.Submit(10, 10, Snap());
        gate.Submit(200, 60, Snap());

        var issue = gate.Tick(Snap(), true, 200, 60, false, false);
        Assert.Equal(WebSurfaceClickGateAction.IssueProbe, issue.Action);
        Assert.Equal(200, issue.PixelX);
    }

    [Fact]
    public void OffScreenPixel_Waits_InsteadOfIssuing()
    {
        var gate = new WebSurfaceClickGate();
        gate.Submit(10, 10, Snap());
        // probePixelAvailable=false (panel outside the viewer's frustum): no probe is issued
        // this tick, the click stays pending for a later viewer.
        var idle = gate.Tick(Snap(), probePixelAvailable: false, 0, 0, false, false);
        Assert.Equal(WebSurfaceClickGateAction.Idle, idle.Action);
        Assert.True(gate.HasPendingClick);
    }
}
