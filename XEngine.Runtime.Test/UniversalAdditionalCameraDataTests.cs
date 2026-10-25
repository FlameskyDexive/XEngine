// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class UniversalAdditionalCameraDataTests : RuntimeTestBase
{
    private readonly bool _imageEffects = QualitySettings.ImageEffectsEnabled;

    public UniversalAdditionalCameraDataTests()
    {
        VolumeManager.Instance.ResetForTests();
        QualitySettings.ImageEffectsEnabled = true;
    }

    public override void Dispose()
    {
        QualitySettings.ImageEffectsEnabled = _imageEffects;
        VolumeManager.Instance.ResetForTests();
        base.Dispose();
    }

    [Fact]
    public void GetOrAdd_CreatesOnce()
    {
        var go = CreateGameObject("Cam");
        Camera camera = go.AddComponent<Camera>();
        Assert.Null(UniversalAdditionalCameraData.Find(camera));

        UniversalAdditionalCameraData first = UniversalAdditionalCameraData.GetOrAdd(camera);
        UniversalAdditionalCameraData second = UniversalAdditionalCameraData.GetOrAdd(camera);
        Assert.Same(first, second);
        Assert.Same(first, go.GetComponent<UniversalAdditionalCameraData>());
        Assert.True(first.RenderPostProcessing);
        Assert.True(first.RenderShadows);
    }

    [Fact]
    public void MissingComponent_KeepsLegacyPostAndShadowsOn()
    {
        var go = CreateGameObject("Cam");
        Camera camera = go.AddComponent<Camera>();
        Assert.True(UniversalAdditionalCameraData.ShouldRenderPostProcessing(camera));
        Assert.True(UniversalAdditionalCameraData.ShouldRenderShadows(camera));
    }

    [Fact]
    public void EchoRoundTrip_PreservesCameraData()
    {
        var go = CreateGameObject("Cam");
        Camera camera = go.AddComponent<Camera>();
        UniversalAdditionalCameraData data = UniversalAdditionalCameraData.GetOrAdd(camera);
        data.RenderPostProcessing = true;
        data.RenderShadows = false;
        data.Antialiasing = CameraAntialiasing.SMAA;
        data.RequiresDepthTexture = true;

        var clone = Serializer.Deserialize<GameObject>(Serializer.Serialize(go));
        var cloneData = clone.GetComponent<UniversalAdditionalCameraData>();
        Assert.NotNull(cloneData);
        Assert.True(cloneData!.RenderPostProcessing);
        Assert.False(cloneData.RenderShadows);
        Assert.Equal(CameraAntialiasing.SMAA, cloneData.Antialiasing);
        Assert.True(cloneData.RequiresDepthTexture);
    }

    [Fact]
    public void EchoRoundTrip_PreservesLightData()
    {
        var go = CreateGameObject("Light");
        DirectionalLight light = go.AddComponent<DirectionalLight>();
        UniversalAdditionalLightData data = UniversalAdditionalLightData.GetOrAdd(light);
        data.UsePipelineSettings = false;
        data.AdditionalLightsShadowResolutionTier = AdditionalLightsShadowResolutionTier.High;
        data.SoftShadowQuality = SoftShadowQuality.Low;

        var clone = Serializer.Deserialize<GameObject>(Serializer.Serialize(go));
        var cloneData = clone.GetComponent<UniversalAdditionalLightData>();
        Assert.NotNull(cloneData);
        Assert.False(cloneData!.UsePipelineSettings);
        Assert.Equal(AdditionalLightsShadowResolutionTier.High, cloneData.AdditionalLightsShadowResolutionTier);
        Assert.Equal(SoftShadowQuality.Low, cloneData.SoftShadowQuality);
    }

    [Fact]
    public void Pipeline_SkipsVolumeBloomWhenPostIsOff()
    {
        var profile = new VolumeProfile();
        profile.Components.Add(new BloomVolumeComponent { OverrideIntensity = true, Intensity = 1.5f });
        var volumeGo = CreateGameObject("Volume");
        Volume volume = volumeGo.AddComponent<Volume>();
        volume.IsGlobal = true;
        volume.Profile = profile;
        VolumeManager.Instance.Register(volume);

        var camGo = CreateGameObject("Cam");
        Camera camera = camGo.AddComponent<Camera>();
        UniversalAdditionalCameraData.GetOrAdd(camera).RenderPostProcessing = false;
        var pipeline = new UniversalRenderPipeline();

        pipeline.GatherEffectsForTest(camera);
        Assert.False(pipeline.HasEffectForTest<BloomEffect>());
    }

    [Fact]
    public void Pipeline_InjectsCameraFxaaOverride()
    {
        var camGo = CreateGameObject("Cam");
        Camera camera = camGo.AddComponent<Camera>();
        UniversalAdditionalCameraData.GetOrAdd(camera).Antialiasing = CameraAntialiasing.FXAA;
        UniversalAdditionalCameraData.Find(camera)!.RenderPostProcessing = true;
        var pipeline = new UniversalRenderPipeline();

        pipeline.GatherEffectsForTest(camera);
        Assert.True(pipeline.HasEffectForTest<FXAAEffect>());
    }
}
