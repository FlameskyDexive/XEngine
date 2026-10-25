// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Playables;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M46 — <see cref="FadeMode"/> timing semantics (FixedSpeed/FixedDuration/Normalized*), fade easing
/// curves, and FadeMode.FromStart weightless-clone restarts on <see cref="global::XEngine.Runtime.Animator"/>.
/// </summary>
public class FadeModeTests : RuntimeTestBase
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

    // Both clips animate Hips X; the blended output therefore tracks the weight ratio directly.
    private static AnimationClip ClipA() => AnimationTestHelpers.MakeConstantPosClip("a", "Hips", posX: 0f);
    private static AnimationClip ClipB() => AnimationTestHelpers.MakeConstantPosClip("b", "Hips", posX: 10f);

    [Fact]
    public void FixedDuration_FromHalfWeight_TakesFullDuration()
    {
        var (scene, _, animator) = BuildAnimator();
        var a = ClipA();
        var b = ClipB();

        animator.Play(a);
        animator.CrossFade(b, 1.0f);              // A→B over 1s.
        Update(scene, frames: 30);                // 0.5s: a≈0.5, b≈0.5.
        Assert.True(System.MathF.Abs(animator.GetState(a)!.Weight - 0.5f) < 0.02f);

        animator.CrossFade(a, 1.0f, FadeMode.FixedDuration); // From 0.5 back to 1 over a FULL second.
        Update(scene, frames: 30);                // 0.5s of that fade: a = 0.5 + 0.5*0.5 = 0.75.

        Assert.True(System.MathF.Abs(animator.GetState(a)!.Weight - 0.75f) < 0.02f,
            $"expected ~0.75, got {animator.GetState(a)!.Weight}");
    }

    [Fact]
    public void FixedSpeed_FromHalfWeight_FinishesInHalfDuration()
    {
        var (scene, _, animator) = BuildAnimator();
        var a = ClipA();
        var b = ClipB();

        animator.Play(a);
        animator.CrossFade(b, 1.0f);
        Update(scene, frames: 30);                // a≈0.5, b≈0.5.

        animator.CrossFade(a, 0.5f, FadeMode.FixedSpeed); // Speed 2/s: 0.5→1 in 0.25s.
        Update(scene, frames: 15);                // 0.25s.

        Assert.True(System.MathF.Abs(animator.GetState(a)!.Weight - 1f) < 0.02f,
            $"expected ~1.0, got {animator.GetState(a)!.Weight}");
        Assert.True(animator.GetState(b)!.Weight < 0.02f, "B should fade out at the same fixed speed");
    }

    [Fact]
    public void NormalizedFixedDuration_ScalesDurationByClipLength()
    {
        var (scene, _, animator) = BuildAnimator();
        var a = AnimationTestHelpers.MakeConstantPosClip("a", "Hips", posX: 0f, duration: 2f); // 2s clip.
        var b = AnimationTestHelpers.MakeConstantPosClip("b", "Hips", posX: 10f, duration: 2f);

        animator.Play(a);
        animator.CrossFade(b, 0.5f, FadeMode.NormalizedFixedDuration); // 0.5 × 2s = 1s fade.

        Update(scene, frames: 30);                // 0.5s elapsed of the 1s fade → b ≈ 0.5.

        Assert.True(System.MathF.Abs(animator.GetState(b)!.Weight - 0.5f) < 0.02f,
            $"expected ~0.5, got {animator.GetState(b)!.Weight}");
    }

    [Fact]
    public void Easing_QuadIn_FollowsSquaredCurve()
    {
        var (scene, _, animator) = BuildAnimator();
        var a = ClipA();
        var b = ClipB();

        animator.Play(a);
        animator.CrossFade(b, 1.0f, FadeMode.FixedDuration, FadeEasing.QuadIn);

        Update(scene, frames: 15);                // t = 0.25 → eased 0.0625.

        Assert.True(System.MathF.Abs(animator.GetState(b)!.Weight - 0.0625f) < 0.01f,
            $"expected ~0.0625, got {animator.GetState(b)!.Weight}");
    }

    [Fact]
    public void Easing_OvershootingCurve_LandsExactlyOnTarget()
    {
        var (scene, _, animator) = BuildAnimator();
        var a = ClipA();
        var b = ClipB();

        animator.Play(a);
        animator.CrossFade(b, 1.0f, FadeMode.FixedDuration, FadeEasing.BackOut); // Overshoots past 1 mid-fade.
        Update(scene, frames: 60);

        Assert.Equal(1f, animator.GetState(b)!.Weight, precision: 3);
        Assert.Equal(0f, animator.GetState(a)!.Weight, precision: 3);
    }

    [Fact]
    public void FromStart_ActiveClip_PlaysCloneFromTimeZero()
    {
        var (scene, root, animator) = BuildAnimator();
        var clip = AnimationTestHelpers.MakeConstantPosClip("c", "Hips", posX: 5f); // 1s loop.

        var main = animator.Play(clip);
        Update(scene, frames: 30);                // Main is halfway through its first loop.
        Assert.True(main.NormalizedTime > 0.25f, "precondition: main state has advanced");

        var clone = animator.Play(clip, 0.5f, FadeMode.FromStart);

        Assert.NotSame(main, clone);
        Assert.Equal(0f, clone.NormalizedTime, precision: 3);
        Assert.Same(clip, animator.CurrentClip);

        Update(scene, frames: 30);                // 0.5s ≥ fade: clone full weight, main faded out.

        Assert.Equal(1f, clone.Weight, precision: 2);
        Assert.Equal(0f, main.Weight, precision: 2);
        Assert.Equal(5f, root.Children[0].Transform.LocalPosition.X, precision: 2);
    }

    [Fact]
    public void FromStart_InactiveClip_PlaysMainState()
    {
        var (scene, _, animator) = BuildAnimator();
        var clip = AnimationTestHelpers.MakeConstantPosClip("c", "Hips", posX: 5f);

        animator.Play(clip);
        animator.Stop(clip);                      // Weight 0 → no clone needed.

        var state = animator.Play(clip, 1.0f, FadeMode.FromStart);

        Assert.Same(animator.GetState(clip), state);
    }

    [Fact]
    public void FromStart_ClonesAreCappedAndReused()
    {
        var (_, _, animator) = BuildAnimator();
        var clip = AnimationTestHelpers.MakeConstantPosClip("c", "Hips", posX: 5f);

        animator.Play(clip);
        var c1 = animator.Play(clip, 0.5f, FadeMode.FromStart);
        var c2 = animator.Play(clip, 0.5f, FadeMode.FromStart);
        var c3 = animator.Play(clip, 0.5f, FadeMode.FromStart);
        var c4 = animator.Play(clip, 0.5f, FadeMode.FromStart);

        Assert.NotSame(c1, c2);
        Assert.NotSame(c2, c3);
        Assert.Same(c1, c4);                      // Cap reached: lightest (first) clone reused.
        Assert.Equal(4, animator.Layer!.InputCount); // Main + exactly 3 clones.
    }

    [Fact]
    public void Stop_AlsoStopsFromStartClones()
    {
        var (_, _, animator) = BuildAnimator();
        var clip = AnimationTestHelpers.MakeConstantPosClip("c", "Hips", posX: 5f);

        animator.Play(clip);
        animator.Play(clip, 0.5f, FadeMode.FromStart);

        animator.Stop(clip);

        Assert.Equal(0f, animator.GetState(clip)!.Weight);
        Assert.False(animator.IsPlaying);
        int n = animator.Layer!.InputCount;
        for (int i = 0; i < n; i++)
            Assert.Equal(0f, animator.Layer.GetInput(i).Weight);
    }

    [Fact]
    public void EasedFade_SteamState_IsAllocationFree()
    {
        var (scene, _, animator) = BuildAnimator();
        var a = ClipA();
        var b = ClipB();

        animator.Play(a);
        animator.CrossFade(b, 1.0f, FadeMode.FixedDuration, FadeEasing.CubicInOut);
        Update(scene, frames: 10);                // Warm up (binds, caches).

        animator.CrossFade(a, 1.0f, FadeMode.FixedSpeed, FadeEasing.SineInOut);
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        Update(scene, frames: 60);
        long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
    }
}
