// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class VolumeTests : RuntimeTestBase
{
    public VolumeTests()
    {
        VolumeManager.Instance.ResetForTests();
    }

    public override void Dispose()
    {
        VolumeManager.Instance.ResetForTests();
        base.Dispose();
    }

    [Fact]
    public void TwoGlobalVolumes_BlendByWeight()
    {
        Volume a = CreateVolume("A", isGlobal: true, weight: 1f, priority: 0, intensity: 0f);
        Volume b = CreateVolume("B", isGlobal: true, weight: 0.5f, priority: 1, intensity: 10f);
        _ = a;
        _ = b;

        VolumeManager.Instance.Update(Float3.Zero);

        Assert.Equal(5f, VolumeManager.Instance.Get<BlendSample>().Intensity, 3);
    }

    [Fact]
    public void TwoOverlappingLocalVolumes_BlendAtIntersection()
    {
        Volume a = CreateVolume("A", isGlobal: false, weight: 1f, priority: 0, intensity: 0f);
        a.Transform.Position = Float3.Zero;
        a.Transform.LocalScale = new Float3(2f, 2f, 2f);

        Volume b = CreateVolume("B", isGlobal: false, weight: 0.5f, priority: 1, intensity: 10f);
        b.Transform.Position = new Float3(2f, 0f, 0f);
        b.Transform.LocalScale = new Float3(2f, 2f, 2f);

        VolumeManager.Instance.Update(new Float3(1f, 0f, 0f));

        Assert.Equal(5f, VolumeManager.Instance.Get<BlendSample>().Intensity, 3);
    }

    [Fact]
    public void LocalVolume_ZeroInfluenceWhenCameraIsFar()
    {
        Volume local = CreateVolume("Local", isGlobal: false, weight: 1f, priority: 0, intensity: 10f);
        local.Transform.Position = Float3.Zero;
        local.Transform.LocalScale = new Float3(1f, 1f, 1f);
        local.BlendDistance = 0f;

        VolumeManager.Instance.Update(new Float3(20f, 0f, 0f));

        Assert.Equal(0f, VolumeManager.Instance.Get<BlendSample>().Intensity, 3);
    }

    [Fact]
    public void DisabledVolume_IsSkipped()
    {
        Volume volume = CreateVolume("Off", isGlobal: true, weight: 1f, priority: 0, intensity: 10f);
        volume.Enabled = false;

        VolumeManager.Instance.Update(Float3.Zero);

        Assert.Equal(0f, VolumeManager.Instance.Get<BlendSample>().Intensity, 3);
    }

    [Fact]
    public void Stack_ReusesSameComponentInstance()
    {
        CreateVolume("A", isGlobal: true, weight: 1f, priority: 0, intensity: 3f);

        VolumeManager.Instance.Update(Float3.Zero);
        BlendSample first = VolumeManager.Instance.Get<BlendSample>();
        VolumeManager.Instance.Update(Float3.Zero);
        BlendSample second = VolumeManager.Instance.Get<BlendSample>();

        Assert.Same(first, second);
        Assert.Equal(3f, second.Intensity, 3);
    }

    [Fact]
    public void EchoRoundTrip_PreservesProfileAndVolumeFields()
    {
        var profile = new VolumeProfile();
        var echo = Serializer.Serialize(typeof(object), profile);
        var deserialized = Serializer.Deserialize<VolumeProfile>(echo);
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.Components);
        Assert.Empty(deserialized.Components);

        var go = CreateGameObject("VolumeGo");
        var volume = go.AddComponent<Volume>();
        volume.IsGlobal = false;
        volume.Weight = 0.5f;
        volume.BlendDistance = 1.5f;
        volume.Priority = 4;

        var clone = Serializer.Deserialize<GameObject>(Serializer.Serialize(go));
        var cloneVol = clone.GetComponent<Volume>();
        Assert.NotNull(cloneVol);
        Assert.False(cloneVol!.IsGlobal);
        Assert.Equal(0.5f, cloneVol.Weight);
        Assert.Equal(1.5f, cloneVol.BlendDistance);
        Assert.Equal(4, cloneVol.Priority);
    }

    private Volume CreateVolume(string name, bool isGlobal, float weight, int priority, float intensity)
    {
        var profile = new VolumeProfile();
        profile.Components.Add(new BlendSample { Intensity = intensity, OverrideIntensity = true });

        var go = CreateGameObject(name);
        var volume = go.AddComponent<Volume>();
        volume.IsGlobal = isGlobal;
        volume.Weight = weight;
        volume.Priority = priority;
        volume.Profile = profile;
        VolumeManager.Instance.Register(volume);
        return volume;
    }

    private sealed class BlendSample : VolumeComponent
    {
        public float Intensity;
        public bool OverrideIntensity = true;

        public override void Interp(VolumeComponent state, float interpFactor)
        {
            if (state is not BlendSample other || !other.OverrideIntensity)
                return;
            Intensity = Maths.Lerp(Intensity, other.Intensity, interpFactor);
        }

        public override void ResetToDefault() => Intensity = 0f;
    }
}
