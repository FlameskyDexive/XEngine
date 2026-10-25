// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M18: <see cref="AnimatorController.Layers"/> compile into <see cref="LayerDef"/> slices, the
/// Animator builds one FSM runtime per layer, and Override+Mask blending matches the M5 mixer.
/// </summary>
public class AnimatorControllerLayerTests : RuntimeTestBase
{
    private static AnimationClip MakeTwoBoneClip(string name, string boneA, float aX, string boneB, float bX)
    {
        var clip = new global::XEngine.Runtime.AnimationClip { Name = name, Duration = 1f, Wrap = AnimationWrapMode.Loop };
        var a = new global::XEngine.Runtime.AnimationClip.AnimBone { BoneName = boneA };
        a.PosX = AnimationTestHelpers.ConstantCurve(aX);
        a.PosY = AnimationTestHelpers.ConstantCurve(0f);
        a.PosZ = AnimationTestHelpers.ConstantCurve(0f);
        clip.AddBone(a);
        var b = new global::XEngine.Runtime.AnimationClip.AnimBone { BoneName = boneB };
        b.PosX = AnimationTestHelpers.ConstantCurve(bX);
        b.PosY = AnimationTestHelpers.ConstantCurve(0f);
        b.PosZ = AnimationTestHelpers.ConstantCurve(0f);
        clip.AddBone(b);
        return clip;
    }

    private static AnimatorController BuildIdleAttackAuthor()
    {
        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", posX: 0f);
        var attack = AnimationTestHelpers.MakeConstantPosClip("attack", "Hips", posX: 10f);
        var author = new AnimatorController { Name = "Player", DefaultStateName = "Idle" };
        author.Parameters.Add(new AnimatorController.Parameter { Name = "Attack", Type = AnimatorParameterType.Trigger });
        author.States.Add(new AnimatorController.State
        {
            Name = "Idle", Speed = 1f, IsLooping = true,
            Motion = new AssetRef<AnimationClip>(idle),
            TransitionNames = { "Idle_To_Attack" },
        });
        author.States.Add(new AnimatorController.State
        {
            Name = "Attack", Speed = 1f, IsLooping = false,
            Motion = new AssetRef<AnimationClip>(attack),
        });
        author.Transitions.Add(new AnimatorController.Transition
        {
            Name = "Idle_To_Attack", DestinationStateName = "Attack", Duration = 0f,
            Conditions = { new AnimatorController.Condition { Mode = ConditionMode.If, ParamName = "Attack" } },
        });
        return author;
    }

    [Fact]
    public void Compile_EmptyLayers_EmitsBaseLayerSlice()
    {
        var c = AnimatorControllerCompiler.Compile(BuildIdleAttackAuthor());

        Assert.Equal(2, c.States.Length);
        Assert.Single(c.Layers);
        Assert.Equal(AnimationNameHash.Hash("Base Layer"), c.Layers[0].NameHash);
        Assert.Equal(1f, c.Layers[0].Weight);
        Assert.Equal(AnimatorLayerBlendingMode.Override, c.Layers[0].Blending);
        Assert.Equal(0, c.Layers[0].StatesStart);
        Assert.Equal(2, c.Layers[0].StatesCount);
        Assert.Equal(c.DefaultStateIndex, c.Layers[0].DefaultStateIndex);
    }

