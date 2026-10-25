// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class PostProcessVolumeTests : RuntimeTestBase
{
    private readonly bool _imageEffects = QualitySettings.ImageEffectsEnabled;

    public PostProcessVolumeTests()
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
    public void TwoVolumes_BlendBloomIntensity()
    {
        CreateBloomVolume("A", weight: 1f, priority: 0, intensity: 0f);
        CreateBloomVolume("B", weight: 0.5f, priority: 1, intensity: 2f);
        VolumeManager.Instance.Update(Float3.Zero);

        BloomVolumeComponent bloom = VolumeManager.Instance.Get<BloomVolumeComponent>();
        Assert.True(bloom.OverrideIntensity);
        Assert.Equal(1f, bloom.Intensity, 3);
    }

    [Fact]
    public void BloomEffect_UsesStackedIntensity()
    {
        var fx = new BloomEffect { Intensity = 0.25f };
        CreateBloomVolume("Bloom", weight: 1f, priority: 0, intensity: 3f);
        VolumeManager.Instance.Update(Float3.Zero);

        Assert.Equal(3f, fx.ResolvedIntensity(), 3);
        Assert.Equal(0.25f, fx.Intensity);
    }

    [Fact]
    public void BloomEffect_KeepsOwnIntensityWithoutVolume()
    {
        var fx = new BloomEffect { Intensity = 0.25f };
        VolumeManager.Instance.Update(Float3.Zero);
        Assert.Equal(0.25f, fx.ResolvedIntensity(), 3);
    }

    [Fact]
    public void Pipeline_InjectsBloomWhenVolumeWantsIt()
    {
        CreateBloomVolume("Bloom", weight: 1f, priority: 0, intensity: 1.5f);
        var camera = CreateGameObject("Cam").AddComponent<Camera>();
        var pipeline = new UniversalRenderPipeline();

        pipeline.GatherEffectsForTest(camera);

        Assert.True(pipeline.HasEffectForTest<BloomEffect>());
    }

    [Fact]
    public void Pipeline_DoesNotInjectWhenCameraAlreadyHasBloom()
    {
        CreateBloomVolume("Bloom", weight: 1f, priority: 0, intensity: 1.5f);
        var camera = CreateGameObject("Cam").AddComponent<Camera>();
        camera.Effects.Add(new BloomEffect { Intensity = 0.1f });
        var pipeline = new UniversalRenderPipeline();

        pipeline.GatherEffectsForTest(camera);
        Assert.Equal(1, pipeline.CountEffectsForTest<BloomEffect>());
    }

    [Fact]
    public void EchoRoundTrip_PreservesBloomVolumeComponent()
    {
        var profile = new VolumeProfile();
        profile.Components.Add(new BloomVolumeComponent
        {
            OverrideIntensity = true,
            Intensity = 2.5f,
            OverrideThreshold = true,
            Threshold = 0.4f,
        });

        var deserialized = Serializer.Deserialize<VolumeProfile>(Serializer.Serialize(typeof(object), profile));
        Assert.NotNull(deserialized);
        Assert.True(deserialized!.TryGet(out BloomVolumeComponent bloom));
        Assert.True(bloom.OverrideIntensity);
        Assert.Equal(2.5f, bloom.Intensity, 3);
        Assert.Equal(0.4f, bloom.Threshold, 3);
    }

    private Volume CreateBloomVolume(string name, float weight, int priority, float intensity)
    {
        var profile = new VolumeProfile();
        profile.Components.Add(new BloomVolumeComponent
        {
            OverrideIntensity = true,
            Intensity = intensity,
        });

        var go = CreateGameObject(name);
        var volume = go.AddComponent<Volume>();
        volume.IsGlobal = true;
        volume.Weight = weight;
        volume.Priority = priority;
        volume.Profile = profile;
        VolumeManager.Instance.Register(volume);
        return volume;
    }
}
