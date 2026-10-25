// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ScreenSpaceGiTests : RuntimeTestBase
{
    private readonly bool _imageEffects = QualitySettings.ImageEffectsEnabled;

    public ScreenSpaceGiTests() => QualitySettings.ImageEffectsEnabled = true;

    public override void Dispose()
    {
        QualitySettings.ImageEffectsEnabled = _imageEffects;
        base.Dispose();
    }

    [Fact]
    public void DisabledSsgiFeature_DoesNotEnqueueCopyOrRequestInputs()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new ScreenSpaceGlobalIlluminationRendererFeature { Enabled = false });

        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(ScriptableRenderPassInput.None, pipeline.RequestedInputsForTest);
    }

    [Fact]
    public void EnabledSsgiFeature_RequestsColorDepthNormalAndCopies()
    {
        var pipeline = new UniversalRenderPipeline();
        var feature = new ScreenSpaceGlobalIlluminationRendererFeature();
        pipeline.AddRendererFeature(feature);

        int count = pipeline.EnqueueBuiltInAndFeaturesForTest();
        ScriptableRenderPassInput expected =
            ScreenSpaceGlobalIlluminationRendererFeature.RequiredInputs(feature.Effect.UseTemporal);

        Assert.Equal(9, count);
        Assert.Equal(expected, pipeline.RequestedInputsForTest);
        Assert.True((expected & ScriptableRenderPassInput.Color) != 0);
        Assert.True((expected & ScriptableRenderPassInput.Depth) != 0);

        pipeline.CompileRenderGraphForTest(out int culled, out int executed);
        Assert.Equal(0, culled);
        Assert.Equal(9, executed);
    }

    [Fact]
    public void Pipeline_InjectsSsgiWhenFeatureEnabled()
    {
        var camera = CreateGameObject("Cam").AddComponent<Camera>();
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new ScreenSpaceGlobalIlluminationRendererFeature());

        pipeline.GatherEffectsForTest(camera);

        Assert.True(pipeline.HasEffectForTest<ScreenSpaceGlobalIlluminationEffect>());
        Assert.Equal(1, pipeline.CountEffectsForTest<ScreenSpaceGlobalIlluminationEffect>());
    }

    [Fact]
    public void Pipeline_SkipsCameraSsgiWhenFeatureEnabled()
    {
        var camera = CreateGameObject("Cam").AddComponent<Camera>();
        camera.Effects.Add(new ScreenSpaceGlobalIlluminationEffect { Intensity = 0.2f });
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new ScreenSpaceGlobalIlluminationRendererFeature());

        pipeline.GatherEffectsForTest(camera);

        Assert.Equal(1, pipeline.CountEffectsForTest<ScreenSpaceGlobalIlluminationEffect>());
    }

    [Fact]
    public void Preview_DoesNotInjectSsgi()
    {
        QualitySettings.ImageEffectsEnabled = false;
        var camera = CreateGameObject("Cam").AddComponent<Camera>();
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new ScreenSpaceGlobalIlluminationRendererFeature());

        pipeline.GatherEffectsForTest(camera);

        Assert.False(pipeline.HasEffectForTest<ScreenSpaceGlobalIlluminationEffect>());
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(ScriptableRenderPassInput.None, pipeline.RequestedInputsForTest);
    }
}

public sealed class ScreenSpaceGiRenderTests : RuntimeTestBase
{
    [Fact]
    public void SsgiShader_ParsesWithFivePasses()
    {
        var shader = Shader.LoadDefault(DefaultShader.SSGI);
        Assert.NotNull(shader);
        int passes = 0;
        foreach (var _ in shader.Passes) passes++;
        Assert.Equal(5, passes);
        Assert.NotNull(shader.GetPass("Gather"));
        Assert.NotNull(shader.GetPass("Temporal"));
        Assert.NotNull(shader.GetPass("Blur"));
        Assert.NotNull(shader.GetPass("Composite"));
        Assert.NotNull(shader.GetPass("ProbeUpdate"));
    }

    [Fact]
    public void StandardShader_HasGiAlbedoPass()
    {
        var shader = Shader.LoadDefault(DefaultShader.Standard);
        Assert.NotNull(shader);
        int? index = shader.GetPassWithTag("LightMode", "GiAlbedo");
        Assert.NotNull(index);
        var anisotropic = Shader.LoadDefault(DefaultShader.StandardAnisotropic);
        Assert.NotNull(anisotropic.GetPassWithTag("LightMode", "GiAlbedo"));
    }

