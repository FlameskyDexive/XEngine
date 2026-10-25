// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>★ M44 — AnimatorOverrideController remaps clip and BlendTree motions without mutating authored trees.</summary>
public class AnimatorOverrideControllerTests : RuntimeTestBase
{
    [Fact]
    public void Compile_RemapsClipState_LeavesUnmappedClip()
    {
        AnimationClip idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", posX: 0f);
        AnimationClip walk = AnimationTestHelpers.MakeConstantPosClip("walk", "Hips", posX: 5f);
        AnimationClip idleB = AnimationTestHelpers.MakeConstantPosClip("idleB", "Hips", posX: 9f);

        var author = new AnimatorController { Name = "base", DefaultStateName = "Idle" };
        author.States.Add(new AnimatorController.State { Name = "Idle", Motion = new AssetRef<AnimationClip>(idle) });
        author.States.Add(new AnimatorController.State { Name = "Walk", Motion = new AssetRef<AnimationClip>(walk) });

        var ov = new AnimatorOverrideController { Name = "ov", Base = new AssetRef<AnimatorController>(author) };
        ov.Overrides.Add(new AnimatorOverrideController.ClipOverride
        {
            From = new AssetRef<AnimationClip>(idle),
            To = new AssetRef<AnimationClip>(idleB),
        });

        AnimatorControllerConstant c = AnimatorControllerCompiler.Compile(ov);
        Assert.Same(idleB, c.MotionClips[0]);
        Assert.Same(walk, c.MotionClips[1]);
        Assert.Same(idle, author.States[0].Motion.Res);
    }

    [Fact]
    public void Compile_ClonesBlendTree_DoesNotMutateAuthoredAsset()
    {
        AnimationClip a = AnimationTestHelpers.MakeConstantPosClip("a", "Hips", posX: 0f);
        AnimationClip b = AnimationTestHelpers.MakeConstantPosClip("b", "Hips", posX: 4f);
        AnimationClip a2 = AnimationTestHelpers.MakeConstantPosClip("a2", "Hips", posX: 8f);

        var tree = new BlendTree { Name = "loco", Type = BlendType.OneD, ParameterName = "Speed" };
        tree.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(a), Threshold = 0f });
        tree.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(b), Threshold = 1f });

        var author = new AnimatorController { Name = "base", DefaultStateName = "Loco" };
        author.Parameters.Add(new AnimatorController.Parameter { Name = "Speed", Type = AnimatorParameterType.Float });
        author.States.Add(new AnimatorController.State
        {
            Name = "Loco",
            BlendTree = new AssetRef<BlendTree>(tree),
        });

        var ov = new AnimatorOverrideController { Base = new AssetRef<AnimatorController>(author) };
        ov.Overrides.Add(new AnimatorOverrideController.ClipOverride
        {
            From = new AssetRef<AnimationClip>(a),
            To = new AssetRef<AnimationClip>(a2),
        });

        AnimatorControllerConstant c = AnimatorControllerCompiler.Compile(ov);
        BlendTree? compiled = c.MotionBlendTrees[0];
        Assert.NotNull(compiled);
        Assert.NotSame(tree, compiled);
        Assert.Same(a, tree.Children[0].Clip.Res);
        Assert.Same(a2, compiled!.Children[0].Clip.Res);
        Assert.Same(b, compiled.Children[1].Clip.Res);
    }

    [Fact]
    public void Runtime_PlaysOverriddenClip()
    {
        AnimationClip idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", posX: 0f);
        AnimationClip idleB = AnimationTestHelpers.MakeConstantPosClip("idleB", "Hips", posX: 7f);

        var author = new AnimatorController { Name = "base", DefaultStateName = "Idle" };
        author.States.Add(new AnimatorController.State
        {
            Name = "Idle",
            Speed = 1f,
            IsLooping = true,
            Motion = new AssetRef<AnimationClip>(idle),
        });

        var ov = new AnimatorOverrideController { Base = new AssetRef<AnimatorController>(author) };
        ov.Overrides.Add(new AnimatorOverrideController.ClipOverride
        {
            From = new AssetRef<AnimationClip>(idle),
            To = new AssetRef<AnimationClip>(idleB),
        });

        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        animator.Controller = new AssetRef<AnimatorController>(ov);
        animator.SetControllerAsset(ov);

        Update(scene, frames: 2);
        Assert.Equal(7f, hips.Transform.LocalPosition.X, precision: 2);
        Assert.Same(author, animator.AuthoringController);
    }

    [Fact]
    public void AvatarMask_HumanoidParts_LegacyEmptyMeansAllIncluded()
    {
        var mask = new AvatarMask();
        Assert.True(mask.GetHumanoidPart(AvatarMaskBodyPart.Head));
        mask.SetHumanoidPart(AvatarMaskBodyPart.Head, false);
        Assert.False(mask.GetHumanoidPart(AvatarMaskBodyPart.Head));
        Assert.True(mask.GetHumanoidPart(AvatarMaskBodyPart.Body));
        Assert.Equal(AvatarMask.HumanoidPartCount, mask.HumanoidParts.Count);
    }
}
