// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Unity 6.7 resolution chain: tier override → Graphics default → engine built-in, re-resolved
/// on every override assignment and tier switch through <see cref="UniversalPipelineAssignment"/>.
/// </summary>
public sealed class QualityPipelineOverrideTests : IDisposable
{
    private readonly QualitySettings.QualityTier _tier = QualitySettings.Tier;
    private readonly UniversalRenderPipelineAsset? _graphicsDefault = UniversalPipelineAssignment.GraphicsDefault;
    private readonly UniversalRenderPipelineAsset? _pipelineAsset = UniversalRenderPipeline.Default.Asset;

    public void Dispose()
    {
        QualitySettings.SetPipelineOverride(QualitySettings.QualityTier.Preview, null);
        QualitySettings.SetPipelineOverride(QualitySettings.QualityTier.Full, null);
        UniversalPipelineAssignment.GraphicsDefault = _graphicsDefault;
        QualitySettings.Tier = _tier;
        UniversalRenderPipeline.Default.Asset = _pipelineAsset;
    }

    [Fact]
    public void TierOverride_WinsOverGraphicsDefault()
    {
        var graphicsDefault = new UniversalRenderPipelineAsset();
        var overrideAsset = new UniversalRenderPipelineAsset();
        QualitySettings.Tier = QualitySettings.QualityTier.Full;
        UniversalPipelineAssignment.GraphicsDefault = graphicsDefault;
        QualitySettings.SetPipelineOverride(QualitySettings.QualityTier.Full, overrideAsset);

        Assert.Same(overrideAsset, UniversalRenderPipeline.Default.Asset);
    }

    [Fact]
    public void EmptyOverride_FallsBackToGraphicsDefault()
    {
        var graphicsDefault = new UniversalRenderPipelineAsset();
        QualitySettings.Tier = QualitySettings.QualityTier.Full;
        QualitySettings.SetPipelineOverride(QualitySettings.QualityTier.Full, null);
        UniversalPipelineAssignment.GraphicsDefault = graphicsDefault;

        Assert.Same(graphicsDefault, UniversalRenderPipeline.Default.Asset);
    }

    [Fact]
    public void SetPipelineOverride_AppliesImmediately()
    {
        var graphicsDefault = new UniversalRenderPipelineAsset();
        var overrideAsset = new UniversalRenderPipelineAsset();
        QualitySettings.Tier = QualitySettings.QualityTier.Full;
        UniversalPipelineAssignment.GraphicsDefault = graphicsDefault;
        Assert.Same(graphicsDefault, UniversalRenderPipeline.Default.Asset);

        QualitySettings.SetPipelineOverride(QualitySettings.QualityTier.Full, overrideAsset);
        Assert.Same(overrideAsset, UniversalRenderPipeline.Default.Asset);
    }

    [Fact]
    public void ClearingOverride_FallsBackImmediately()
    {
        var graphicsDefault = new UniversalRenderPipelineAsset();
        var overrideAsset = new UniversalRenderPipelineAsset();
        QualitySettings.Tier = QualitySettings.QualityTier.Full;
        UniversalPipelineAssignment.GraphicsDefault = graphicsDefault;
        QualitySettings.SetPipelineOverride(QualitySettings.QualityTier.Full, overrideAsset);

        QualitySettings.SetPipelineOverride(QualitySettings.QualityTier.Full, null);
        Assert.Same(graphicsDefault, UniversalRenderPipeline.Default.Asset);
    }

    [Fact]
    public void TierSwitch_SwitchesPipeline()
    {
        var graphicsDefault = new UniversalRenderPipelineAsset();
        var previewOnly = new UniversalRenderPipelineAsset();
        QualitySettings.SetPipelineOverride(QualitySettings.QualityTier.Preview, previewOnly);
        QualitySettings.SetPipelineOverride(QualitySettings.QualityTier.Full, null);
        UniversalPipelineAssignment.GraphicsDefault = graphicsDefault;

        QualitySettings.Tier = QualitySettings.QualityTier.Full;
        Assert.Same(graphicsDefault, UniversalRenderPipeline.Default.Asset);

        QualitySettings.Tier = QualitySettings.QualityTier.Preview;
        Assert.Same(previewOnly, UniversalRenderPipeline.Default.Asset);
    }

    [Fact]
    public void NoOverrideNoDefault_DegradesToBuiltInDefaults()
    {
        QualitySettings.SetPipelineOverride(QualitySettings.QualityTier.Preview, null);
        QualitySettings.SetPipelineOverride(QualitySettings.QualityTier.Full, null);
        UniversalPipelineAssignment.GraphicsDefault = null;

        Assert.Null(UniversalRenderPipeline.Default.Asset);
        // The pipeline still renders with built-in Universal/Forward defaults.
        Assert.Equal(RendererKind.Universal, UniversalRenderPipeline.Default.RendererKind);
        Assert.Equal(RenderingPath.Forward, UniversalRenderPipeline.Default.RenderingPath);
    }
}