    [Fact]
    public void OnRenderEffect_RunsHeadless_AndRecordsGiPasses()
    {
        QualitySettings.ImageEffectsEnabled = true;
        var effect = new ScreenSpaceGlobalIlluminationEffect { Intensity = 0.5f, Resolution = EffectResolution.Quarter };

        // Fabricated frame state: prepass (depth + normals + motion), albedo capture, scene color.
        var prepass = new RenderTexture(64, 64, true,
            [TextureImageFormat.Color4b, TextureImageFormat.Short4]);
        var albedo = new RenderTexture(64, 64, true, [TextureImageFormat.Color4b]);
        var color = new RenderTexture(64, 64, true, [TextureImageFormat.Color4b]);

        var context = new RenderContext
        {
            DepthNormals = prepass,
            MotionVectors = prepass.InternalTextures[1],
            GiAlbedoTexture = albedo.MainTexture,
            SceneColor = color,
            Width = 64,
            Height = 64,
            CurrentStage = RenderStage.AfterOpaques,
        };

        RenderStats.BeginFrame();
        effect.OnRenderEffect(context);
        RenderStats.EndFrame();

        Assert.True(RenderStats.Last.GiPasses >= 3); // gather + composite (+ blur)
        effect.OnDisable();
        context.Dispose();
        color.Dispose();
        albedo.Dispose();
        prepass.Dispose();
    }
}

public sealed class RealtimeGiSettingsTests : RuntimeTestBase
{
    [Fact]
    public void SceneGiEnabled_EnqueuesEffect_WithoutFeatureOrCameraEffect()
    {
        QualitySettings.ImageEffectsEnabled = true;
        var scene = CreateScene();
        var go = CreateGameObject("Cam");
        go.Scene = scene;
        var camera = go.AddComponent<Camera>();
        var pipeline = new UniversalRenderPipeline();

        scene.Gi = new Scene.GiParams { Enable = true };
        pipeline.GatherEffectsForTest(camera);
        Assert.True(pipeline.HasEffectForTest<ScreenSpaceGlobalIlluminationEffect>());

        scene.Gi = new Scene.GiParams { Enable = false };
        pipeline.GatherEffectsForTest(camera);
        Assert.False(pipeline.HasEffectForTest<ScreenSpaceGlobalIlluminationEffect>());
    }

    [Fact]
    public void VolumeComponent_WantsEffect_OnlyWhenEnabled()
    {
        var component = new GlobalIlluminationVolumeComponent();
        Assert.False(component.WantsEffect);

        component.Active = true;
        Assert.False(component.WantsEffect);

        component.OverrideEnable = true;
        component.Enable = true;
        Assert.True(component.WantsEffect);

        // InterpFloat: destination un-overridden takes the source value outright.
        var target = new GlobalIlluminationVolumeComponent
        {
            Active = true, OverrideEnable = true, Enable = true,
            OverrideIntensity = true, Intensity = 2.5f,
        };
        component.Interp(target, 0.5f);
        Assert.True(component.OverrideIntensity);
        Assert.Equal(2.5f, component.Intensity, 3);

        // Both overridden: value blends toward the source.
        component.Interp(target, 0.5f);
        Assert.Equal(2.5f, component.Intensity, 3);

        component.ResetToDefault();
        Assert.False(component.OverrideEnable);
        Assert.False(component.Enable);
    }

    [Fact]
    public void EffectResolvers_VolumeOverSceneOverInstance()
    {
        QualitySettings.ImageEffectsEnabled = true;
        var scene = CreateScene();
        var go = CreateGameObject("Cam");
        go.Scene = scene;
        var camera = go.AddComponent<Camera>();
        var effect = new ScreenSpaceGlobalIlluminationEffect { Intensity = 0.25f };

        scene.Gi = new Scene.GiParams { Enable = true, Intensity = 0.5f };
        Assert.Equal(0.5f, effect.ResolvedIntensity(camera), 3);

        var vol = VolumeManager.Instance.Get<GlobalIlluminationVolumeComponent>();
        vol.Active = true;
        vol.OverrideIntensity = true;
        vol.Intensity = 2f;
        Assert.Equal(2f, effect.ResolvedIntensity(camera), 3);

        vol.OverrideIntensity = false;
        vol.Active = false;
        scene.Gi = new Scene.GiParams();
        Assert.Equal(0.25f, effect.ResolvedIntensity(camera), 3);
    }
}

public sealed class RealtimeGiCostGateTests : RuntimeTestBase
{
    [Fact]
    public void DisabledGi_SubmitsZeroGiPasses()
    {
        // Gate (rendering-gpu-performance skill): GI fully off must add zero passes/RTs.
        QualitySettings.ImageEffectsEnabled = true;
        var camera = CreateGameObject("Cam").AddComponent<Camera>();
        var pipeline = new UniversalRenderPipeline();

        RenderStats.BeginFrame();
        pipeline.GatherEffectsForTest(camera); // no SSGI feature, scene Gi off
        RenderStats.EndFrame();
        Assert.Equal(0, RenderStats.Last.GiPasses);
        Assert.Equal(0, RenderStats.Last.GiAlbedoPasses);
        Assert.Equal(0, RenderStats.Last.GiProbesUpdated);
        Assert.False(GiProbeVolume.Active);
    }
}
