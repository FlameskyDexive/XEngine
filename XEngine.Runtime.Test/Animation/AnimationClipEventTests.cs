// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Echo;
using XEngine.Playables;
using XEngine.Runtime;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies the M2 normalized-time event system on <see cref="AnimationClipPlayable"/>: events fire
/// when the clip's local time crosses the registered threshold, the end event fires at the clip's
/// duration, loop clips re-fire the end event each loop, and clips with no events cost nothing.
/// M49 note: runtime events now fire through the deferred <see cref="PlayableEventQueue"/>, so this
/// direct-drive harness flushes the shared queue after advancing (the graph-driven paths flush at
/// the end of PlayableGraph.Evaluate).
/// </summary>
public class AnimationClipEventTests : RuntimeTestBase
{
    private static readonly PlayableContext Ctx = new(0f, 0d, false);

    private (SkeletonBinding binding, AnimationClipPlayable playable) BuildWithClip(float duration = 1f, AnimationWrapMode wrap = AnimationWrapMode.ClampForever)
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var binding = SkeletonBinding.Build(root.Transform);

        var clip = AnimationTestHelpers.MakeSingleBonePosXClip("c", "Hips", endX: 10f, duration: duration);
        clip.Wrap = wrap;
        var playable = new AnimationClipPlayable(clip);
        playable.Bind(binding);
        playable.Play();
        return (binding, playable);
    }

    private static void Advance(AnimationClipPlayable p, float seconds)
    {
        // 1/60 per step, matching RuntimeTestBase's FixedDeltaTime.
        int frames = (int)MathF.Round(seconds * 60f);
        for (int i = 0; i < frames; i++)
        {
            p.PrepareFrame(new PlayableContext(1f / 60f, 0d, false));
            p.ProcessFrame(Ctx);
            PlayableEventQueue.Shared.Flush(null); // M49: events are deferred until flush.
        }
    }

    [Fact]
    public void NormalizedTime_TracksLocalTimeOverDuration()
    {
        var (_, p) = BuildWithClip(duration: 2f);
        p.SetTime(0.5);

        Assert.Equal(0.25f, p.NormalizedTime, precision: 4); // 0.5 / 2.0
    }

    [Fact]
    public void AddEvent_FiresWhenThresholdCrossed()
    {
        var (_, p) = BuildWithClip(duration: 1f);
        int fires = 0;
        p.AddEvent(0.5f, () => fires++);

        Advance(p, 0.5f); // crosses 0.5 at the last frame.

        Assert.Equal(1, fires);
    }

    [Fact]
    public void AddEvent_BeforeThreshold_DoesNotFire()
    {
        var (_, p) = BuildWithClip(duration: 1f);
        int fires = 0;
        p.AddEvent(0.7f, () => fires++);

        Advance(p, 0.5f); // only reached 0.5, event is at 0.7.

        Assert.Equal(0, fires);
    }

    [Fact]
    public void AddEvent_FiresExactlyOnce_ForwardPlay()
    {
        var (_, p) = BuildWithClip(duration: 1f);
        int fires = 0;
        p.AddEvent(0.3f, () => fires++);

        Advance(p, 1.0f); // crosses 0.3 once; ClampForever holds at 1.0 after.

        Assert.Equal(1, fires);
    }

    [Fact]
    public void SetEndEvent_FiresAtDuration()
    {
        var (_, p) = BuildWithClip(duration: 1f, wrap: AnimationWrapMode.ClampForever);
        int fires = 0;
        p.SetEndEvent(() => fires++);

        Advance(p, 1.0f);

        Assert.Equal(1, fires);
    }

    [Fact]
    public void ZeroDuration_SetEndEvent_UsesOneSecondStateClock()
    {
        var (_, p) = BuildWithClip(duration: 0f, wrap: AnimationWrapMode.Loop);
        int fires = 0;
        p.SetEndEvent(() => fires++);

        Advance(p, 1.0f);

        Assert.Equal(1f, p.NormalizedTime, precision: 4);
        Assert.Equal(1, fires);
    }

    [Fact]
    public void Loop_EndEvent_RefiresEachLoop()
    {
        var (_, p) = BuildWithClip(duration: 1f, wrap: AnimationWrapMode.Loop);
        int fires = 0;
        p.SetEndEvent(() => fires++);

        Advance(p, 2.0f); // two full loops → end event fires twice (once per wrap).

        Assert.Equal(2, fires);
    }

    [Fact]
    public void ClearEvents_StopsFiring()
    {
        var (_, p) = BuildWithClip(duration: 1f);
        int fires = 0;
        p.AddEvent(0.3f, () => fires++);
        p.AddEvent(0.8f, () => fires++);

        Advance(p, 0.5f); // first event fires (1).
        p.ClearEvents();
        Advance(p, 0.5f); // second event would have fired but was cleared.

        Assert.Equal(1, fires);
    }

    [Fact]
    public void AuthoredClipEvent_InvokesNamedMethodOnRoot()
    {
        var root = CreateGameObject("Rig");
        var sink = root.AddComponent<AnimEventSink>();
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var binding = SkeletonBinding.Build(root.Transform);

        var clip = AnimationTestHelpers.MakeSingleBonePosXClip("c", "Hips", endX: 10f, duration: 1f);
        clip.Events.Add(new AnimationClipEvent { Time = 0.5f, FunctionName = "Footstep" });
        var p = new AnimationClipPlayable(clip);
        p.Bind(binding);
        p.Play();

        Advance(p, 0.5f);
        Assert.Equal(1, sink.Hits);
    }

    [Fact]
    public void AuthoredClipEvent_PassesFloatParam()
    {
        var root = CreateGameObject("Rig");
        var sink = root.AddComponent<AnimEventSink>();
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var binding = SkeletonBinding.Build(root.Transform);

        var clip = AnimationTestHelpers.MakeSingleBonePosXClip("c", "Hips", endX: 10f, duration: 1f);
        clip.Events.Add(new AnimationClipEvent { Time = 0.5f, FunctionName = "OnLand", FloatParam = 3.5f });
        var p = new AnimationClipPlayable(clip);
        p.Bind(binding);
        p.Play();

        Advance(p, 0.5f);
        Assert.Equal(1, sink.Hits);
        Assert.Equal(3.5f, sink.LastFloat);
    }

    [Fact]
    public void SerializeRoundTrip_PreservesAuthoredEvent()
    {
        var clip = new AnimationClip { Name = "ev", Duration = 1f };
        clip.Events.Add(new AnimationClipEvent { Time = 0.25f, FunctionName = "Footstep", FloatParam = 1.5f, IntParam = 2, StringParam = "heel" });
        EchoObject echo = Serializer.Serialize(clip, TypeMode.Aggressive);
        string text = echo.WriteToString();
        Assert.Contains("Footstep", text);
        Assert.Contains("Events", text);

        var loaded = Serializer.Deserialize<AnimationClip>(EchoObject.ReadFromString(text));
        Assert.NotNull(loaded);
        AnimationClipEvent e = Assert.Single(loaded!.Events);
        Assert.Equal(0.25f, e.Time);
        Assert.Equal("Footstep", e.FunctionName);
        Assert.Equal(1.5f, e.FloatParam);
        Assert.Equal(2, e.IntParam);
        Assert.Equal("heel", e.StringParam);
    }

    [Fact]
    public void Deserialize_OldClipWithoutEvents_HasEmptyList()
    {
        var clip = new AnimationClip { Name = "old", Duration = 1f };
        EchoObject echo = Serializer.Serialize(clip, TypeMode.Aggressive);
        string text = echo.WriteToString();
        Assert.DoesNotContain("Events", text);

        var loaded = Serializer.Deserialize<AnimationClip>(EchoObject.ReadFromString(text));
        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Events);
    }
}

public sealed class AnimEventSink : MonoBehaviour
{
    public int Hits;
    public float LastFloat;

    public void Footstep() => Hits++;
    public void OnLand(float v)
    {
        LastFloat = v;
        Hits++;
    }
}
