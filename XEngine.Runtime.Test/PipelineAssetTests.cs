// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class PipelineAssetTests : IDisposable
{
    private readonly QualitySettings.QualityTier _tier = QualitySettings.Tier;
    private readonly int _shadowAtlasSize = QualitySettings.ShadowAtlasSize;
    private readonly float _shadowDistance = QualitySettings.ShadowDistance;
    private readonly int _maxShadowCascades = QualitySettings.MaxShadowCascades;
    private readonly bool _shadowsEnabled = QualitySettings.ShadowsEnabled;
    private readonly SoftShadowQuality _softShadowQuality = QualitySettings.SoftShadowQuality;

    public void Dispose()
    {
        QualitySettings.Tier = _tier;
        QualitySettings.ShadowAtlasSize = _shadowAtlasSize;
        QualitySettings.ShadowDistance = _shadowDistance;
        QualitySettings.MaxShadowCascades = _maxShadowCascades;
        QualitySettings.ShadowsEnabled = _shadowsEnabled;
        QualitySettings.SoftShadowQuality = _softShadowQuality;
    }

    [Fact]
    public void ApplyQuality_WritesShadowKnobs()
    {
        QualitySettings.ApplyPlayerDefaults();
        var asset = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
        asset.ShadowAtlasSize = 1024;
        asset.ShadowDistance = 50f;
        asset.MaxShadowCascades = 2;
        asset.ShadowsEnabled = false;
        asset.SoftShadowQuality = SoftShadowQuality.High;
        asset.ApplyQuality();

        Assert.Equal(1024, QualitySettings.ShadowAtlasSize);
        Assert.Equal(50f, QualitySettings.ShadowDistance);
        Assert.Equal(2, QualitySettings.MaxShadowCascades);
        Assert.False(QualitySettings.ShadowsEnabled);
        Assert.Equal(SoftShadowQuality.High, QualitySettings.SoftShadowQuality);
    }

    [Fact]
    public void ApplyQuality_PreviewCapsAtlasAt2048()
    {
        QualitySettings.ApplyEditorDefaults();
        var asset = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
        asset.ShadowAtlasSize = 8192;
        asset.ApplyQuality();

        Assert.Equal(QualitySettings.QualityTier.Preview, QualitySettings.Tier);
        Assert.Equal(2048, QualitySettings.ShadowAtlasSize);
    }

    [Fact]
    public void CascadeCap_ReappliesAfterExternalChangesAndRoundTrips()
    {
        var asset = new UniversalRenderPipelineAsset { MaxShadowCascades = 2 };
        var copy = Serializer.Deserialize<UniversalRenderPipelineAsset>(Serializer.Serialize(asset));
        Assert.Equal(2, copy.MaxShadowCascades);
        copy.ApplyQuality();
        long version = QualitySettings.Version;
        copy.ApplyQuality();
        Assert.Equal(version, QualitySettings.Version);
        QualitySettings.MaxShadowCascades = 4;
        copy.ApplyQuality();
        Assert.Equal(2, QualitySettings.MaxShadowCascades);
        copy.MaxShadowCascades = 1;
        copy.ApplyQuality();
        Assert.Equal(1, QualitySettings.MaxShadowCascades);
        copy.MaxShadowCascades = 999;
        copy.ApplyQuality();
        Assert.Equal(4, QualitySettings.MaxShadowCascades);
    }

    [Fact]
    public void OnValidate_ClampsAndSnapsPowerOfTwo()
    {
        var asset = new UniversalRenderPipelineAsset
        {
            RenderScale = 99f,
            ShadowAtlasSize = 3000,
            ShadowDistance = 0f,
        };
        asset.OnValidate();
        Assert.Equal(2f, asset.RenderScale);
        Assert.Equal(2048, asset.ShadowAtlasSize);
        Assert.Equal(1f, asset.ShadowDistance);
        Assert.Equal(PipelineAntialiasingMode.None, asset.Antialiasing);
        Assert.Equal(4, asset.MsaaSamples);
    }

    [Fact]
    public void GetEffectiveRenderScale_UnchangedWithoutAsset()
    {
        var pipeline = new UniversalRenderPipeline();
        var camera = new Camera { RenderScale = 0.8f };
        Assert.Equal(0.8f, pipeline.GetEffectiveRenderScale(camera));
    }

    [Fact]
    public void GetEffectiveRenderScale_MultipliesPipelineDefault()
    {
        var pipeline = new UniversalRenderPipeline
        {
            Asset = new UniversalRenderPipelineAsset { RenderScale = 0.5f },
        };
        var camera = new Camera { RenderScale = 1f };
        Assert.Equal(0.5f, pipeline.GetEffectiveRenderScale(camera));
        camera.RenderScale = 2f;
        Assert.Equal(1f, pipeline.GetEffectiveRenderScale(camera));
    }

    [Fact]
    public void EchoRoundTrip_PreservesPipelineFields()
    {
        var original = new UniversalRenderPipelineAsset
        {
            Hdr = true,
            RenderScale = 0.75f,
            ShadowAtlasSize = 2048,
            ShadowDistance = 80f,
            ShadowsEnabled = false,
            SoftShadowQuality = SoftShadowQuality.Low,
            Antialiasing = PipelineAntialiasingMode.TAA,
            MsaaSamples = 8,
            UpscalingFilter = PipelineUpscalingFilter.STP,
        };

        var echo = Serializer.Serialize(typeof(object), original);
        var deserialized = Serializer.Deserialize<UniversalRenderPipelineAsset>(echo);

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.Hdr);
        Assert.Equal(0.75f, deserialized.RenderScale);
        Assert.Equal(2048, deserialized.ShadowAtlasSize);
        Assert.Equal(80f, deserialized.ShadowDistance);
        Assert.False(deserialized.ShadowsEnabled);
        Assert.Equal(SoftShadowQuality.Low, deserialized.SoftShadowQuality);
        Assert.Equal(PipelineAntialiasingMode.TAA, deserialized.Antialiasing);
        Assert.Equal(8, deserialized.MsaaSamples);
        Assert.Equal(PipelineUpscalingFilter.STP, deserialized.UpscalingFilter);
        Assert.False(deserialized.AllowPostProcessAlphaOutput);
    }

    [Fact]
    public void AllowPostProcessAlphaOutput_ReadsAsset_StillSevenPasses()
    {
        var pipeline = new UniversalRenderPipeline();
        Assert.False(pipeline.AllowPostProcessAlphaOutput);
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());

        pipeline.Asset = new UniversalRenderPipelineAsset { Hdr = true, AllowPostProcessAlphaOutput = true };
        Assert.True(pipeline.AllowPostProcessAlphaOutput);
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
    }

    [Fact]
    public void EchoRoundTrip_PreservesRendererData()
    {
        var original = new UniversalRenderData { DepthPriming = true };
        var echo = Serializer.Serialize(typeof(object), original);
        var deserialized = Serializer.Deserialize<UniversalRenderData>(echo);
        Assert.NotNull(deserialized);
        Assert.True(deserialized!.DepthPriming);
        Assert.Equal(RenderingPath.Forward, deserialized.RenderingPath);
        Assert.Equal(RendererKind.Universal, deserialized.Kind);
    }
}
