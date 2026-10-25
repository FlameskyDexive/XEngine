// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class UpscalingFilterTests : RuntimeTestBase
{
    private readonly QualitySettings.AntiAliasingMode _antiAliasing = QualitySettings.AntiAliasing;
    private readonly bool _imageEffects = QualitySettings.ImageEffectsEnabled;

    public override void Dispose()
    {
        QualitySettings.AntiAliasing = _antiAliasing;
        QualitySettings.ImageEffectsEnabled = _imageEffects;
        base.Dispose();
    }

    [Fact]
    public void OnValidate_ClampsUnknownFilter()
    {
        var asset = new UniversalRenderPipelineAsset { UpscalingFilter = (PipelineUpscalingFilter)9 };
        asset.OnValidate();
        Assert.Equal(PipelineUpscalingFilter.Auto, asset.UpscalingFilter);
    }

    [Fact]
    public void Auto_WithoutTaa_ResolvesToLinear()
    {
        var pipeline = new UniversalRenderPipeline();
        Assert.Equal(PipelineUpscalingFilter.Auto, pipeline.UpscalingFilter);
        Assert.False(pipeline.UsesTemporalUpscale);
        Assert.Equal(PipelineUpscalingFilter.Linear, pipeline.GetEffectiveUpscalingFilter());
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
    }

    [Fact]
    public void Taa_AutoFilter_ResolvesToStp()
    {
        var pipeline = new UniversalRenderPipeline { AntialiasingMode = PipelineAntialiasingMode.TAA };
        Assert.True(pipeline.UsesTemporalUpscale);
        Assert.Equal(PipelineUpscalingFilter.STP, pipeline.GetEffectiveUpscalingFilter());
    }

    [Fact]
    public void Stp_InjectsTaaOnBase_NotOverlay_StillSevenPasses()
    {
        QualitySettings.ImageEffectsEnabled = true;
        QualitySettings.AntiAliasing = QualitySettings.AntiAliasingMode.Disabled;

        var scene = CreateScene(enable: true);
        var go = CreateGameObject("Cam");
        scene.Add(go);
        var camera = go.AddComponent<Camera>();
        camera.RenderType = CameraRenderType.Base;

        var pipeline = new UniversalRenderPipeline { UpscalingFilter = PipelineUpscalingFilter.STP };
        Assert.True(pipeline.UsesTemporalUpscale);
        Assert.Equal(PipelineUpscalingFilter.STP, pipeline.GetEffectiveUpscalingFilter());

        pipeline.GatherEffectsForTest(camera);
        Assert.True(pipeline.HasEffectForTest<TAAEffect>());
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());

        camera.RenderType = CameraRenderType.Overlay;
        pipeline.GatherEffectsForTest(camera);
        Assert.False(pipeline.HasEffectForTest<TAAEffect>());
    }

    [Fact]
    public void EchoRoundTrip_PreservesUpscalingFilter()
    {
        var original = new UniversalRenderPipelineAsset { UpscalingFilter = PipelineUpscalingFilter.FSR };
        var deserialized = Serializer.Deserialize<UniversalRenderPipelineAsset>(Serializer.Serialize(typeof(object), original));
        Assert.Equal(PipelineUpscalingFilter.FSR, deserialized!.UpscalingFilter);
    }
}
