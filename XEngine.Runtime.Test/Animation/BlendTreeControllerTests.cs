// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies the M8 BlendTree path end-to-end through the FSM: a controller state whose motion is a
/// BlendTree compiles, the runtime builds a BlendTreePlayable for it, and changing the controller's
/// blend parameter re-blends the child clips onto the skeleton.
/// </summary>
public class BlendTreeControllerTests : RuntimeTestBase
{
    private (Scene scene, GameObject root, Animator animator) BuildWithBlendTreeController()
    {
        var idleClip = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", posX: 0f);
        var walkClip = AnimationTestHelpers.MakeConstantPosClip("walk", "Hips", posX: 5f);
        var runClip = AnimationTestHelpers.MakeConstantPosClip("run", "Hips", posX: 10f);

        var bt = new BlendTree { Name = "loco", Type = BlendType.OneD, ParameterName = "Speed" };
        bt.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(idleClip), Threshold = 0f });
        bt.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(walkClip), Threshold = 0.5f });
        bt.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(runClip), Threshold = 1f });

        var author = new AnimatorController { Name = "c", DefaultStateName = "Loco" };
        author.Parameters.Add(new AnimatorController.Parameter { Name = "Speed", Type = AnimatorParameterType.Float });
        author.States.Add(new AnimatorController.State
        {
            Name = "Loco",
            Speed = 1f,
            IsLooping = true,
            BlendTree = new AssetRef<BlendTree>(bt),
        });

        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        animator.SetControllerAsset(author);
        return (scene, root, animator);
    }

    [Fact]
    public void BlendTreeState_DefaultParameter_PicksFirstChild()
    {
        var (scene, root, _) = BuildWithBlendTreeController();

        Update(scene, frames: 2); // Speed defaults to 0 → idle (pos 0).

        Assert.Equal(0f, root.Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void BlendTreeState_ParameterChange_Reblends()
    {
        var (scene, root, animator) = BuildWithBlendTreeController();
        Update(scene, frames: 2); // enter Locomotion at Speed=0.

        animator.SetFloat("Speed", 0.5f); // → walk (pos 5).
        Update(scene, frames: 1);

        Assert.Equal(5f, root.Children[0].Transform.LocalPosition.X, precision: 3);

        animator.SetFloat("Speed", 1f); // → run (pos 10).
        Update(scene, frames: 1);

        Assert.Equal(10f, root.Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void BlendTreeState_MidpointParameter_LerpsChildren()
    {
        var (scene, root, animator) = BuildWithBlendTreeController();
        Update(scene, frames: 2);

        animator.SetFloat("Speed", 0.25f); // halfway idle(0)↔walk(5) → 2.5.
        Update(scene, frames: 1);

        Assert.Equal(2.5f, root.Children[0].Transform.LocalPosition.X, precision: 3);
    }
}
