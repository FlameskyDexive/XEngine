// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M52 — API parity batch: GetNextAnimatorStateInfo during a crossfade, manual Animator.Update
/// stepping, fade-out exit callbacks, and IsInTransition for code-driven fades.
/// </summary>
public class AnimatorApiParityTests : RuntimeTestBase
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
    public void NextStateInfo_DuringCodeDrivenCrossfade_ReportsDestination()
    {
        var (scene, _, animator) = BuildAnimator();
        var a = AnimationTestHelpers.MakeConstantPosClip("a", "Hips", 0f);
        var b = AnimationTestHelpers.MakeConstantPosClip("b", "Hips", 10f);

        animator.Play(a);
        Assert.False(animator.IsInTransition());

        animator.CrossFade(b, 1f);
        Assert.True(animator.IsInTransition());
        AnimatorStateInfo next = animator.GetNextAnimatorStateInfo();
        Assert.Equal(global::XEngine.Animation.AnimationNameHash.Hash("b"), next.nameHash);

        Update(scene, frames: 65);                    // Fade completes (float accumulation margin).
        Assert.False(animator.IsInTransition());
    }

    [Fact]
    public void ManualUpdate_AdvancesWithoutSceneTick()
    {
        var (_, root, animator) = BuildAnimator();
        var clip = AnimationTestHelpers.MakeSingleBonePosXClip("move", "Hips", endX: 10f);
        animator.Play(clip);

        animator.Update(0.5f);                        // Manual half-clip step, no Scene.Update call.

        Assert.True(root.Children[0].Transform.LocalPosition.X > 4f
            && root.Children[0].Transform.LocalPosition.X < 6f,
            "manual Update should sample the clip at 0.5s");
    }

    [Fact]
    public void OnFadeOut_FiresOnceAfterWeightSettles()
    {
        var (scene, _, animator) = BuildAnimator();
        var a = AnimationTestHelpers.MakeConstantPosClip("a", "Hips", 0f);
        var b = AnimationTestHelpers.MakeConstantPosClip("b", "Hips", 10f);

        var stateA = animator.Play(a);
        animator.CrossFade(b, 1f);
        int exits = 0;
        animator.OnFadeOut(stateA, () => exits++);

        Update(scene, frames: 30);                    // Mid-fade: not yet.
        Assert.Equal(0, exits);

        Update(scene, frames: 35);                    // Fade settles (float accumulation margin).
        Assert.Equal(1, exits);

        Update(scene, frames: 10);                    // Fires once only.
        Assert.Equal(1, exits);
    }
}
