// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class RenderStatsObservabilityTests
{
    [Fact]
    public void FrameCapturesSubmissionClearBlitAndTransientAttachmentCost()
    {
        RenderStats.BeginFrame();
        RenderStats.RecordCommandBufferSubmission("ColorPass");
        RenderStats.RecordCommandBufferSubmission("DirectionalLightCascade0");
        RenderStats.RecordClear(ClearFlags.Color | ClearFlags.Depth);
        RenderStats.RecordBlit(16, 8);
        RenderStats.RecordTransientRenderTarget(
            16, 8, true, new[] { TextureImageFormat.Color4b, TextureImageFormat.Short4 });
        RenderStats.EndFrame();

        RenderStats.Frame frame = RenderStats.Last;
        Assert.Equal(2, frame.CommandBufferSubmissions);
        Assert.Equal(1, frame.ColorPassSubmissions);
        Assert.Equal(1, frame.ShadowPassSubmissions);
        Assert.Equal(1, frame.ClearOperations);
        Assert.Equal(1, frame.ColorClears);
        Assert.Equal(1, frame.DepthClears);
        Assert.Equal(1, frame.BlitOperations);
        Assert.Equal(128, frame.BlitPixels);
        Assert.Equal(1, frame.TransientRenderTargetCount);
        Assert.Equal(16L * 8 * (4 + 8 + 4), frame.TransientRenderTargetBytes);
        Assert.False(RenderStats.IsFrameActive);
    }

    [Fact]
    public void FrameCapturesRetainedModeUiCountersWithoutLeakingAcrossFrames()
    {
        RenderStats.BeginFrame();
        RenderStats.BeginUIPass();
        RenderStats.RecordCanvasCollection(2);
        RenderStats.RecordDirtyLayoutRoot();
        RenderStats.RecordLayoutRebuild();
        RenderStats.RecordGraphicRebuild();
        RenderStats.RecordMaterialUpdate();
        RenderStats.RecordMeshUpload(4, 6, 176);
        RenderStats.RecordUIDraw(4, 6);
        RenderStats.RecordUIBatch();
        RenderStats.RecordClipStateChange(false);
        RenderStats.EndUIPass();
        RenderStats.EndFrame();

        RenderStats.Frame frame = RenderStats.Last;
        Assert.Equal(1, frame.CanvasCount);
        Assert.Equal(2, frame.ActiveGraphics);
        Assert.Equal(1, frame.DirtyLayoutRoots);
        Assert.Equal(1, frame.LayoutRebuilds);
        Assert.Equal(1, frame.GraphicRebuilds);
        Assert.Equal(1, frame.MaterialUpdates);
        Assert.Equal(1, frame.MeshUploads);
        Assert.Equal(176, frame.UploadedBytes);
        Assert.Equal(4, frame.UIVertices);
        Assert.Equal(6, frame.UIIndices);
        Assert.Equal(1, frame.UIDrawCalls);
        Assert.Equal(1, frame.UIBatches);
        Assert.Equal(1, frame.ClipStateChanges);
        Assert.Equal(0, frame.StencilPasses);

        RenderStats.BeginFrame();
        RenderStats.EndFrame();
        Assert.Equal(0, RenderStats.Last.MeshUploads);
        Assert.Equal(0, RenderStats.Last.UIDrawCalls);
    }

    [Fact]
    public void StencilClipIsCountedSeparatelyFromRectClip()
    {
        RenderStats.BeginFrame();
        RenderStats.RecordClipStateChange(true);
        RenderStats.EndFrame();

        Assert.Equal(1, RenderStats.Last.ClipStateChanges);
        Assert.Equal(1, RenderStats.Last.StencilPasses);
    }
}
