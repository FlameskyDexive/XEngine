// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Xunit;

using XEngine.Animation;
using XEngine.Echo;
using XEngine.Playables;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M47 — Animancer-style transitions: Echo polymorphic round-trip ($type envelope) for
/// <see cref="ClipTransition"/> and <see cref="TransitionAsset"/>, state caching per transition /
/// per asset, Apply semantics (speed, normalized start time, loop override), and fade behaviour
/// driven by the transition's own settings.
/// </summary>
public class TransitionTests : RuntimeTestBase
{
    private (Scene scene, GameObject root, Animator animator) BuildAnimator()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        return (scene, root, animator);
    }

    private static ClipTransition MakeClipTransition(string clipName, float posX, float fade = 0.25f)
        => new()
        {
            Clip = new AssetRef<AnimationClip>(
                AnimationTestHelpers.MakeConstantPosClip(clipName, "Hips", posX)),
            FadeDuration = fade,
        };

    [Fact]
    public void Echo_RoundTrip_PreservesConcreteTransitionTypeAndFields()
    {
        Guid clipId = Guid.Parse("aaaa1111-2222-3333-4444-555566667777");
        var transition = new ClipTransition
        {
            Clip = new AssetRef<AnimationClip>(clipId),
            FadeDuration = 0.4f,
            FadeMode = FadeMode.NormalizedFromStart,
            FadeEasing = FadeEasing.CubicOut,
            Speed = 1.5f,
            NormalizedStartTime = 0.25f,
            Loop = true,
        };

        EchoObject echo = Serializer.Serialize(typeof(object), transition);
        string text = echo.WriteToString();

        Assert.Contains("$type", text);
        Assert.Contains(nameof(ClipTransition), text);

        var back = Serializer.Deserialize<AnimationTransition>(echo) as ClipTransition;
        Assert.NotNull(back);
        Assert.Equal(clipId, back!.Clip.AssetID);
        Assert.Equal(0.4f, back.FadeDuration);
        Assert.Equal(FadeMode.NormalizedFromStart, back.FadeMode);
        Assert.Equal(FadeEasing.CubicOut, back.FadeEasing);
        Assert.Equal(1.5f, back.Speed);
        Assert.Equal(0.25f, back.NormalizedStartTime);
        Assert.True(back.Loop);
    }

    [Fact]
    public void Echo_RoundTrip_TransitionAssetKeepsPolymorphicPayload()
    {
        Guid clipId = Guid.Parse("bbbb1111-2222-3333-4444-555566667777");
        var asset = new TransitionAsset { Name = "t" };
        asset.Transition = new ClipTransition
        {
            Clip = new AssetRef<AnimationClip>(clipId),
            FadeDuration = 0.75f,
            FadeMode = FadeMode.FixedSpeed,
        };

        EchoObject echo = Serializer.Serialize(typeof(TransitionAsset), asset);
        var back = Serializer.Deserialize<TransitionAsset>(echo);

        Assert.NotNull(back);
        var clip = Assert.IsType<ClipTransition>(back!.Transition);
        Assert.Equal(clipId, clip.Clip.AssetID);
        Assert.Equal(0.75f, clip.FadeDuration);
        Assert.Equal(FadeMode.FixedSpeed, clip.FadeMode);
    }

    [Fact]
    public void Play_ClipTransition_ReusesStatePerTransition()
    {
        var (_, _, animator) = BuildAnimator();
        var t = MakeClipTransition("a", 5f);

        var s1 = animator.Play(t);
        var s2 = animator.Play(t);

        Assert.Same(s1, s2);
    }

    [Fact]
    public void Play_TransitionAsset_AllReferencesShareOneState()
    {
        var (_, _, animator) = BuildAnimator();
        var asset = new TransitionAsset { Name = "shared" };
        asset.Transition = MakeClipTransition("a", 5f);

        var s1 = animator.Play(asset);
        var s2 = animator.Play(asset);

        Assert.Same(s1, s2);
    }

    [Fact]
    public void Play_ClipTransition_DrivesBoneAndAppliesSettings()
    {
        var (scene, root, animator) = BuildAnimator();
        var t = MakeClipTransition("a", 8f, fade: 0f);
        t.Speed = 2f;

        var state = (AnimationClipPlayable)animator.Play(t);
        Update(scene, frames: 20);            // 1/3s at 2x speed = 0.67s of the 1s clip.

        Assert.Equal(8f, root.Children[0].Transform.LocalPosition.X, precision: 3);
        Assert.Equal(2f, state.Speed);
        Assert.True(state.NormalizedTime > 0.5f && state.NormalizedTime < 0.9f,
            $"expected ~0.67 normalized, got {state.NormalizedTime}");
    }

    [Fact]
    public void Play_ClipTransition_StartTimeIsReappliedEachPlay()
    {
        var (scene, _, animator) = BuildAnimator();
        var t = MakeClipTransition("a", 5f, fade: 0f);
        t.NormalizedStartTime = 0.1f;

        var state = (AnimationClipPlayable)animator.Play(t);
        Update(scene, frames: 30);            // Advance well past the start.
        animator.Play(t);                     // Replay resets to the transition's start time.

        Assert.True(System.MathF.Abs(state.NormalizedTime - 0.1f) < 0.02f,
            $"expected restart near 0.1, got {state.NormalizedTime}");
    }

    [Fact]
    public void Play_LoopOverride_ForcesLoopWrap()
    {
        var (_, _, animator) = BuildAnimator();
        var clip = AnimationTestHelpers.MakeConstantPosClip("once", "Hips", 5f, duration: 1f);
        clip.Wrap = AnimationWrapMode.Once;
        var t = new ClipTransition { Clip = new AssetRef<AnimationClip>(clip), Loop = true };

        var state = (AnimationClipPlayable)animator.Play(t);

        Assert.Equal(AnimationWrapMode.Loop, state.Wrap);
    }

    [Fact]
    public void Play_TransitionFadesAccordingToItsOwnSettings()
    {
        var (scene, root, animator) = BuildAnimator();
        var a = MakeClipTransition("a", 0f, fade: 0f);
        var b = MakeClipTransition("b", 10f, fade: 1f);

        animator.Play(a);
        animator.Play(b);                     // 1s crossfade per b's own FadeDuration.
        Update(scene, frames: 30);            // Midpoint: 0.5/0.5 → x = 5.

        Assert.Equal(5f, root.Children[0].Transform.LocalPosition.X, precision: 3);
        Assert.True(System.MathF.Abs(animator.GetState(b)!.Weight - 0.5f) < 0.02f);
    }

    [Fact]
    public void Play_TransitionWithFixedSpeedEasing_HonoursBoth()
    {
        var (scene, _, animator) = BuildAnimator();
        var a = MakeClipTransition("a", 0f, fade: 0f);
        var b = MakeClipTransition("b", 10f, fade: 1f);
        b.FadeMode = FadeMode.FixedSpeed;
        b.FadeEasing = FadeEasing.QuadIn;

        animator.Play(a);
        animator.Play(b);
        Update(scene, frames: 15);            // t = 0.25 → eased 0.0625 (from 0).

        var stateB = animator.GetState(b)!;
        Assert.True(System.MathF.Abs(stateB.Weight - 0.0625f) < 0.01f,
            $"expected ~0.0625, got {stateB.Weight}");
    }

    [Fact]
    public void Play_TwoTransitionsSameClip_CreatesDistinctStates()
    {
        var (_, _, animator) = BuildAnimator();
        var clip = AnimationTestHelpers.MakeConstantPosClip("c", "Hips", 5f);
        var t1 = new ClipTransition { Clip = new AssetRef<AnimationClip>(clip) };
        var t2 = new ClipTransition { Clip = new AssetRef<AnimationClip>(clip) };

        var s1 = animator.Play(t1);
        var s2 = animator.Play(t2);

        Assert.NotSame(s1, s2); // Keyed per transition object, not per clip (Animancer semantics).
    }
}
