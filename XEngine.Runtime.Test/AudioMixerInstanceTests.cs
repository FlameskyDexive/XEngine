// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Audio;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// N1 mixer foundation: persistent group ids with legacy migration, hierarchy validation that
/// never hangs, the compiled runtime instance whose chain gains match the legacy asset math,
/// automatic propagation to registered listeners (instance mutations and legacy direct asset
/// edits alike), snapshot overrides that stay out of the asset, and zero-allocation steady state.
/// </summary>
public sealed class AudioMixerInstanceTests : IDisposable
{
    public AudioMixerInstanceTests()
    {
        AudioMixerInstance.Forget(MakeAsset()); // no-op warm-up of statics
    }

    public void Dispose()
    {
        // The registry holds instances keyed by asset; tests create fresh assets each time, so
        // nothing to reset beyond letting instances expire with their assets.
    }

    private static AudioMixer MakeAsset()
    {
        var mixer = new AudioMixer { Name = "Mixer" };
        mixer.Groups.Add(new AudioMixerGroupData { Name = "Music", Parent = "Master", VolumeDB = -20f });
        mixer.Groups.Add(new AudioMixerGroupData { Name = "SFX", Parent = "Master", VolumeDB = 0f });
        mixer.EnsureGroupIds();
        return mixer;
    }

    // --- ids & migration ---------------------------------------------------------------------

    [Fact]
    public void LegacyAssetWithoutIds_MintsIdsOnLoad_AndRoundTrips()
    {
        // Hand-write an Echo compound in the pre-N1 format (no Id keys).
        var echo = EchoObject.NewCompound();
        echo.Add("ActiveSnapshot", new EchoObject(""));
        var groups = EchoObject.NewList();
        var master = EchoObject.NewCompound();
        master.Add("Name", new EchoObject("Master"));
        master.Add("Parent", new EchoObject(""));
        master.Add("VolumeDB", new EchoObject(0f));
        groups.ListAdd(master);
        var music = EchoObject.NewCompound();
        music.Add("Name", new EchoObject("Music"));
        music.Add("Parent", new EchoObject("Master"));
        music.Add("VolumeDB", new EchoObject(-6f));
        groups.ListAdd(music);
        echo.Add("Groups", groups);

        var restored = new AudioMixer();
        restored.Deserialize(echo, new SerializationContext());

        Assert.All(restored.Groups, g => Assert.False(string.IsNullOrEmpty(g.Id)));
        Assert.NotEqual(restored.Groups[0].Id, restored.Groups[1].Id);

        // A second round-trip keeps the ids stable (editor save migration).
        var second = EchoObject.NewCompound();
        restored.Serialize(ref second, new SerializationContext());
        var again = new AudioMixer();
        again.Deserialize(second, new SerializationContext());
        Assert.Equal(restored.Groups[1].Id, again.Groups[1].Id);
    }

    [Fact]
    public void IdLookup_SurvivesRename_WhileNameLookupTracksNewName()
    {
        var asset = MakeAsset();
        var instance = AudioMixerInstance.Get(asset);
        string musicId = asset.Groups[1].Id;

        Assert.Equal(1, instance.GetIndex(musicId));

        asset.Groups[1].Name = "BGM"; // rename breaks name refs, not id refs
        instance.ForceRecompile();

        Assert.Equal(1, instance.GetIndex(musicId));
        Assert.Equal(1, instance.GetIndex("BGM"));
        Assert.Equal(-1, instance.GetIndex("Music"));
    }

    // --- hierarchy validation ------------------------------------------------------------------

    [Fact]
    public void UnknownOrEmptyParent_FallsBackToMaster()
    {
        var asset = MakeAsset();
        asset.Groups.Add(new AudioMixerGroupData { Id = "g1", Name = "Ghost", Parent = "DoesNotExist", VolumeDB = -6f });
        var instance = AudioMixerInstance.Get(asset);
        instance.ForceRecompile();

        int ghost = instance.GetIndex("Ghost");
        Assert.Equal(instance.GetIndex("Master"), instance.GetParentIndex(ghost));

        // Chain: Master * Ghost = 1.0 * 10^(-6/20).
        Assert.Equal(0.5f, instance.GetChainGain("Ghost"), 2);
        Assert.Equal(1f, instance.GetChainGain("SFX"), 2);
    }

    [Fact]
    public void ParentCycle_DoesNotHang_AndIsReRooted()
    {
        var asset = MakeAsset();
        // A ↔ B cycle: A parents B, B parents A.
        asset.Groups.Add(new AudioMixerGroupData { Id = "a", Name = "A", Parent = "B", VolumeDB = -6f });
        asset.Groups.Add(new AudioMixerGroupData { Id = "b", Name = "B", Parent = "A", VolumeDB = -6f });
        var instance = AudioMixerInstance.Get(asset);
        instance.ForceRecompile();

        // No hang (we got here), and every chain gain is a finite number.
        for (int i = 0; i < instance.GroupCount; i++)
            Assert.True(float.IsFinite(instance.GetChainGain(i)));
        // The cycled group is re-rooted under Master (a valid tree; the cycle is cut).
        Assert.Equal(instance.GetIndex("Master"), instance.GetParentIndex(instance.GetIndex("A")));
    }

