// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ScreenSpaceFeatureTests : RuntimeTestBase
{
    private readonly bool _imageEffects = QualitySettings.ImageEffectsEnabled;

    public ScreenSpaceFeatureTests() => QualitySettings.ImageEffectsEnabled = true;

    public override void Dispose()
    {
        QualitySettings.ImageEffectsEnabled = _imageEffects;
        base.Dispose();
    }

    [Fact]
    public void DisabledGtaoFeature_DoesNotEnqueueCopyOrRequestInputs()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new GTAORendererFeature { Enabled = false });

        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(ScriptableRenderPassInput.None, pipeline.RequestedInputsForTest);
        Assert.False(UniversalRenderPipeline.RequiresPrepass(
            CameraClearFlags.Skybox, UniversalRenderPipeline.MapPassInputs(pipeline.RequestedInputsForTest)));
    }

    [Fact]
    public void EnabledGtaoFeature_RequestsPrepassAndCopyDepth()
    {
        var pipeline = new UniversalRenderPipeline();
        var feature = new GTAORendererFeature();
        pipeline.AddRendererFeature(feature);

        int count = pipeline.EnqueueBuiltInAndFeaturesForTest();
        ScriptableRenderPassInput expected = GTAORendererFeature.RequiredInputs(feature.Effect.UseTemporal);

        Assert.Equal(8, count);
        Assert.Equal(expected, pipeline.RequestedInputsForTest);
        Assert.True(UniversalRenderPipeline.RequiresPrepass(
            CameraClearFlags.Skybox, UniversalRenderPipeline.MapPassInputs(expected)));
    }

    [Fact]
    public void EnabledSsrFeature_RequestsPrepassAndCopyDepth()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new ScreenSpaceReflectionRendererFeature());

        Assert.Equal(8, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(
            ScriptableRenderPassInput.Depth
            | ScriptableRenderPassInput.Normal
            | ScriptableRenderPassInput.Motion,
            pipeline.RequestedInputsForTest);
    }

    [Fact]
    public void Pipeline_InjectsGtaoWhenFeatureEnabled()
    {
        var camera = CreateGameObject("Cam").AddComponent<Camera>();
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new GTAORendererFeature());

        pipeline.GatherEffectsForTest(camera);

        Assert.True(pipeline.HasEffectForTest<GTAOEffect>());
        Assert.Equal(1, pipeline.CountEffectsForTest<GTAOEffect>());
    }

    [Fact]
    public void Pipeline_SkipsCameraGtaoWhenFeatureEnabled()
    {
        var camera = CreateGameObject("Cam").AddComponent<Camera>();
        camera.Effects.Add(new GTAOEffect { Intensity = 0.1f });
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new GTAORendererFeature());

        pipeline.GatherEffectsForTest(camera);

        Assert.Equal(1, pipeline.CountEffectsForTest<GTAOEffect>());
    }

    [Fact]
    public void Pipeline_KeepsCameraGtaoWithoutFeature()
    {
        var camera = CreateGameObject("Cam").AddComponent<Camera>();
        camera.Effects.Add(new GTAOEffect());
        var pipeline = new UniversalRenderPipeline();

        pipeline.GatherEffectsForTest(camera);

        Assert.True(pipeline.HasEffectForTest<GTAOEffect>());
        Assert.Equal(1, pipeline.CountEffectsForTest<GTAOEffect>());
    }

    [Fact]
    public void Preview_DoesNotInjectOrRequestInputsFromGtaoFeature()
    {
        QualitySettings.ImageEffectsEnabled = false;
        var camera = CreateGameObject("Cam").AddComponent<Camera>();
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new GTAORendererFeature());

        pipeline.GatherEffectsForTest(camera);

        Assert.False(pipeline.HasEffectForTest<GTAOEffect>());
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(ScriptableRenderPassInput.None, pipeline.RequestedInputsForTest);
    }
}
