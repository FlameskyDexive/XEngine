// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;
using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class StageAPerformanceContractTests : IDisposable
{
    private readonly QualitySettings.QualityTier _tier = QualitySettings.Tier;
    private readonly int _shadowAtlasSize = QualitySettings.ShadowAtlasSize;
    private readonly float _shadowDistance = QualitySettings.ShadowDistance;
    private readonly bool _shadowsEnabled = QualitySettings.ShadowsEnabled;
    private readonly bool _imageEffectsEnabled = QualitySettings.ImageEffectsEnabled;
    private readonly QualitySettings.AntiAliasingMode _antiAliasing = QualitySettings.AntiAliasing;

    public void Dispose()
    {
        QualitySettings.Tier = _tier;
        QualitySettings.ShadowAtlasSize = _shadowAtlasSize;
        QualitySettings.ShadowDistance = _shadowDistance;
        QualitySettings.ShadowsEnabled = _shadowsEnabled;
        QualitySettings.ImageEffectsEnabled = _imageEffectsEnabled;
        QualitySettings.AntiAliasing = _antiAliasing;
    }

    [Fact]
    public void QualityTiers_ApplyPreviewAndPlayerDefaults()
    {
        long initialVersion = QualitySettings.Version;

        QualitySettings.ApplyEditorDefaults();

        Assert.Equal(QualitySettings.QualityTier.Preview, QualitySettings.Tier);
        Assert.Equal(2048, QualitySettings.ShadowAtlasSize);
        Assert.Equal(100f, QualitySettings.ShadowDistance);
        Assert.True(QualitySettings.ShadowsEnabled);
        Assert.False(QualitySettings.ImageEffectsEnabled);
        Assert.Equal(QualitySettings.AntiAliasingMode.Disabled, QualitySettings.AntiAliasing);
        Assert.True(QualitySettings.Version > initialVersion);

        QualitySettings.ApplyPlayerDefaults();

        Assert.Equal(QualitySettings.QualityTier.Full, QualitySettings.Tier);
        Assert.Equal(4096, QualitySettings.ShadowAtlasSize);
        Assert.Equal(100f, QualitySettings.ShadowDistance);
        Assert.True(QualitySettings.ShadowsEnabled);
        Assert.True(QualitySettings.ImageEffectsEnabled);
        Assert.Equal(QualitySettings.AntiAliasingMode.Disabled, QualitySettings.AntiAliasing);
    }

    [Fact]
    public void ShadowDistance_IsClampedToSupportedRange()
    {
        QualitySettings.ShadowDistance = -1f;
        Assert.Equal(1f, QualitySettings.ShadowDistance);

        QualitySettings.ShadowDistance = 5000f;
        Assert.Equal(1000f, QualitySettings.ShadowDistance);
    }

    [Fact]
    public void RenderStats_CaptureTierAtlasAndSkippedWork()
    {
        QualitySettings.ApplyEditorDefaults();

        RenderStats.BeginFrame();
        RenderStats.RecordShadowPassSkipped();
        RenderStats.RecordImageEffectsSkipped(3);
        RenderStats.EndFrame();

        RenderStats.Frame frame = RenderStats.Last;
        Assert.Equal(QualitySettings.QualityTier.Preview, frame.QualityTier);
        Assert.False(frame.ImageEffectsEnabled);
        Assert.Equal(1, frame.ShadowPassesSkipped);
        Assert.Equal(3, frame.ImageEffectsSkipped);
        Assert.Equal(0, frame.ShadowAtlasClears);
    }

    [Fact]
    public void ShadowAtlasCache_TracksKeyAndInvalidation()
    {
        ShadowAtlas.InvalidateShadowCache();
        Assert.False(ShadowAtlas.IsShadowCacheValid(123UL));

        ShadowAtlas.MarkShadowCacheValid(123UL);
        Assert.True(ShadowAtlas.IsShadowCacheValid(123UL));
        Assert.False(ShadowAtlas.IsShadowCacheValid(124UL));

        ShadowAtlas.InvalidateShadowCache();
        Assert.False(ShadowAtlas.IsShadowCacheValid(123UL));
    }

    [Fact]
    public void RenderStats_RecordCachedShadowPass()
    {
        RenderStats.BeginFrame();
        RenderStats.RecordShadowPassCached();
        RenderStats.EndFrame();

        Assert.Equal(1, RenderStats.Last.ShadowPassesCached);
        Assert.Equal(0, RenderStats.Last.ShadowPasses);
        Assert.Equal(0, RenderStats.Last.ShadowPassesSkipped);
    }

    [Theory]
    [InlineData(GraphicsBackend.OpenGL, true)]
    [InlineData(GraphicsBackend.WebGL2, true)]
    [InlineData(GraphicsBackend.Vulkan, false)]
    [InlineData(GraphicsBackend.Direct3D12, false)]
    [InlineData(GraphicsBackend.Null, false)]
    public void ShadowDepthSampling_UsesHardwareComparisonOnlyForOpenGL(
        GraphicsBackend backend,
        bool expectedHardwareComparison)
    {
        Assert.Equal(expectedHardwareComparison, ShadowAtlas.UsesHardwareDepthComparison(backend));
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan, "Vulkan (Mali-G610)", true)]
    [InlineData(GraphicsBackend.Vulkan, "Vulkan (Intel Arc)", true)]
    [InlineData(GraphicsBackend.Direct3D12, "Direct3D 12", false)]
    public void ShadowAtlas_UsesPositiveViewportOnlyForMaliVulkan(
        GraphicsBackend backend,
        string backendName,
        bool expectedPositiveViewport)
    {
        // UsesPositiveViewportHeight is player-build gated (the editor's own presentation keeps
        // the legacy convention), so exercise the player contract explicitly.
        bool previousPlayerBuild = Application.IsPlayerBuild;
        try
        {
            Application.IsPlayerBuild = true;
            Assert.Equal(expectedPositiveViewport, ShadowAtlas.UsesPositiveViewportHeight(backend, backendName));
        }
        finally
        {
            Application.IsPlayerBuild = previousPlayerBuild;
        }
    }
}
