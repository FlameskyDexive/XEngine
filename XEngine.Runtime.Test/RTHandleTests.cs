// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class RTHandleTests : IDisposable
{
    private readonly RHI.GraphicsBackend? _previousBackend;

    public RTHandleTests()
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
    public void ScaledSize_RoundsToNearestAndClamps()
    {
        Assert.Equal(960, RTHandle.ScaledSize(1920, 0.5f));
        Assert.Equal(540, RTHandle.ScaledSize(1080, 0.5f));
        Assert.Equal(1, RTHandle.ScaledSize(0, 1f));
        Assert.Equal(128, RTHandle.ScaledSize(128, 0f));
        Assert.Equal(256, RTHandle.ScaledSize(256, float.NaN));
    }

    [Fact]
    public void Alloc_ReusesSameTargetWhenSizeAndFormatMatch()
    {
        TextureImageFormat[] formats = [TextureImageFormat.Color4b];
        using var handle = new RTHandle("_TestColor");
        RenderStats.BeginFrame();

        RenderTexture first = handle.Alloc(32, 24, true, formats);
        RenderTexture second = handle.Alloc(32, 24, true, formats);

        RenderStats.EndFrame();
        Assert.Same(first, second);
        Assert.False(first.IsDisposed);
        Assert.Equal(1, RenderStats.Last.RTHandleAllocations);
        Assert.Equal(1, RenderStats.Last.RTHandleReuses);
    }

    [Fact]
    public void Alloc_RebuildsWhenSizeOrHdrFormatChanges()
    {
        using var handle = new RTHandle("_TestResize");
        RenderTexture small = handle.Alloc(16, 16, true, [TextureImageFormat.Color4b]);
        RenderTexture large = handle.Alloc(32, 16, true, [TextureImageFormat.Color4b]);

        Assert.NotSame(small, large);
        Assert.True(small.IsDisposed);
        Assert.Equal(32, large.Width);
        Assert.Equal(16, large.Height);

        RenderTexture hdr = handle.Alloc(32, 16, true, [TextureImageFormat.Short4]);
        Assert.NotSame(large, hdr);
        Assert.True(large.IsDisposed);
        Assert.Equal(TextureImageFormat.Short4, hdr.MainTexture.ImageFormat);
    }

    [Fact]
    public void AllocScaled_UsesReferenceSizeTimesScale()
    {
        using var handle = new RTHandle("_TestScale");
        using var system = new RTHandleSystem();
        system.SetReferenceSize(1920, 1080);

        RenderTexture half = handle.AllocScaled(
            system.ReferenceWidth, system.ReferenceHeight, 0.5f, true, [TextureImageFormat.Color4b]);

        Assert.Equal(960, half.Width);
        Assert.Equal(540, half.Height);
        Assert.Equal(0.5f, handle.Scale);
    }

    [Fact]
    public void DefaultPipeline_CameraHandles_ReuseAcrossAlloc()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.RTHandles.SetReferenceSize(64, 48);
        TextureImageFormat[] color = [TextureImageFormat.Color4b];

        RenderTexture a = pipeline.RTHandles.AllocCameraColor(true, color);
        RenderTexture b = pipeline.RTHandles.AllocCameraColor(true, color);
        RenderTexture prepass = pipeline.RTHandles.AllocCameraPrepass(
        [
            TextureImageFormat.Color4b,
            TextureImageFormat.Short4,
        ]);

        Assert.Same(a, b);
        Assert.Equal(64, a.Width);
        Assert.Equal(48, a.Height);
        Assert.Equal(RTHandleNames.CameraColor, pipeline.RTHandles.CameraColor.Name);
        Assert.Equal(RTHandleNames.CameraPrepass, pipeline.RTHandles.CameraPrepass.Name);
        Assert.NotNull(prepass.InternalDepth);
        Assert.NotNull(pipeline.RTHandles.CameraPrepass.NormalTexture);
        Assert.NotNull(pipeline.RTHandles.CameraPrepass.MotionTexture);

        pipeline.RTHandles.ReleaseAll();
    }

    [Fact]
    public void Alloc_Reuse_HasZeroSteadyStateAllocation()
    {
        TextureImageFormat[] formats = [TextureImageFormat.Color4b];
        using var handle = new RTHandle("_TestNoAlloc");
        handle.Alloc(32, 32, true, formats);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 256; i++)
            handle.Alloc(32, 32, true, formats);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
