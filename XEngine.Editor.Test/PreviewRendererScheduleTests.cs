// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.GUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class PreviewRendererScheduleTests
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, false)] // hover alone no longer renders; orbit marks dirty
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, false, false)]
    public void ShouldRenderPreview_TruthTable(bool dirty, bool hovered, bool livePlaying, bool expected)
        => Assert.Equal(expected, PreviewRenderer.ShouldRenderPreview(dirty, hovered, livePlaying));

    [Fact]
    public void ApplyPreviewCameraSettings_DisablesPostAndShadows()
    {
        using var go = new GameObject("PreviewCam");
        var camera = go.AddComponent<Camera>();
        PreviewRenderer.ApplyPreviewCameraSettings(camera);

        Assert.Equal(CameraClearFlags.SolidColor, camera.ClearFlags);
        var extra = UniversalAdditionalCameraData.Find(camera);
        Assert.NotNull(extra);
        Assert.False(extra!.RenderPostProcessing);
        Assert.False(extra.RenderShadows);
        Assert.Equal(CameraAntialiasing.None, extra.Antialiasing);
    }
}
