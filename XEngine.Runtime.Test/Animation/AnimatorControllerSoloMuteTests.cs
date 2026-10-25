// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>M33: multiple transitions, Solo/Mute filter, TransitionNames order is priority.</summary>
public class AnimatorControllerSoloMuteTests : RuntimeTestBase
{
    private static AnimatorController BuildIdleWalkRun(out AnimationClip idle, out AnimationClip walk, out AnimationClip run)
    {
        idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f, duration: 1f);
        walk = AnimationTestHelpers.MakeConstantPosClip("walk", "Hips", 1f, duration: 1f);
        run = AnimationTestHelpers.MakeConstantPosClip("run", "Hips", 2f, duration: 1f);
        var author = new AnimatorController { Name = "m33", DefaultStateName = "Idle" };
        author.Parameters.Add(new AnimatorController.Parameter { Name = "Go", Type = AnimatorParameterType.Trigger });
        author.States.Add(new AnimatorController.State
        {
            Name = "Idle", Motion = new AssetRef<AnimationClip>(idle), IsLooping = true,
            TransitionNames = { "ToWalk", "ToRun" },
        });
        author.States.Add(new AnimatorController.State { Name = "Walk", Motion = new AssetRef<AnimationClip>(walk) });
        author.States.Add(new AnimatorController.State { Name = "Run", Motion = new AssetRef<AnimationClip>(run) });
        author.Transitions.Add(new AnimatorController.Transition
        {
            Name = "ToWalk", DestinationStateName = "Walk", Duration = 0f,
            Conditions = { new AnimatorController.Condition { Mode = ConditionMode.If, ParamName = "Go" } },
        });
        author.Transitions.Add(new AnimatorController.Transition
        {
            Name = "ToRun", DestinationStateName = "Run", Duration = 0f,
            Conditions = { new AnimatorController.Condition { Mode = ConditionMode.If, ParamName = "Go" } },
        });
        return author;
    }

    [Fact]
    public void Compile_CopiesSoloAndMute()
    {
        var author = BuildIdleWalkRun(out _, out _, out _);
        author.Transitions[0].Mute = true;
        author.Transitions[1].Solo = true;
        var c = AnimatorControllerCompiler.Compile(author);
        Assert.True(c.Transitions[0].Mute);
        Assert.False(c.Transitions[0].Solo);
        Assert.True(c.Transitions[1].Solo);
        Assert.False(c.Transitions[1].Mute);
    }

    [Fact]
    public void Mute_SkipsTransition_NextInOrderFires()
    {
        var author = BuildIdleWalkRun(out _, out _, out _);
        author.Transitions[0].Mute = true;
        var c = AnimatorControllerCompiler.Compile(author);

        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        var runtime = animator.SetController(c);
        Update(scene, frames: 1);
        Assert.Equal(0, runtime.CurrentStateIndex);

        animator.SetTrigger("Go");
        Update(scene, frames: 1);
        Assert.Equal(AnimationNameHash.Hash("Run"), c.States[runtime.CurrentStateIndex].NameHash);
    }

    [Fact]
    public void Solo_IgnoresNonSoloEvenIfEarlier()
    {
        var author = BuildIdleWalkRun(out _, out _, out _);
        author.Transitions[1].Solo = true;
        var c = AnimatorControllerCompiler.Compile(author);

        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        var runtime = animator.SetController(c);
        Update(scene, frames: 1);
        animator.SetTrigger("Go");
        Update(scene, frames: 1);
        Assert.Equal(AnimationNameHash.Hash("Run"), c.States[runtime.CurrentStateIndex].NameHash);
    }

    [Fact]
    public void Order_FirstReadyWinsWithoutSolo()
    {
        var author = BuildIdleWalkRun(out _, out _, out _);
        var c = AnimatorControllerCompiler.Compile(author);
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        var runtime = animator.SetController(c);
        Update(scene, frames: 1);
        animator.SetTrigger("Go");
        Update(scene, frames: 1);
        Assert.Equal(AnimationNameHash.Hash("Walk"), c.States[runtime.CurrentStateIndex].NameHash);
    }
}
