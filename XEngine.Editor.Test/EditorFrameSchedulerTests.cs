using XEngine.Editor.Core;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorFrameSchedulerTests
{
    [Fact]
    public void QueuedInvalidationRendersOnceThenReturnsIdle()
    {
        var scheduler = new EditorFrameScheduler();
        scheduler.Invalidate(EditorSurfaceDirtyReason.Layout | EditorSurfaceDirtyReason.Theme);

        Assert.True(scheduler.ShouldRender(1));
        Assert.Equal(EditorSurfaceSchedulerState.Idle, scheduler.State);
        Assert.Equal(EditorSurfaceDirtyReason.Layout | EditorSurfaceDirtyReason.Theme, scheduler.LastRenderReasons);
        Assert.False(scheduler.ShouldRender(2));
    }

    [Fact]
    public void InteractiveAndAnimatedRequestsKeepTheirStatesSeparate()
    {
        var scheduler = new EditorFrameScheduler();
        scheduler.RequestInteractiveFrame();

        Assert.True(scheduler.ShouldRender(0));
        Assert.Equal(EditorSurfaceSchedulerState.Interactive, scheduler.State);

        scheduler.ReleaseInteractiveFrame();
        scheduler.RegisterAnimation(10);
        Assert.True(scheduler.ShouldRender(5));
        Assert.Equal(EditorSurfaceSchedulerState.Animated, scheduler.State);
        Assert.False(scheduler.ShouldRender(10));
        Assert.Equal(EditorSurfaceSchedulerState.Idle, scheduler.State);
    }

    [Fact]
    public void UnfocusedSurfaceOnlyRendersQueuedInvalidationsAndResumesOnFocus()
    {
        var scheduler = new EditorFrameScheduler();
        scheduler.SetFocused(false);

        Assert.False(scheduler.ShouldRender(0));
        Assert.Equal(EditorSurfaceSchedulerState.Unfocused, scheduler.State);

        scheduler.Invalidate(EditorSurfaceDirtyReason.Input);
        Assert.True(scheduler.ShouldRender(1));
        Assert.Equal(EditorSurfaceSchedulerState.Unfocused, scheduler.State);

        scheduler.SetFocused(true);
        Assert.True(scheduler.ShouldRender(2));
        Assert.Equal(EditorSurfaceSchedulerState.Idle, scheduler.State);
        Assert.Equal(EditorSurfaceDirtyReason.Window, scheduler.LastRenderReasons);
    }

    [Fact]
    public void KeepUiLive_WhenFocusedHoveredOrMenuOpen()
    {
        Assert.False(EditorUiRenderGate.ShouldKeepUiLive(false, false, false));
        Assert.True(EditorUiRenderGate.ShouldKeepUiLive(true, false, false));
        Assert.True(EditorUiRenderGate.ShouldKeepUiLive(false, true, false));
        Assert.True(EditorUiRenderGate.ShouldKeepUiLive(false, false, true));
    }

    [Fact]
    public void PaperClickFollowUp_InvalidatesPendingEdgeAndConsumedClick()
    {
        Assert.False(EditorUiRenderGate.ShouldInvalidateForPaperInput(false, false));
        Assert.True(EditorUiRenderGate.ShouldInvalidateForPaperInput(true, false));
        Assert.True(EditorUiRenderGate.ShouldInvalidateForPaperInput(false, true));
        Assert.True(EditorUiRenderGate.ShouldRegisterClickFollowUpAnimation(true));
        Assert.False(EditorUiRenderGate.ShouldRegisterClickFollowUpAnimation(false));
    }

    [Fact]
    public void PaperClickFollowUp_KeepsRenderingUntilAnimationWindowEnds()
    {
        var scheduler = new EditorFrameScheduler();
        // Click frame already consumed the edge (no leftover invalidation). The follow-up
        // paint + short animation window must still present, or tree expand waits for a
        // mouse move.
        scheduler.Invalidate(EditorSurfaceDirtyReason.Input);
        scheduler.RegisterAnimation(1.0 + EditorUiRenderGate.PaperClickFollowUpSeconds);

        Assert.True(scheduler.ShouldRender(1.0));
        Assert.True(scheduler.ShouldRender(1.1));
        Assert.False(scheduler.ShouldRender(1.0 + EditorUiRenderGate.PaperClickFollowUpSeconds));
        Assert.Equal(EditorSurfaceSchedulerState.Idle, scheduler.State);
    }
}
