// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies the M2 code-driven API on <see cref="global::XEngine.Runtime.Animator"/>: state reuse,
/// snap-play, crossfade blending at the midpoint, stop, and that the mixer-layer refactor keeps M1
/// single-clip playback behaviour intact.
/// </summary>
public class AnimatorCodeDrivenTests : RuntimeTestBase
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

    [Fact]
    public void Play_SameClipTwice_ReusesState()
    {
        var (_, _, animator) = BuildAnimator();
        var clip = AnimationTestHelpers.MakeConstantPosClip("c", "Hips", posX: 5f);

        var s1 = animator.Play(clip);
        var s2 = animator.Play(clip);

        Assert.Same(s1, s2);
    }

    [Fact]
    public void Play_DifferentClips_CreatesDistinctStates()
    {
        var (_, _, animator) = BuildAnimator();
        var a = AnimationTestHelpers.MakeConstantPosClip("a", "Hips", posX: 0f);
        var b = AnimationTestHelpers.MakeConstantPosClip("b", "Hips", posX: 10f);

        var sa = animator.Play(a);
        var sb = animator.Play(b);

        Assert.NotSame(sa, sb);
        Assert.Same(sa, animator.GetState(a));
        Assert.Same(sb, animator.GetState(b));
    }

    [Fact]
    public void SnapPlay_NewStateFullWeight_OthersZeroed()
    {
        var (_, _, animator) = BuildAnimator();
        var a = AnimationTestHelpers.MakeConstantPosClip("a", "Hips", posX: 0f);
        var b = AnimationTestHelpers.MakeConstantPosClip("b", "Hips", posX: 10f);

        animator.Play(a);
        animator.Play(b); // snap: A should drop to 0, B to 1.

        Assert.Equal(0f, animator.GetState(a)!.Weight);
        Assert.Equal(1f, animator.GetState(b)!.Weight);
    }

    [Fact]
    public void CrossFade_AtMidpoint_BlendsEqually()
    {
        var (scene, root, animator) = BuildAnimator();
        var a = AnimationTestHelpers.MakeConstantPosClip("a", "Hips", posX: 0f);
        var b = AnimationTestHelpers.MakeConstantPosClip("b", "Hips", posX: 10f);

        animator.Play(a);                 // A snaps to weight 1.
        animator.CrossFade(b, 1.0f);      // 1-second crossfade A -> B.
        Update(scene, frames: 30);        // 0.5s elapsed → both weights ≈ 0.5.

        // Normalized 0.5/0.5 → output pos = 0.5*0 + 0.5*10 = 5.
        Assert.Equal(5f, root.Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void CrossFade_Complete_LandsOnTarget()
    {
        var (scene, root, animator) = BuildAnimator();
        var a = AnimationTestHelpers.MakeConstantPosClip("a", "Hips", posX: 0f);
        var b = AnimationTestHelpers.MakeConstantPosClip("b", "Hips", posX: 10f);

        animator.Play(a);
        animator.CrossFade(b, 1.0f);
        Update(scene, frames: 60);        // 1.0s elapsed → fade done, B at full weight.

        Assert.Equal(10f, root.Children[0].Transform.LocalPosition.X, precision: 3);
        Assert.Equal(0f, animator.GetState(a)!.Weight, precision: 3);
    }

    [Fact]
    public void Stop_DropsStateWeightToZero()
    {
        var (_, _, animator) = BuildAnimator();
        var a = AnimationTestHelpers.MakeConstantPosClip("a", "Hips", posX: 0f);
        animator.Play(a);

        animator.Stop(a);

        Assert.Equal(0f, animator.GetState(a)!.Weight);
        Assert.False(animator.IsPlaying);
    }

    [Fact]
    public void Stop_LeavesStateRegisteredForReuse()
    {
        var (_, _, animator) = BuildAnimator();
        var a = AnimationTestHelpers.MakeConstantPosClip("a", "Hips", posX: 0f);
        var first = animator.Play(a);
        animator.Stop(a);

        var second = animator.Play(a);

        Assert.Same(first, second);
    }
}
