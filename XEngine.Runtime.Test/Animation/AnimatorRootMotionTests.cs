// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies M6 root motion: the current clip's root-bone motion is extracted each frame and applied
/// to the Animator's GameObject, the root bone is pinned to its bind offset, and <see cref="global::XEngine.Runtime.Animator.OnAnimatorMove"/>
/// switches the Animator to manual application (no auto-move).
/// </summary>
public class AnimatorRootMotionTests : RuntimeTestBase
{
    private (Scene scene, GameObject armature, GameObject hips, Animator animator) BuildRootMotionSetup()
    {
        var scene = CreateScene(enable: true);
        var armature = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(armature);
        scene.Add(armature);

        var animator = new Animator
        {
            PlayAutomatically = false,
            ApplyRootMotion = true,
            Wrap = AnimationWrapMode.ClampForever, // root-motion clips clamp so accumulation is continuous
        };
        armature.AddComponent(animator);

        // Linear clip moving Hips.PosX 0 -> 1 over 1s, with Hips as the root-motion bone.
        var clip = AnimationTestHelpers.MakeSingleBonePosXClip("walk", "Hips", endX: 1f, duration: 1f);
        clip.RootBonePath = "Hips";
        animator.Play(clip);
        return (scene, armature, hips, animator);
    }

    [Fact]
    public void RootMotion_MovesGameObject_ByRootBoneDelta()
    {
        var (scene, armature, hips, _) = BuildRootMotionSetup();

        Update(scene, frames: 30); // 0.5s → root bone PosX = 0.5.

        // The Armature (Animator's GO) moves by the accumulated root motion; the Hips bone is pinned.
        Assert.Equal(0.5f, armature.Transform.LocalPosition.X, precision: 3);
        Assert.Equal(0f, hips.Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void RootMotion_AccumulatesOverTime()
    {
        var (scene, armature, _, _) = BuildRootMotionSetup();

        Update(scene, frames: 60); // 1.0s → root bone PosX = 1.0.

        Assert.Equal(1.0f, armature.Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void OnAnimatorMove_SuppressesAutoApply_DeliversDelta()
    {
        var (scene, armature, _, animator) = BuildRootMotionSetup();

        Float3 received = default;
        int calls = 0;
        animator.OnAnimatorMove = (delta, rot) => { received += delta; calls++; };

        Update(scene, frames: 30);

        // With a handler set, the Animator must NOT move itself.
        Assert.Equal(0f, armature.Transform.LocalPosition.X, precision: 3);
        Assert.True(calls > 0);
        // The handler accumulated the same total delta the auto path would have applied.
        Assert.Equal(0.5f, received.X, precision: 3);
    }

    [Fact]
    public void ApplyRootMotionFalse_NoExtraction()
    {
        var scene = CreateScene(enable: true);
        var armature = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(armature);
        scene.Add(armature);
        var animator = new Animator { PlayAutomatically = false, ApplyRootMotion = false };
        armature.AddComponent(animator);
        var clip = AnimationTestHelpers.MakeSingleBonePosXClip("walk", "Hips", endX: 1f, duration: 1f);
        clip.RootBonePath = "Hips";
        animator.Play(clip);

        Update(scene, frames: 30);

        // Root motion off → the bone just animates in place, GameObject stays put.
        Assert.Equal(0f, armature.Transform.LocalPosition.X, precision: 3);
        Assert.Equal(0.5f, hips.Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void RootMotion_AppliesRotationDelta_AndPinsBone()
    {
        var scene = CreateScene(enable: true);
        var armature = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(armature);
        scene.Add(armature);

        var animator = new Animator
        {
            PlayAutomatically = false,
            ApplyRootMotion = true,
            Wrap = AnimationWrapMode.ClampForever,
        };
        armature.AddComponent(animator);

        Quaternion end = Quaternion.FromEuler(0f, 90f, 0f);
        var clip = AnimationTestHelpers.MakeSingleBoneRotClip("turn", "Hips", Quaternion.Identity, end, duration: 1f);
        clip.RootBonePath = "Hips";
        animator.Play(clip);

        Update(scene, frames: 60); // t = 1s → end rotation.

        Quaternion got = armature.Transform.LocalRotation;
        Assert.Equal(end.X, got.X, precision: 2);
        Assert.Equal(end.Y, got.Y, precision: 2);
        Assert.Equal(end.Z, got.Z, precision: 2);
        Assert.Equal(end.W, got.W, precision: 2);
        Assert.Equal(Quaternion.Identity.X, hips.Transform.LocalRotation.X, precision: 3);
        Assert.Equal(Quaternion.Identity.Y, hips.Transform.LocalRotation.Y, precision: 3);
        Assert.Equal(Quaternion.Identity.Z, hips.Transform.LocalRotation.Z, precision: 3);
        Assert.Equal(Quaternion.Identity.W, hips.Transform.LocalRotation.W, precision: 3);
    }

    [Fact]
    public void OnAnimatorMove_DeliversRotationDelta()
    {
        var scene = CreateScene(enable: true);
        var armature = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(armature);
        scene.Add(armature);

        var animator = new Animator
        {
            PlayAutomatically = false,
            ApplyRootMotion = true,
            Wrap = AnimationWrapMode.ClampForever,
        };
        armature.AddComponent(animator);

        Quaternion end = Quaternion.FromEuler(0f, 90f, 0f);
        var clip = AnimationTestHelpers.MakeSingleBoneRotClip("turn", "Hips", Quaternion.Identity, end, duration: 1f);
        clip.RootBonePath = "Hips";
        animator.Play(clip);

        Quaternion acc = Quaternion.Identity;
        animator.OnAnimatorMove = (_, rot) => { acc = acc * rot; };

        Update(scene, frames: 60);

        Assert.Equal(Quaternion.Identity.W, armature.Transform.LocalRotation.W, precision: 3);
        Assert.Equal(end.X, acc.X, precision: 2);
        Assert.Equal(end.Y, acc.Y, precision: 2);
        Assert.Equal(end.Z, acc.Z, precision: 2);
        Assert.Equal(end.W, acc.W, precision: 2);
    }
}
