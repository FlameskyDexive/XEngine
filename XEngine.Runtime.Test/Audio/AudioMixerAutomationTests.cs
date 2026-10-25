// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Audio;

using Xunit;

namespace XEngine.Runtime.Test.Audio;

/// <summary>
/// N3: snapshot transition tweens and duck envelopes — layered effective volume, cancellation,
/// handle safety, stacking, and allocation-free steady-state envelopes.
/// </summary>
public sealed class AudioMixerAutomationTests
{
    private static (AudioMixer Asset, AudioMixerInstance Instance) Make()
    {
        var asset = new AudioMixer { Name = "Mixer" };
        asset.Groups.Add(new AudioMixerGroupData { Id = "m", Name = "Music", Parent = "Master", VolumeDB = 0f });
        asset.Groups.Add(new AudioMixerGroupData { Id = "s", Name = "SFX", Parent = "Master", VolumeDB = 0f });
        asset.Snapshots.Add(new AudioMixerSnapshotData
        {
            Name = "Quiet",
            Volumes = { new AudioMixerSnapshotVolume { Group = "Music", VolumeDB = -20f } }
        });
        var instance = AudioMixerInstance.Get(asset);
        instance.ForceRecompile();
        return (asset, instance);
    }

    [Fact]
    public void Transition_TweensEffectiveVolume_AndKeepsAssetUntouched()
    {
        var (asset, instance) = Make();
        float before = asset.Groups[1].VolumeDB;

        Assert.True(instance.Automation.TransitionToSnapshot("Quiet", 1f, AudioFadeCurve.Linear));
        Assert.True(instance.Automation.IsTransitioning);

        float quarter = instance.GetEffectiveVolumeDB(1);
        instance.Tick(0.5f);
        float half = instance.GetEffectiveVolumeDB(1);
        instance.Tick(0.5f);
        float end = instance.GetEffectiveVolumeDB(1);

        Assert.True(quarter > half, $"{quarter} > {half}");      // decaying
        Assert.True(half > end, $"{half} > {end}");
        Assert.Equal(-20f, end, 2);                              // landed on the override
        Assert.False(instance.Automation.IsTransitioning);
        Assert.Equal(before, asset.Groups[1].VolumeDB);          // asset never written
        Assert.Equal(-20f, instance.GetEffectiveVolumeDB(1), 2); // stays at snapshot level
    }

    [Fact]
    public void CancelTransition_FreezesWithoutJump()
    {
        var (_, instance) = Make();
        instance.Automation.TransitionToSnapshot("Quiet", 1f, AudioFadeCurve.Linear);
        instance.Tick(0.4f);
        float frozen = instance.GetEffectiveVolumeDB(1);

        instance.Automation.CancelTransition();
        Assert.False(instance.Automation.IsTransitioning);

        instance.Tick(0.5f);
        instance.Tick(0.5f);
        Assert.Equal(frozen, instance.GetEffectiveVolumeDB(1), 3); // no further movement, no jump
    }

    [Fact]
    public void SecondTransition_ReplacesFirst()
    {
        var (asset, instance) = Make();
        asset.Snapshots.Add(new AudioMixerSnapshotData
        {
            Name = "Loud",
            Volumes = { new AudioMixerSnapshotVolume { Group = "Music", VolumeDB = 6f } }
        });

        instance.Automation.TransitionToSnapshot("Quiet", 1f);
        instance.Tick(0.3f);
        Assert.True(instance.Automation.TransitionToSnapshot("Loud", 1f));
        Assert.Equal("Loud", instance.Automation.TransitionTargetSnapshot);

        instance.Tick(2f);
        Assert.Equal(6f, instance.GetEffectiveVolumeDB(1), 2);
    }

    [Fact]
    public void Duck_ReducesWhileHeld_AndRecoversAfterRelease()
    {
        var (_, instance) = Make();
        int music = instance.GetIndex("Music");
        float fullGain = instance.GetChainGain(music);

        var handle = instance.Automation.AcquireDuck(music, duckDB: 20f, attackSeconds: 0.5f, releaseSeconds: 0.5f);
        Assert.True(handle.IsValid);

        instance.Tick(0.5f); // fully ducked
        Assert.Equal(0.1f, instance.GetChainGain(music) / fullGain, 2);
        var (reduction, holders) = instance.Automation.GetDuckInfo(music);
        Assert.Equal(20f, reduction, 1);
        Assert.Equal(1, holders);

        Assert.True(instance.Automation.ReleaseDuck(ref handle));
        Assert.False(handle.IsValid); // consumed
        instance.Tick(0.5f);          // release envelope done
        Assert.Equal(fullGain, instance.GetChainGain(music), 3);
        var (after, afterHolders) = instance.Automation.GetDuckInfo(music);
        Assert.Equal(0f, after, 2);
        Assert.Equal(0, afterHolders);
    }

    [Fact]
    public void Duck_StaleAndInvalidHandles_AreSafeNoOps()
    {
        var (_, instance) = Make();
        int music = instance.GetIndex("Music");

        var handle = instance.Automation.AcquireDuck(music, 6f, 0f, 0.1f);
        Assert.True(instance.Automation.ReleaseDuck(ref handle));
        Assert.False(instance.Automation.ReleaseDuck(ref handle)); // double release

        var stale = new AudioDuckHandle(1, 999);
        Assert.False(instance.Automation.ReleaseDuck(ref stale));
        var invalid = default(AudioDuckHandle);
        Assert.False(instance.Automation.ReleaseDuck(ref invalid));
    }

    [Fact]
    public void Duck_StackedHolders_UseDeepest_AndReleaseOnLast()
    {
        var (_, instance) = Make();
        int music = instance.GetIndex("Music");

        var first = instance.Automation.AcquireDuck(music, 6f, 0f, 0.2f);
        var second = instance.Automation.AcquireDuck(music, 12f, 0f, 0.2f);
        instance.Tick(1f);
        Assert.Equal(12f, instance.Automation.GetDuckInfo(music).ReductionDB, 1);
        Assert.Equal(2, instance.Automation.GetDuckInfo(music).Holders);

        Assert.True(instance.Automation.ReleaseDuck(ref first));
        instance.Tick(1f);
        Assert.Equal(12f, instance.Automation.GetDuckInfo(music).ReductionDB, 1); // second still holds

        Assert.True(instance.Automation.ReleaseDuck(ref second));
        instance.Tick(1f);
        Assert.Equal(0f, instance.Automation.GetDuckInfo(music).ReductionDB, 2);
    }

    [Fact]
    public void AutomationTick_IsAllocationFree()
    {
        var (_, instance) = Make();
        int music = instance.GetIndex("Music");
        var handle = instance.Automation.AcquireDuck(music, 12f, 0.5f, 0.5f);
        instance.Automation.TransitionToSnapshot("Quiet", 5f, AudioFadeCurve.Linear);

        instance.Tick(0.1f); // warm the paths
        int gen0 = GC.CollectionCount(0);
        for (int i = 0; i < 1000; i++)
            instance.Tick(0.016f);
        Assert.Equal(gen0, GC.CollectionCount(0));

        instance.Automation.ReleaseDuck(ref handle);
    }
}
