using XEngine.Editor.GUI.SceneView;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorCameraResizeTests
{
    [Fact]
    public void ResizeKeepsPreviousTargetDuringDebounceThenReplacesIt()
    {
        TimeData time = Time.CurrentTime;
        time.UnscaledTotalTime = 0;
        using var camera = new EditorCamera();

        camera.EnsureRenderTarget(320, 200);
        var original = camera.RenderTarget;
        Assert.NotNull(original);

        camera.EnsureRenderTarget(640, 400);
        Assert.Same(original, camera.RenderTarget);

        time.UnscaledTotalTime = 0.13f;
        camera.EnsureRenderTarget(640, 400);
        Assert.NotSame(original, camera.RenderTarget);
        Assert.Equal(640, camera.RenderTarget!.Width);
        Assert.Equal(400, camera.RenderTarget.Height);
    }
}
