// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class CameraHistoryTests : IDisposable
{
    private readonly RHI.GraphicsBackend? _previousBackend;

    public CameraHistoryTests()
    {
        _previousBackend = RHI.GraphicsBackendSelection.Preferred;
        RHI.GraphicsBackendSelection.Preferred = RHI.GraphicsBackend.Null;
        Graphics.Initialize(debug: false);
    }

    public void Dispose()
    {
        Graphics.Dispose();
        RHI.GraphicsBackendSelection.Preferred = _previousBackend;
    }

    [Fact]
    public void Alloc_ReusesSameTargetWhenSizeAndFormatMatch()
    {
        using var history = new CameraHistory();
        TextureImageFormat[] formats = [TextureImageFormat.Color4b];
        RenderStats.BeginFrame();

        RenderTexture first = history.Alloc(CameraHistoryId.Color, 32, 24, false, formats);
        RenderTexture second = history.Alloc(CameraHistoryId.Color, 32, 24, false, formats);

        RenderStats.EndFrame();
        Assert.Same(first, second);
        Assert.Same(first, history.Get(CameraHistoryId.Color).RenderTexture);
        Assert.Equal(1, RenderStats.Last.RTHandleAllocations);
        Assert.Equal(1, RenderStats.Last.RTHandleReuses);
    }

    [Fact]
    public void Alloc_RebuildsWhenSizeChanges_IndependentSlots()
    {
        using var history = new CameraHistory();
        TextureImageFormat[] formats = [TextureImageFormat.Color4b];

        RenderTexture color = history.Alloc(CameraHistoryId.Color, 16, 16, false, formats);
        RenderTexture taa = history.Alloc(CameraHistoryId.Taa, 16, 16, false, formats);
        Assert.NotSame(color, taa);

        RenderTexture resized = history.Alloc(CameraHistoryId.Color, 32, 16, false, formats);
        Assert.NotSame(color, resized);
        Assert.Same(taa, history.Get(CameraHistoryId.Taa).RenderTexture);
        Assert.True(color.IsDisposed);
        Assert.False(taa.IsDisposed);
    }

    [Fact]
    public void Pipeline_ReusesHistoryInstancePerCamera()
    {
        var pipeline = new UniversalRenderPipeline();
        var a = new Camera();
        var b = new Camera();

        CameraHistory first = pipeline.GetHistory(a);
        CameraHistory again = pipeline.GetHistory(a);
        CameraHistory other = pipeline.GetHistory(b);

        Assert.Same(first, again);
        Assert.NotSame(first, other);
    }

    [Fact]
    public void Forward_StillSevenBuiltInPasses()
    {
        var pipeline = new UniversalRenderPipeline();
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
    }
}