    [Fact]
    public void DuplicateNames_FirstGroupWinsLookup()
    {
        var asset = MakeAsset();
        asset.Groups.Add(new AudioMixerGroupData { Id = "dup", Name = "SFX", Parent = "Master", VolumeDB = -60f });
        var instance = AudioMixerInstance.Get(asset);
        instance.ForceRecompile();

        int resolved = instance.GetIndex("SFX");
        Assert.Equal(1f, instance.GetChainGain("SFX"), 2); // first SFX is 0 dB (=1.0), not the -60 dB dupe
        Assert.Equal(2, resolved);
        Assert.NotEqual(3, resolved);
    }

    [Fact]
    public void UnknownSourceGroup_FallsBackToMaster()
    {
        var asset = MakeAsset();
        var instance = AudioMixerInstance.Get(asset);

        Assert.Equal(1f, instance.GetChainGain("NoSuchGroup"), 2);
    }

    // --- parity with legacy math ---------------------------------------------------------------

    [Theory]
    [InlineData(-20f, false, false)]
    [InlineData(0f, true, false)]
    [InlineData(-6f, false, true)]
    [InlineData(-40f, true, true)]
    public void InstanceChainGain_MatchesLegacyAssetMath(float musicDB, bool mute, bool solo)
    {
        var asset = MakeAsset();
        var instance = AudioMixerInstance.Get(asset);

        asset.Groups[1].VolumeDB = musicDB;
        asset.Groups[1].Mute = mute;
        asset.Groups[2].Solo = solo;
        instance.ForceRecompile();

        Assert.Equal(asset.GetGroupLinearVolume("Music"), instance.GetChainGain("Music"), 3);
        Assert.Equal(asset.GetGroupLinearVolume("SFX"), instance.GetChainGain("SFX"), 3);
    }

    // --- propagation -----------------------------------------------------------------------------

    [Fact]
    public void InstanceMutations_NotifyListeners_AndUpdateGains()
    {
        var asset = MakeAsset();
        var instance = AudioMixerInstance.Get(asset);
        int notifications = 0;
        void Handler() => notifications++;
        instance.Changed += Handler;

        int music = instance.GetIndex("Music");

        Assert.True(instance.SetGroupVolumeDB(music, -40f));
        Assert.Equal(0.01f, instance.GetChainGain("Music"), 2);
        Assert.Equal(1, notifications);

        Assert.True(instance.SetMute(music, true));
        Assert.Equal(0f, instance.GetChainGain("Music"));
        Assert.Equal(2, notifications);

        // A no-op mutation (solo already false) returns false and doesn't notify.
        Assert.False(instance.SetSolo(instance.GetIndex("Music"), false));
        Assert.Equal(2, notifications);

        instance.Changed -= Handler;
        Assert.True(instance.SetMute(music, false));
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void LegacyDirectAssetEdit_IsPickedUpByTick()
    {
        var asset = MakeAsset();
        var instance = AudioMixerInstance.Get(asset);
        int notifications = 0;
        instance.Changed += () => notifications++;

        instance.Tick(); // baseline
        Assert.Equal(0.1f, instance.GetChainGain("Music"), 2); // -20 dB default
        Assert.Equal(0, notifications);

        asset.Groups[1].VolumeDB = -40f; // legacy direct mutation, no instance call
        instance.Tick();

        Assert.Equal(0.01f, instance.GetChainGain("Music"), 2);
        Assert.Equal(1, notifications);

        instance.Tick(); // stable now: no more notifications
        instance.Tick();
        Assert.Equal(1, notifications);
    }

    // --- snapshots stay out of the asset --------------------------------------------------------

    [Fact]
    public void SnapshotOverrides_AreInstanceOnly_AndRevert()
    {
        var asset = MakeAsset();
        asset.Snapshots.Add(new AudioMixerSnapshotData
        {
            Name = "Duck",
            Volumes = { new AudioMixerSnapshotVolume { Group = "Music", VolumeDB = -60f } }
        });
        var instance = AudioMixerInstance.Get(asset);
        float before = asset.Groups[1].VolumeDB;

        Assert.True(instance.ApplySnapshot("Duck"));
        Assert.Equal(0.001f, instance.GetChainGain("Music"), 3);
        Assert.Equal("Duck", instance.ActiveSnapshot);
        Assert.Equal(before, asset.Groups[1].VolumeDB); // asset untouched

        instance.ClearSnapshot();
        Assert.Equal(before, instance.GetEffectiveVolumeDB(instance.GetIndex("Music")));
        Assert.Equal(0.1f, instance.GetChainGain("Music"), 2);
    }

    // --- zero-allocation steady state -------------------------------------------------------------

    [Fact]
    public void SteadyStateTick_AndValueEdits_AllocateNothing()
    {
        var asset = MakeAsset();
        var instance = AudioMixerInstance.Get(asset);
        instance.Changed += () => { }; // a listener, like a registered AudioSource

        instance.Tick();
        instance.ForceRecompile(); // warm every path incl. dictionary rebuilds
        int music = instance.GetIndex("Music");

        int gen0 = GC.CollectionCount(0);
        for (int i = 0; i < 1000; i++)
            instance.Tick();
        for (int i = 0; i < 1000; i++)
            instance.SetGroupVolumeDB(music, -20f - (i % 5)); // slider-drag pattern
        Assert.Equal(gen0, GC.CollectionCount(0));
    }
}