    [Fact]
    public void Compile_TwoLayers_SlicesStatesIndependently()
    {
        var baseClip = AnimationTestHelpers.MakeConstantPosClip("base", "Hips", 0f);
        var waveClip = AnimationTestHelpers.MakeConstantPosClip("wave", "Hips/Spine", 10f);
        var author = new AnimatorController { Name = "c" };
        author.Layers.Add(new AnimatorController.Layer
        {
            Name = "Base Layer",
            DefaultStateName = "Idle",
            States = { new AnimatorController.State { Name = "Idle", Motion = new AssetRef<AnimationClip>(baseClip) } },
        });
        author.Layers.Add(new AnimatorController.Layer
        {
            Name = "UpperBody",
            Weight = 1f,
            Blending = AnimatorLayerBlendingMode.Override,
            DefaultStateName = "Wave",
            States = { new AnimatorController.State { Name = "Wave", Motion = new AssetRef<AnimationClip>(waveClip) } },
        });

        var c = AnimatorControllerCompiler.Compile(author);

        Assert.Equal(2, c.Layers.Length);
        Assert.Equal(2, c.States.Length);
        Assert.Equal(0, c.Layers[0].StatesStart);
        Assert.Equal(1, c.Layers[0].StatesCount);
        Assert.Equal(1, c.Layers[1].StatesStart);
        Assert.Equal(1, c.Layers[1].StatesCount);
        Assert.Equal(AnimationNameHash.Hash("Idle"), c.States[0].NameHash);
        Assert.Equal(AnimationNameHash.Hash("Wave"), c.States[1].NameHash);
        Assert.Same(baseClip, c.MotionClips[0]);
        Assert.Same(waveClip, c.MotionClips[1]);
        Assert.Equal(0, c.Layers[0].DefaultStateIndex);
        Assert.Equal(1, c.Layers[1].DefaultStateIndex);
        Assert.Equal(AnimationNameHash.Hash("UpperBody"), c.Layers[1].NameHash);
    }

    [Fact]
    public void SetControllerAsset_TwoLayerOverrideMask_OverlayWinsMaskedBones()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var spine = CreateGameObject("Spine");
        spine.SetParent(hips);
        scene.Add(root);

        var baseClip = MakeTwoBoneClip("base", "Hips", 0f, "Hips/Spine", 5f);
        var overlayClip = AnimationTestHelpers.MakeConstantPosClip("ov", "Hips/Spine", posX: 10f);
        var mask = new AvatarMask();
        mask.Entries.Add(new AvatarMask.Entry { Path = "Hips/Spine", Weight = 1f });

        var author = new AnimatorController { Name = "layered" };
        author.Layers.Add(new AnimatorController.Layer
        {
            Name = "Base Layer",
            DefaultStateName = "Idle",
            States = { new AnimatorController.State { Name = "Idle", Motion = new AssetRef<AnimationClip>(baseClip) } },
        });
        author.Layers.Add(new AnimatorController.Layer
        {
            Name = "UpperBody",
            Weight = 1f,
            Blending = AnimatorLayerBlendingMode.Override,
            Mask = new AssetRef<AvatarMask>(mask),
            DefaultStateName = "Wave",
            States = { new AnimatorController.State { Name = "Wave", Motion = new AssetRef<AnimationClip>(overlayClip) } },
        });

        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        animator.SetControllerAsset(author);

        Assert.Equal(2, animator.LayerCount);
        Assert.Equal(1f, animator.GetLayerWeight(0));
        Assert.Equal(1f, animator.GetLayerWeight(1));

        Update(scene, frames: 2);

        Assert.Equal(0, animator.Runtime!.CurrentStateIndex);
        Assert.Equal(1, animator.GetLayerRuntime(1)!.CurrentStateIndex);
        Assert.Equal(0f, root.Children[0].Transform.LocalPosition.X, precision: 3);
        Assert.Equal(10f, root.Children[0].Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void SetLayerWeight_ZeroesOverlay()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var spine = CreateGameObject("Spine");
        spine.SetParent(hips);
        scene.Add(root);

        var baseClip = MakeTwoBoneClip("base", "Hips", 0f, "Hips/Spine", 5f);
        var overlayClip = AnimationTestHelpers.MakeConstantPosClip("ov", "Hips/Spine", posX: 10f);
        var mask = new AvatarMask();
        mask.Entries.Add(new AvatarMask.Entry { Path = "Hips/Spine", Weight = 1f });

        var author = new AnimatorController { Name = "layered" };
        author.Layers.Add(new AnimatorController.Layer
        {
            Name = "Base Layer",
            DefaultStateName = "Idle",
            States = { new AnimatorController.State { Name = "Idle", Motion = new AssetRef<AnimationClip>(baseClip) } },
        });
        author.Layers.Add(new AnimatorController.Layer
        {
            Name = "UpperBody",
            Weight = 1f,
            Mask = new AssetRef<AvatarMask>(mask),
            DefaultStateName = "Wave",
            States = { new AnimatorController.State { Name = "Wave", Motion = new AssetRef<AnimationClip>(overlayClip) } },
        });

        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        animator.SetControllerAsset(author);
        animator.SetLayerWeight(1, 0f);

        Update(scene, frames: 2);

        Assert.Equal(0f, animator.GetLayerWeight(1));
        Assert.Equal(5f, root.Children[0].Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void Play_String_TargetsLayer()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);

        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f);
        var attack = AnimationTestHelpers.MakeConstantPosClip("attack", "Hips", 10f);
        var author = new AnimatorController { Name = "c", DefaultStateName = "Idle" };
        author.States.Add(new AnimatorController.State { Name = "Idle", Motion = new AssetRef<AnimationClip>(idle) });
        author.States.Add(new AnimatorController.State { Name = "Attack", Motion = new AssetRef<AnimationClip>(attack) });

        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        animator.SetControllerAsset(author);
        Update(scene, frames: 1);
        Assert.Equal(0, animator.Runtime!.CurrentStateIndex);

