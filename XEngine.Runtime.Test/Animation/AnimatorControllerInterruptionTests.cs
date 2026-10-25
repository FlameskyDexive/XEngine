// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>M34: HasFixedDuration, InterruptionSource, OrderedInterruption, CanTransitionToSelf.</summary>
public class AnimatorControllerInterruptionTests : RuntimeTestBase
{
    [Fact]
    public void ResolveFadeDuration_FixedVsNormalized()
    {
        var fixedT = new TransitionDef { Duration = 0.5f, HasFixedDuration = true };
        var normT = new TransitionDef { Duration = 0.5f, HasFixedDuration = false };
        Assert.Equal(0.5f, AnimatorControllerRuntime.ResolveFadeDuration(in fixedT, 2f));
        Assert.Equal(1f, AnimatorControllerRuntime.ResolveFadeDuration(in normT, 2f));
    }

    [Fact]
    public void Compile_CopiesInterruptionFields()
    {
        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f, duration: 1f);
        var walk = AnimationTestHelpers.MakeConstantPosClip("walk", "Hips", 1f, duration: 1f);
        var author = new AnimatorController { DefaultStateName = "Idle" };
        author.States.Add(new AnimatorController.State
        {
            Name = "Idle", Motion = new AssetRef<AnimationClip>(idle), TransitionNames = { "T" }
        });
        author.States.Add(new AnimatorController.State { Name = "Walk", Motion = new AssetRef<AnimationClip>(walk) });
        author.Transitions.Add(new AnimatorController.Transition
        {
            Name = "T", DestinationStateName = "Walk", Duration = 0.2f,
            HasFixedDuration = false,
            InterruptionSource = TransitionInterruptionSource.CurrentState,
            OrderedInterruption = false,
            CanTransitionToSelf = true,
        });
        var c = AnimatorControllerCompiler.Compile(author);
        Assert.False(c.Transitions[0].HasFixedDuration);
        Assert.Equal(TransitionInterruptionSource.CurrentState, c.Transitions[0].InterruptionSource);
        Assert.False(c.Transitions[0].OrderedInterruption);
        Assert.True(c.Transitions[0].CanTransitionToSelf);
    }

    [Fact]
    public void InterruptionNone_BlocksNewTransitionWhileFading()
    {
        var (scene, animator, runtime, c) = BuildIdleWalkRun(
            TransitionInterruptionSource.None, fade: 0.4f);
        Update(scene, frames: 1);
        animator.SetTrigger("ToWalk");
        Update(scene, frames: 1);
        Assert.Equal(AnimationNameHash.Hash("Walk"), c.States[runtime.CurrentStateIndex].NameHash);
        Assert.True(runtime.IsInTransition);

        animator.SetTrigger("ToRun");
        Update(scene, frames: 1);
        Assert.Equal(AnimationNameHash.Hash("Walk"), c.States[runtime.CurrentStateIndex].NameHash);
    }

    [Fact]
    public void InterruptionCurrentState_AllowsSourceTransitionWhileFading()
    {
        var (scene, animator, runtime, c) = BuildIdleWalkRun(
            TransitionInterruptionSource.CurrentState, fade: 0.4f);
        Update(scene, frames: 1);
        animator.SetTrigger("ToWalk");
        Update(scene, frames: 1);
        Assert.True(runtime.IsInTransition);

        animator.SetTrigger("ToRun");
        Update(scene, frames: 1);
        Assert.Equal(AnimationNameHash.Hash("Run"), c.States[runtime.CurrentStateIndex].NameHash);
    }

    [Fact]
    public void AnyState_CanTransitionToSelfFalse_SkipsCurrent()
    {
        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f, duration: 1f);
        var author = new AnimatorController { DefaultStateName = "Idle" };
        author.Parameters.Add(new AnimatorController.Parameter { Name = "Pulse", Type = AnimatorParameterType.Trigger });
        author.States.Add(new AnimatorController.State { Name = "Idle", Motion = new AssetRef<AnimationClip>(idle) });
        author.AnyStateTransitions.Add(new AnimatorController.Transition
        {
            Name = "AnyIdle", DestinationStateName = "Idle", Duration = 0f,
            CanTransitionToSelf = false,
            Conditions = { new AnimatorController.Condition { Mode = ConditionMode.If, ParamName = "Pulse" } },
        });
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
        int before = runtime.CurrentStateIndex;
        animator.SetTrigger("Pulse");
        Update(scene, frames: 1);
        Assert.Equal(before, runtime.CurrentStateIndex);
        Assert.True(runtime.GetTrigger(runtime.GetParameterIndex(AnimationNameHash.Hash("Pulse"))));
    }

    private (Scene scene, Animator animator, AnimatorControllerRuntime runtime, AnimatorControllerConstant c)
        BuildIdleWalkRun(TransitionInterruptionSource interrupt, float fade)
    {
        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f, duration: 2f);
        var walk = AnimationTestHelpers.MakeConstantPosClip("walk", "Hips", 1f, duration: 1f);
        var run = AnimationTestHelpers.MakeConstantPosClip("run", "Hips", 2f, duration: 1f);
        var author = new AnimatorController { DefaultStateName = "Idle" };
        author.Parameters.Add(new AnimatorController.Parameter { Name = "ToWalk", Type = AnimatorParameterType.Trigger });
        author.Parameters.Add(new AnimatorController.Parameter { Name = "ToRun", Type = AnimatorParameterType.Trigger });
        author.States.Add(new AnimatorController.State
        {
            Name = "Idle", Motion = new AssetRef<AnimationClip>(idle), IsLooping = true,
            TransitionNames = { "IdleWalk", "IdleRun" },
        });
        author.States.Add(new AnimatorController.State { Name = "Walk", Motion = new AssetRef<AnimationClip>(walk) });
        author.States.Add(new AnimatorController.State { Name = "Run", Motion = new AssetRef<AnimationClip>(run) });
        author.Transitions.Add(new AnimatorController.Transition
        {
            Name = "IdleWalk", DestinationStateName = "Walk", Duration = fade, HasFixedDuration = true,
            InterruptionSource = interrupt, OrderedInterruption = false,
            Conditions = { new AnimatorController.Condition { Mode = ConditionMode.If, ParamName = "ToWalk" } },
        });
        author.Transitions.Add(new AnimatorController.Transition
        {
            Name = "IdleRun", DestinationStateName = "Run", Duration = 0f, HasFixedDuration = true,
            Conditions = { new AnimatorController.Condition { Mode = ConditionMode.If, ParamName = "ToRun" } },
        });
        var c = AnimatorControllerCompiler.Compile(author);
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        var runtime = animator.SetController(c);
        return (scene, animator, runtime, c);
    }
}
