// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class FogVolumeTests : RuntimeTestBase
{
    public FogVolumeTests()
    {
        VolumeManager.Instance.ResetForTests();
    }

    public override void Dispose()
    {
        VolumeManager.Instance.ResetForTests();
        base.Dispose();
    }

    [Fact]
    public void TwoVolumes_BlendGlobalDensity()
    {
        CreateFogSettingsVolume("A", weight: 1f, priority: 0, density: 0f);
        CreateFogSettingsVolume("B", weight: 0.5f, priority: 1, density: 0.02f);

        VolumeManager.Instance.Update(Float3.Zero);
        FogVolumeComponent fog = VolumeManager.Instance.Get<FogVolumeComponent>();

        Assert.True(fog.OverrideDensity);
        Assert.Equal(0.01f, fog.GlobalDensity, 4);
    }

    [Fact]
    public void Effect_UsesStackedDensityWhenVolumeOverrides()
    {
        var fx = new VolumetricFogEffect { GlobalDensity = 0.005f };
        CreateFogSettingsVolume("Fog", weight: 1f, priority: 0, density: 0.02f);
        VolumeManager.Instance.Update(Float3.Zero);

        Assert.Equal(0.02f, fx.ResolvedGlobalDensityForTest(), 4);
        Assert.Equal(0.005f, fx.GlobalDensity);
    }

    [Fact]
    public void Effect_KeepsOwnDensityWithoutVolume()
    {
        var fx = new VolumetricFogEffect { GlobalDensity = 0.005f };
        VolumeManager.Instance.Update(Float3.Zero);
        Assert.Equal(0.005f, fx.ResolvedGlobalDensityForTest(), 4);
    }

    [Fact]
    public void SpatialFogVolume_UploadsNearestFirst()
    {
        FogVolume far = CreateSpatialFog("Far", FogVolumeShape.Sphere, new Float3(10f, 0f, 0f));
        FogVolume near = CreateSpatialFog("Near", FogVolumeShape.Sphere, new Float3(1f, 0f, 0f));
        FogVolume global = CreateSpatialFog("Global", FogVolumeShape.Global, Float3.Zero);

        VolumeManager.Instance.PrepareFogVolumes(Float3.Zero);

        Assert.Equal(3, VolumeManager.Instance.FogVolumeUploadCount);
        Assert.Same(global, VolumeManager.Instance.GetFogVolumeUpload(0));
        Assert.Same(near, VolumeManager.Instance.GetFogVolumeUpload(1));
        Assert.Same(far, VolumeManager.Instance.GetFogVolumeUpload(2));
    }

    [Fact]
    public void EchoRoundTrip_PreservesLegacyFogVolumeFields()
    {
        var go = CreateGameObject("Fog");
        var fog = go.AddComponent<FogVolume>();
        fog.Shape = FogVolumeShape.Box;
        fog.DensityMultiplier = 2.5f;
        fog.ColorTint = new Color(0.2f, 0.4f, 0.6f, 1f);
        fog.Falloff = 0.3f;
        fog.ConeAngle = 15f;

        var clone = Serializer.Deserialize<GameObject>(Serializer.Serialize(go));
        var cloneFog = clone.GetComponent<FogVolume>();
        Assert.NotNull(cloneFog);
        Assert.Equal(FogVolumeShape.Box, cloneFog!.Shape);
        Assert.Equal(2.5f, cloneFog.DensityMultiplier);
        Assert.Equal(0.3f, cloneFog.Falloff);
        Assert.Equal(15f, cloneFog.ConeAngle);
    }

    [Fact]
    public void EchoRoundTrip_PreservesFogVolumeComponent()
    {
        var profile = new VolumeProfile();
        profile.Components.Add(new FogVolumeComponent
        {
            OverrideDensity = true,
            GlobalDensity = 0.012f,
            OverrideColor = true,
            GlobalColorTint = new Color(1f, 0.5f, 0.25f, 1f),
        });

        var deserialized = Serializer.Deserialize<VolumeProfile>(Serializer.Serialize(typeof(object), profile));
        Assert.NotNull(deserialized);
        Assert.True(deserialized!.TryGet(out FogVolumeComponent fog));
        Assert.True(fog.OverrideDensity);
        Assert.Equal(0.012f, fog.GlobalDensity, 4);
        Assert.True(fog.OverrideColor);
        Assert.Equal(1f, fog.GlobalColorTint.R, 3);
        Assert.Equal(0.5f, fog.GlobalColorTint.G, 3);
    }

    private Volume CreateFogSettingsVolume(string name, float weight, int priority, float density)
    {
        var profile = new VolumeProfile();
        profile.Components.Add(new FogVolumeComponent
        {
            OverrideDensity = true,
            GlobalDensity = density,
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

    private FogVolume CreateSpatialFog(string name, FogVolumeShape shape, Float3 position)
    {
        var go = CreateGameObject(name);
        go.Transform.Position = position;
        var fog = go.AddComponent<FogVolume>();
        fog.Shape = shape;
        VolumeManager.Instance.RegisterFogVolume(fog);
        return fog;
    }
}
