// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies <see cref="AnimatorControllerCompiler"/>: the author-form controller (named, human-readable)
/// compiles into the flat runtime <see cref="AnimatorControllerConstant"/> with hashed names, integer
/// state indices, contiguous per-state transition slices, resolved condition parameter indices, and
/// resolved motion clips — and that the compiled constant then drives the Animator end-to-end.
/// </summary>
public class AnimatorControllerCompilerTests : RuntimeTestBase
{
    /// <summary>Builds the same Idle/Attack/trigger controller as the M3 tests, but in author form.</summary>
    private static AnimatorController BuildIdleAttackAuthor(out AnimationClip idleClip, out AnimationClip attackClip)
    {
        idleClip = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", posX: 0f, duration: 1f);
        attackClip = AnimationTestHelpers.MakeConstantPosClip("attack", "Hips", posX: 10f, duration: 1f);

        var author = new AnimatorController { Name = "Player" };
        author.DefaultStateName = "Idle";
        author.Parameters.Add(new AnimatorController.Parameter
        {
            Name = "Attack",
            Type = AnimatorParameterType.Trigger,
        });
        author.States.Add(new AnimatorController.State
        {
            Name = "Idle",
            Speed = 1f,
            IsLooping = true,
            Motion = new AssetRef<AnimationClip>(idleClip),
            TransitionNames = { "Idle_To_Attack" },
        });
        author.States.Add(new AnimatorController.State
        {
            Name = "Attack",
            Speed = 1f,
            IsLooping = false,
            Motion = new AssetRef<AnimationClip>(attackClip),
            TransitionNames = { "Attack_To_Idle" },
        });
        author.Transitions.Add(new AnimatorController.Transition
        {
            Name = "Idle_To_Attack",
            DestinationStateName = "Attack",
            Duration = 0f,
            Conditions = { new AnimatorController.Condition { Mode = ConditionMode.If, ParamName = "Attack" } },
        });
        author.Transitions.Add(new AnimatorController.Transition
        {
            Name = "Attack_To_Idle",
            DestinationStateName = "Idle",
            Duration = 0f,
            HasExitTime = true,
            ExitTime = 0.9f,
        });
        return author;
    }

    [Fact]
    public void Compile_HashesParameterNames()
    {
        var author = BuildIdleAttackAuthor(out _, out _);
        var c = AnimatorControllerCompiler.Compile(author);

        Assert.Single(c.Parameters);
        Assert.Equal(AnimationNameHash.Hash("Attack"), c.Parameters[0].NameHash);
        Assert.Equal(AnimatorParameterType.Trigger, c.Parameters[0].Type);
    }

    [Fact]
    public void Compile_AssignsStateIndicesAndResolvesClips()
    {
        var author = BuildIdleAttackAuthor(out var idle, out var attack);
        var c = AnimatorControllerCompiler.Compile(author);

        Assert.Equal(2, c.States.Length);
        Assert.Equal(AnimationNameHash.Hash("Idle"), c.States[0].NameHash);
        Assert.Equal(AnimationNameHash.Hash("Attack"), c.States[1].NameHash);
        Assert.Same(idle, c.MotionClips[0]);
        Assert.Same(attack, c.MotionClips[1]);
        Assert.Equal(0, c.DefaultStateIndex);
    }

    [Fact]
    public void Compile_FlattensTransitionsPerState()
    {
        var author = BuildIdleAttackAuthor(out _, out _);
        var c = AnimatorControllerCompiler.Compile(author);

        // Idle owns transition[0..1): Idle_To_Attack (dest = Attack = index 1).
        Assert.Equal(0, c.States[0].TransitionsStart);
        Assert.Equal(1, c.States[0].TransitionsCount);
        Assert.Equal(1, c.Transitions[c.States[0].TransitionsStart].DestinationStateIndex);

        // Attack owns transition[1..2): Attack_To_Idle (dest = Idle = index 0), with ExitTime.
        Assert.Equal(1, c.States[1].TransitionsStart);
        Assert.Equal(1, c.States[1].TransitionsCount);
        ref readonly TransitionDef back = ref c.Transitions[c.States[1].TransitionsStart];
        Assert.Equal(0, back.DestinationStateIndex);
        Assert.True(back.HasExitTime);
        Assert.Equal(0.9f, back.ExitTime);
    }

