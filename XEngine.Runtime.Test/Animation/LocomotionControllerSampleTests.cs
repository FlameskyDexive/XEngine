// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M16 sample: a full character-locomotion controller (the BananaMan-style use case) built and
/// verified end-to-end. Exercises BlendTree (Idle/Walk/Run on Speed) + a triggered Jump state that
/// returns on ExitTime — the canonical state-machine layout for an animated character. No on-disk
/// model is required (the BananaMan sample ships without one), so clips are synthetic; the controller
/// structure and runtime behaviour are what's verified.
/// </summary>
public class LocomotionControllerSampleTests : RuntimeTestBase
{
    private static AnimatorController BuildLocomotionController()
    {
        // Synthetic locomotion clips (a real character would import these from its model).
        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", posX: 0f);
        var walk = AnimationTestHelpers.MakeConstantPosClip("walk", "Hips", posX: 2f);
        var run = AnimationTestHelpers.MakeConstantPosClip("run", "Hips", posX: 5f);
        var jump = AnimationTestHelpers.MakeConstantPosClip("jump", "Hips", posX: 8f);
        jump.Wrap = AnimationWrapMode.Once;

        var locomotion = new BlendTree { Name = "Locomotion", Type = BlendType.OneD, ParameterName = "Speed" };
        locomotion.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(idle), Threshold = 0f });
        locomotion.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(walk), Threshold = 0.5f });
        locomotion.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(run), Threshold = 1f });

        var c = new AnimatorController { Name = "Locomotion", DefaultStateName = "Locomotion" };
        c.Parameters.Add(new AnimatorController.Parameter { Name = "Speed", Type = AnimatorParameterType.Float });
        c.Parameters.Add(new AnimatorController.Parameter { Name = "Jump", Type = AnimatorParameterType.Trigger });

        c.States.Add(new AnimatorController.State
        {
            Name = "Locomotion", Speed = 1f, IsLooping = true,
            BlendTree = new AssetRef<BlendTree>(locomotion),
            TransitionNames = { "ToJump" },
        });
        c.States.Add(new AnimatorController.State
        {
            Name = "Jump", Speed = 1f, IsLooping = false,
            Motion = new AssetRef<AnimationClip>(jump),
            TransitionNames = { "JumpToLoco" },
        });

        c.Transitions.Add(new AnimatorController.Transition
        {
            Name = "ToJump", DestinationStateName = "Jump", Duration = 0.1f,
            Conditions = { new AnimatorController.Condition { Mode = ConditionMode.If, ParamName = "Jump" } },
        });
        c.Transitions.Add(new AnimatorController.Transition
        {
            Name = "JumpToLoco", DestinationStateName = "Locomotion", Duration = 0.1f,
            HasExitTime = true, ExitTime = 0.9f,
        });
        return c;
    }

    private (Scene scene, GameObject root, Animator animator) BuildAnimator()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Character");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        animator.SetControllerAsset(BuildLocomotionController());
        return (scene, root, animator);
    }

    [Fact]
    public void Locomotion_SpeedDrivesIdleWalkRunBlend()
    {
        var (scene, root, animator) = BuildAnimator();
        Update(scene, frames: 2); // enter Locomotion at Speed=0 → idle.

        Assert.Equal(0f, root.Children[0].Transform.LocalPosition.X, precision: 3);

        animator.SetFloat("Speed", 0.5f); // → walk (pos 2).
        Update(scene, frames: 1);
        Assert.Equal(2f, root.Children[0].Transform.LocalPosition.X, precision: 3);

        animator.SetFloat("Speed", 1f); // → run (pos 5).
        Update(scene, frames: 1);
        Assert.Equal(5f, root.Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void JumpTrigger_JumpsAndReturnsToLocomotion()
    {
        var (scene, root, animator) = BuildAnimator();
        Update(scene, frames: 2); // Locomotion.

        animator.SetTrigger("Jump");
        Update(scene, frames: 8); // → Jump; let the 0.1s crossfade finish (pos 8).
        Assert.Equal(1, animator.Runtime!.CurrentStateIndex);
        Assert.Equal(8f, root.Children[0].Transform.LocalPosition.X, precision: 3);

        // Advance past the Jump clip's ExitTime (0.9 of 1s) → back to Locomotion.
        Update(scene, frames: 55);
        Assert.Equal(0, animator.Runtime!.CurrentStateIndex); // Locomotion.
    }

    [Fact]
    public void Locomotion_CompilesToValidConstant()
    {
        // The authored controller compiles to a well-formed runtime constant (the importer's job).
        var constant = AnimatorControllerCompiler.Compile(BuildLocomotionController());

        Assert.Equal(2, constant.States.Length);
        Assert.Equal(1, constant.MotionKind[0]); // Locomotion is a BlendTree state.
        Assert.Equal(0, constant.MotionKind[1]); // Jump is a clip state.
        Assert.Equal(0, constant.DefaultStateIndex);
        // Locomotion has one outbound transition (ToJump); Jump has one (JumpToLoco).
        Assert.Equal(1, constant.States[0].TransitionsCount);
        Assert.Equal(1, constant.States[1].TransitionsCount);
    }
}
