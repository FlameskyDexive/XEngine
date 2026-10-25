// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.PostProcessing;
using XEngine.PostProcessing.Effects;
using XEngine.Runtime.Rendering;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class PostProcessPackageTests : RuntimeTestBase
{
    private readonly bool _imageEffects = QualitySettings.ImageEffectsEnabled;

    public PostProcessPackageTests()
    {
        VolumeManager.Instance.ResetForTests();
        PostProcessManager.instance.ResetForTests();
        QualitySettings.ImageEffectsEnabled = true;
    }

    public override void Dispose()
    {
        QualitySettings.ImageEffectsEnabled = _imageEffects;
        PostProcessManager.instance.ResetForTests();
        VolumeManager.Instance.ResetForTests();
        base.Dispose();
    }

    [Fact]
    public void FloatParameter_InterpsWhenOverridden()
    {
        var from = new FloatParameter(0f, overrideState: true);
        var to = new FloatParameter(2f, overrideState: true);
        var dest = new FloatParameter(0f);
        dest.OverrideFrom(to, 0.5f);
        Assert.True(dest.overrideState);
        Assert.Equal(2f, dest.value, 3);

        dest = new FloatParameter(0f, overrideState: true);
        dest.OverrideFrom(to, 0.5f);
        Assert.Equal(1f, dest.value, 3);
        Assert.Equal(0f, from.value, 3);
    }

    [Fact]
    public void Profile_AddGetRemoveSettings()
    {
        var profile = new PostProcessProfile();
        Bloom bloom = profile.AddSettings<Bloom>();
        bloom.intensity.Override(1.5f);
        Assert.True(profile.HasSettings<Bloom>());
        Assert.Same(bloom, profile.GetSetting<Bloom>());
        Assert.Throws<InvalidOperationException>(() => profile.AddSettings<Bloom>());
        profile.RemoveSettings<Bloom>();
        Assert.False(profile.HasSettings<Bloom>());
    }

    [Fact]
    public void TwoVolumes_BlendBloomIntensity()
    {
        CreateBloomVolume("A", weight: 1f, priority: 0, intensity: 0f);
        CreateBloomVolume("B", weight: 0.5f, priority: 1, intensity: 2f);
        PostProcessManager.instance.Update(Float3.Zero, LayerMask.Everything);

        Bloom bloom = PostProcessManager.instance.Get<Bloom>();
        Assert.True(bloom.intensity.overrideState);
        Assert.Equal(1f, bloom.intensity.value, 3);
        Assert.True(bloom.enabled.value);
    }

    [Fact]
    public void Layer_InjectsBloomIntoPipeline()
    {
        CreateBloomVolume("Bloom", weight: 1f, priority: 0, intensity: 1.5f);
        var camera = CreateGameObject("Cam").AddComponent<Camera>();
        camera.GameObject.AddComponent<PostProcessLayer>();
        var pipeline = new UniversalRenderPipeline();

        pipeline.GatherEffectsForTest(camera);

        Assert.True(pipeline.HasEffectForTest<BloomEffect>());
        Assert.Equal(1, pipeline.CountEffectsForTest<BloomEffect>());
    }

    [Fact]
    public void LayerBloom_IgnoresCoreVolumeOverride()
    {
        var coreProfile = new VolumeProfile();
        coreProfile.Components.Add(new BloomVolumeComponent
        {
            OverrideIntensity = true,
            Intensity = 3f,
        });
        var coreGo = CreateGameObject("CoreVolume");
        var coreVolume = coreGo.AddComponent<Volume>();
        coreVolume.IsGlobal = true;
        coreVolume.Weight = 1f;
        coreVolume.Profile = coreProfile;
        VolumeManager.Instance.Register(coreVolume);
        VolumeManager.Instance.Update(Float3.Zero);

        CreateBloomVolume("PpvBloom", weight: 1f, priority: 0, intensity: 1f);
        var camera = CreateGameObject("Cam").AddComponent<Camera>();
        var layer = camera.GameObject.AddComponent<PostProcessLayer>();
        var owned = new List<ImageEffect>();
        layer.Prepare(camera);
        layer.CollectImageEffects(owned);

        BloomEffect bloom = Assert.IsType<BloomEffect>(Assert.Single(owned, e => e is BloomEffect));
        Assert.False(bloom.UseVolumeOverrides);
        Assert.Equal(1f, bloom.ResolvedIntensity(), 3);
        Assert.Equal(3f, VolumeManager.Instance.Get<BloomVolumeComponent>().Intensity, 3);
    }

    [Fact]
    public void EchoRoundTrip_PreservesBloomSettings()
    {
        var profile = new PostProcessProfile { Name = "Look" };
        Bloom bloom = profile.AddSettings<Bloom>();
        bloom.intensity.Override(2.5f);
        bloom.threshold.Override(0.4f);

        var deserialized = Serializer.Deserialize<PostProcessProfile>(Serializer.Serialize(typeof(object), profile));
        Assert.NotNull(deserialized);
        Assert.True(deserialized!.TryGetSettings(out Bloom? roundTrip));
        Assert.NotNull(roundTrip);
        Assert.True(roundTrip!.intensity.overrideState);
        Assert.Equal(2.5f, roundTrip.intensity.value, 3);
        Assert.Equal(0.4f, roundTrip.threshold.value, 3);
    }

    [Fact]
    public void Layer_Antialiasing_InjectsFxaa()
    {
        var camera = CreateGameObject("Cam").AddComponent<Camera>();
        var layer = camera.GameObject.AddComponent<PostProcessLayer>();
        layer.antialiasingMode = PostProcessLayer.Antialiasing.FastApproximateAntialiasing;
        var pipeline = new UniversalRenderPipeline();

        pipeline.GatherEffectsForTest(camera);

        Assert.True(pipeline.HasEffectForTest<FXAAEffect>());
    }

    [Fact]
    public void LocalVolume_FallsOffOutsideBlendDistance()
    {
        var profile = new PostProcessProfile();
        Bloom bloom = profile.AddSettings<Bloom>();
        bloom.enabled.Override(true);
        bloom.intensity.Override(2f);

        var go = CreateGameObject("Local");
        go.Transform.Position = new Float3(10f, 0f, 0f);
        go.Transform.LocalScale = new Float3(1f, 1f, 1f);
        var volume = go.AddComponent<PostProcessVolume>();
        volume.isGlobal = false;
        volume.weight = 1f;
        volume.blendDistance = 1f;
        volume.sharedProfile = profile;
        PostProcessManager.instance.Register(volume);

        PostProcessManager.instance.Update(new Float3(20f, 0f, 0f), LayerMask.Everything);
        Assert.False(PostProcessManager.instance.Get<Bloom>().IsEnabledAndSupported());

        PostProcessManager.instance.Update(new Float3(10f, 0f, 0f), LayerMask.Everything);
        Assert.True(PostProcessManager.instance.Get<Bloom>().IsEnabledAndSupported());
        Assert.Equal(2f, PostProcessManager.instance.Get<Bloom>().intensity.value, 3);
    }

    private PostProcessVolume CreateBloomVolume(string name, float weight, float priority, float intensity)
    {
        var profile = new PostProcessProfile();
        Bloom bloom = profile.AddSettings<Bloom>();
        bloom.enabled.Override(true);
        bloom.intensity.Override(intensity);

        var go = CreateGameObject(name);
        var volume = go.AddComponent<PostProcessVolume>();
        volume.isGlobal = true;
        volume.weight = weight;
        volume.priority = priority;
        volume.sharedProfile = profile;
        PostProcessManager.instance.Register(volume);
        return volume;
    }
}