    [Fact]
    public void Compile_ResolvesConditionParameterIndices()
    {
        var author = BuildIdleAttackAuthor(out _, out _);
        var c = AnimatorControllerCompiler.Compile(author);

        // The Idle_To_Attack transition's "If Attack" condition → ParamIndex 0.
        ref readonly TransitionDef t = ref c.Transitions[0];
        Assert.Equal(1, t.ConditionsCount);
        Assert.Equal(0, c.Conditions[t.ConditionsStart].ParamIndex);
        Assert.Equal(ConditionMode.If, c.Conditions[t.ConditionsStart].Mode);
    }

    [Fact]
    public void CompiledConstant_DrivesAnimator_EndToEnd()
    {
        var author = BuildIdleAttackAuthor(out _, out _);
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);

        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        animator.SetControllerAsset(author);

        Update(scene, frames: 1); // enter Idle.
        Assert.Equal(0, animator.Runtime!.CurrentStateIndex);

        animator.SetTrigger("Attack");
        Update(scene, frames: 2); // → Attack (pos 10) + sample.
        Assert.Equal(1, animator.Runtime!.CurrentStateIndex);
        Assert.Equal(10f, root.Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void Compile_AnyStateTransitions()
    {
        var idleClip = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", posX: 0f, duration: 1f);
        var dieClip = AnimationTestHelpers.MakeConstantPosClip("die", "Hips", posX: 99f, duration: 1f);
        var author = new AnimatorController { Name = "c", DefaultStateName = "Idle" };
        author.Parameters.Add(new AnimatorController.Parameter { Name = "Die", Type = AnimatorParameterType.Trigger });
        author.States.Add(new AnimatorController.State { Name = "Idle", Speed = 1f, IsLooping = true, Motion = new AssetRef<AnimationClip>(idleClip) });
        author.States.Add(new AnimatorController.State { Name = "Die", Speed = 1f, IsLooping = false, Motion = new AssetRef<AnimationClip>(dieClip) });
        author.AnyStateTransitions.Add(new AnimatorController.Transition
        {
            Name = "Any_To_Die",
            DestinationStateName = "Die",
            Duration = 0f,
            Conditions = { new AnimatorController.Condition { Mode = ConditionMode.If, ParamName = "Die" } },
        });

        var c = AnimatorControllerCompiler.Compile(author);

        Assert.Equal(1, c.AnyStateTransitionsCount);
        Assert.Equal(1, c.Transitions[c.AnyStateTransitionsStart].DestinationStateIndex);
        Assert.Single(c.Layers);
        Assert.Equal(c.AnyStateTransitionsStart, c.Layers[0].AnyStateTransitionsStart);
        Assert.Equal(c.AnyStateTransitionsCount, c.Layers[0].AnyStateTransitionsCount);
    }

    [Fact]
    public void Compile_ExitDestination_ResolvesToDefaultState()
    {
        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f);
        var author = new AnimatorController { Name = "c", DefaultStateName = "Idle" };
        author.States.Add(new AnimatorController.State
        {
            Name = "Idle",
            Motion = new AssetRef<AnimationClip>(idle),
            TransitionNames = { "to_exit" },
        });
        author.Transitions.Add(new AnimatorController.Transition
        {
            Name = "to_exit",
            DestinationStateName = AnimatorController.ExitStateName,
            Duration = 0f,
            HasExitTime = true,
            ExitTime = 0.9f,
        });

        var c = AnimatorControllerCompiler.Compile(author);
        Assert.Equal(0, c.Transitions[0].DestinationStateIndex);
        Assert.True(c.Transitions[0].HasExitTime);
    }
}
