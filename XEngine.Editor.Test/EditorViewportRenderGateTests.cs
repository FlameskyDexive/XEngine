using XEngine.Editor.Core;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorViewportRenderGateTests
{
    [Fact]
    public void VisibleScheduledViewportRenders()
    {
        Assert.True(EditorViewportRenderGate.ShouldRender(true, true, 640, 480, true));
    }

    [Fact]
    public void HiddenOrZeroSizedViewportSkips()
    {
        Assert.False(EditorViewportRenderGate.ShouldRender(false, true, 640, 480, true));
        Assert.False(EditorViewportRenderGate.ShouldRender(true, false, 640, 480, true));
        Assert.False(EditorViewportRenderGate.ShouldRender(true, true, 0, 480, true));
        Assert.False(EditorViewportRenderGate.ShouldRender(true, true, 640, 0, true));
    }

    [Fact]
    public void UnscheduledViewportKeepsItsRetainedImage()
    {
        Assert.False(EditorViewportRenderGate.ShouldRender(true, true, 640, 480, false));
    }
}