        animator.Play("Attack");
        Update(scene, frames: 2);

        Assert.Equal(1, animator.Runtime!.CurrentStateIndex);
        Assert.Equal(10f, root.Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void GetInteger_ReturnsSetValue()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        scene.Add(root);

        var author = new AnimatorController { Name = "c", DefaultStateName = "Idle" };
        author.Parameters.Add(new AnimatorController.Parameter
        {
            Name = "Weapon",
            Type = AnimatorParameterType.Int,
            DefaultInt = 2,
        });
        author.States.Add(new AnimatorController.State
        {
            Name = "Idle",
            Motion = new AssetRef<AnimationClip>(AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f)),
        });

        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        animator.SetControllerAsset(author);

        Assert.Equal(2, animator.GetInteger("Weapon"));
        animator.SetInteger("Weapon", 7);
        Assert.Equal(7, animator.GetInteger("Weapon"));
    }

    [Fact]
    public void Play_Hash_AndHasState_AndStateInfo()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);

        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f, duration: 2f);
        var attack = AnimationTestHelpers.MakeConstantPosClip("attack", "Hips", 10f, duration: 1f);
        var author = new AnimatorController { Name = "c", DefaultStateName = "Idle" };
        author.States.Add(new AnimatorController.State { Name = "Idle", IsLooping = true, Motion = new AssetRef<AnimationClip>(idle) });
        author.States.Add(new AnimatorController.State { Name = "Attack", IsLooping = false, Motion = new AssetRef<AnimationClip>(attack) });

        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        animator.SetControllerAsset(author);
        Update(scene, frames: 1);

        Assert.True(animator.HasState("Idle"));
        Assert.True(animator.HasState("Attack"));
        Assert.False(animator.HasState("Missing"));
        Assert.True(animator.HasState(0, AnimationNameHash.Hash("Idle")));
        Assert.False(animator.HasState(1, AnimationNameHash.Hash("Idle")));

        AnimatorStateInfo idleInfo = animator.GetCurrentAnimatorStateInfo();
        Assert.True(idleInfo.IsName("Idle"));
        Assert.True(idleInfo.isLooping);
        Assert.Equal(2f, idleInfo.length, precision: 3);
        Assert.True(idleInfo.normalizedTime >= 0f);

        animator.Play(AnimationNameHash.Hash("Attack"), layer: 0);
        Update(scene, frames: 2);

        AnimatorStateInfo attackInfo = animator.GetCurrentAnimatorStateInfo(0);
        Assert.True(attackInfo.IsName("Attack"));
        Assert.False(attackInfo.isLooping);
        Assert.Equal(1, animator.Runtime!.CurrentStateIndex);
        Assert.Equal(10f, root.Children[0].Transform.LocalPosition.X, precision: 3);

        animator.CrossFade(AnimationNameHash.Hash("Idle"), 0.1f, layer: 0);
        Update(scene, frames: 1);
        Assert.Equal(0, animator.Runtime!.CurrentStateIndex);
    }
}
