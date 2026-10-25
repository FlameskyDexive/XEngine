// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Audio;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Tests for the managed AudioMixer (prowl-sync M6.2): group-chain gain math, mute, solo
/// silencing with ancestor passthrough, snapshot activation, and asset round-trips.
/// </summary>
public class AudioMixerTests
{
    private static AudioMixer MakeMixer()
    {
        var mixer = new AudioMixer { Name = "Mixer" };
        mixer.Groups.Add(new AudioMixerGroupData { Name = "Music", Parent = "Master", VolumeDB = -20f });
        mixer.Groups.Add(new AudioMixerGroupData { Name = "SFX", Parent = "Master", VolumeDB = 0f });
        return mixer;
    }

    [Fact]
    public void GroupChainGain_IsProductOfAncestors()
    {
        var mixer = MakeMixer();

        // -20 dB music under a 0 dB master => linear 0.1.
        Assert.Equal(0.1f, mixer.GetGroupLinearVolume("Music"), 2);
        Assert.Equal(1f, mixer.GetGroupLinearVolume("SFX"), 2);
        Assert.Equal(1f, mixer.GetGroupLinearVolume("Master"), 2);
    }

    [Fact]
    public void MutedGroup_SilencesItsChain()
    {
        var mixer = MakeMixer();
        mixer.FindGroup("Master")!.Mute = true;

        Assert.Equal(0f, mixer.GetGroupLinearVolume("Music"));
        Assert.Equal(0f, mixer.GetGroupLinearVolume("Master"));
    }

    [Fact]
    public void Solo_SilencesNonSoloed_ChainButKeepsAncestorsAudible()
    {
        var mixer = MakeMixer();
        mixer.Groups.Add(new AudioMixerGroupData { Name = "Soloed", Parent = "Master" });
        mixer.FindGroup("Soloed")!.Solo = true;

        // The soloed chain stays audible, the sibling chain is silenced by solo, and the shared
        // Master ancestor is not treated as silenced just because it is not itself soloed.
        Assert.Equal(1f, mixer.GetGroupLinearVolume("Soloed"));
        Assert.Equal(0f, mixer.GetGroupLinearVolume("SFX"));
        Assert.True(mixer.AnySolo);
    }

    [Fact]
    public void Snapshot_Activation_CopiesVolumes()
    {
        var mixer = MakeMixer();
        mixer.Snapshots.Add(new AudioMixerSnapshotData
        {
            Name = "Duck",
            Volumes =
            {
                new AudioMixerSnapshotVolume { Group = "Music", VolumeDB = -30f },
                new AudioMixerSnapshotVolume { Group = "SFX", VolumeDB = -6f },
            }
        });

        Assert.True(mixer.ApplySnapshot("Duck"));
        Assert.Equal("Duck", mixer.ActiveSnapshot);
        Assert.Equal(-30f, mixer.FindGroup("Music")!.VolumeDB, 2);
        Assert.Equal(-6f, mixer.FindGroup("SFX")!.VolumeDB, 2);
        Assert.Equal(0.031f, mixer.GetGroupLinearVolume("Music"), 2);
    }

    [Fact]
    public void Snapshot_UnknownName_ReturnsFalse()
    {
        var mixer = MakeMixer();
        Assert.False(mixer.ApplySnapshot("DoesNotExist"));
    }

    [Fact]
    public void Serialization_RoundTripsGroupsAndSnapshots()
    {
        var mixer = new AudioMixer { Name = "Mixer" };
        mixer.Groups.Add(new AudioMixerGroupData { Name = "Music", Parent = "Master", VolumeDB = -12f, Mute = true });
        mixer.Snapshots.Add(new AudioMixerSnapshotData
        {
            Name = "Duck",
            Volumes = { new AudioMixerSnapshotVolume { Group = "Music", VolumeDB = -30f } }
        });

        var echo = EchoObject.NewCompound();
        mixer.Serialize(ref echo, new SerializationContext());

        var restored = new AudioMixer();
        restored.Deserialize(echo, new SerializationContext());

        var music = restored.FindGroup("Music")!;
        Assert.Equal("Master", music.Parent);
        Assert.Equal(-12f, music.VolumeDB, 2);
        Assert.True(music.Mute);
        Assert.Single(restored.Snapshots);
        Assert.Equal("Duck", restored.Snapshots[0].Name);
        Assert.Single(restored.Snapshots[0].Volumes);
        Assert.Equal("Music", restored.Snapshots[0].Volumes[0].Group);
        Assert.Equal(-30f, restored.Snapshots[0].Volumes[0].VolumeDB, 2);
    }
}
