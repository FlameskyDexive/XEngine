// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ScreenSpaceLensFlareTests : RuntimeTestBase
{
    public ScreenSpaceLensFlareTests()
    {
        VolumeManager.Instance.ResetForTests();
    }

    public override void Dispose()
    {
        VolumeManager.Instance.ResetForTests();
        base.Dispose();
    }

    [Fact]
    public void LensFlare_BlendsIntensityAndEchoRoundTrips()
    {
        var profileA = new VolumeProfile();
        profileA.Components.Add(new ScreenSpaceLensFlareVolumeComponent
        {
            OverrideIntensity = true,
            Intensity = 0f,
            OverrideGhostCount = true,
            GhostCount = 1,
        });
        var goA = CreateGameObject("FlareA");
        var volA = goA.AddComponent<Volume>();
        volA.IsGlobal = true;
        volA.Weight = 1f;
        volA.Profile = profileA;
        VolumeManager.Instance.Register(volA);

        var profileB = new VolumeProfile();
        profileB.Components.Add(new ScreenSpaceLensFlareVolumeComponent
        {
            OverrideIntensity = true,
            Intensity = 2f,
            OverrideGhostCount = true,
            GhostCount = 5,
        });
        var goB = CreateGameObject("FlareB");
        var volB = goB.AddComponent<Volume>();
        volB.IsGlobal = true;
        volB.Weight = 0.5f;
        volB.Priority = 1;
        volB.Profile = profileB;
        VolumeManager.Instance.Register(volB);

        VolumeManager.Instance.Update(Float3.Zero);
        ScreenSpaceLensFlareVolumeComponent flare = VolumeManager.Instance.Get<ScreenSpaceLensFlareVolumeComponent>();
        Assert.True(flare.WantsEffect);
        Assert.Equal(1f, flare.Intensity, 3);
        Assert.Equal(5, flare.GhostCount);

        var original = new ScreenSpaceLensFlareVolumeComponent
        {
            OverrideIntensity = true,
            Intensity = 0.75f,
            OverrideTint = true,
            Tint = Color.Red,
        };
        var clone = Serializer.Deserialize<ScreenSpaceLensFlareVolumeComponent>(
            Serializer.Serialize(typeof(object), original));
        Assert.True(clone!.OverrideIntensity);
        Assert.Equal(0.75f, clone.Intensity);
        Assert.Equal(Color.Red, clone.Tint);
    }
}
