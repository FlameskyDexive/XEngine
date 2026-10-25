// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies the M3 FSM runtime: parameter defaults, trigger-driven transitions, ExitTime
/// transitions, AnyState transitions, trigger consumption, and that transitions actually hand
/// control to the destination clip (verified through the Animator's mixer).
/// </summary>
public class AnimatorControllerRuntimeTests : RuntimeTestBase
{
    private const string IdleName = "Idle";
    private const string AttackName = "Attack";
    private const string DieName = "Die";
    private const string TriggerParam = "AttackTrigger";

    private (Scene scene, GameObject root, Animator animator, AnimatorControllerRuntime runtime)
        BuildAnimatorWithController(AnimatorControllerConstant constant)
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        var runtime = animator.SetController(constant);
        return (scene, root, animator, runtime);
    }

    /// <summary>Idle (clip pos 0, looping) → Attack (clip pos 10, non-looping) on the Attack trigger.</summary>
    private static AnimatorControllerConstant BuildIdleAttackController()
    {
        var idleClip = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", posX: 0f, duration: 1f);
        var attackClip = AnimationTestHelpers.MakeConstantPosClip("attack", "Hips", posX: 10f, duration: 1f);
        attackClip.Wrap = AnimationWrapMode.Once;

        return new AnimatorControllerConstant
        {
            Parameters = new[]
            {
                new ParameterDef { NameHash = AnimationNameHash.Hash(TriggerParam), Type = AnimatorParameterType.Trigger },
            },
            MotionClips = new[] { idleClip, attackClip },
            States = new[]
            {
                new StateDef
                {
                    NameHash = AnimationNameHash.Hash(IdleName), MotionIndex = 0, IsLooping = true, Speed = 1f,
                    TransitionsStart = 0, TransitionsCount = 1,
                },
                new StateDef
                {
                    NameHash = AnimationNameHash.Hash(AttackName), MotionIndex = 1, IsLooping = false, Speed = 1f,
                    TransitionsStart = 1, TransitionsCount = 1,
                },
            },
            Conditions = new[]
            {
                new ConditionDef { Mode = ConditionMode.If, ParamIndex = 0 }, // trigger set
            },
            Transitions = new[]
            {
                // Idle → Attack when trigger is set (snap).
                new TransitionDef { DestinationStateIndex = 1, Duration = 0f, ConditionsStart = 0, ConditionsCount = 1 },
                // Attack → Idle on ExitTime 0.9 (snap).
                new TransitionDef { DestinationStateIndex = 0, Duration = 0f, HasExitTime = true, ExitTime = 0.9f },
            },
            DefaultStateIndex = 0,
        };
    }

    [Fact]
    public void FirstEvaluate_EntersDefaultState()
    {
        var (scene, _, _, runtime) = BuildAnimatorWithController(BuildIdleAttackController());

        Update(scene, frames: 1);

        Assert.Equal(0, runtime.CurrentStateIndex); // Idle.
    }

    [Fact]
    public void TriggerTransition_FiresToDestination()
    {
        var (scene, _, animator, runtime) = BuildAnimatorWithController(BuildIdleAttackController());
        Update(scene, frames: 1); // enter Idle.

        animator.SetTrigger(TriggerParam);
        Update(scene, frames: 1); // transition fires this frame.

        Assert.Equal(1, runtime.CurrentStateIndex); // Attack.
    }

    [Fact]
    public void Trigger_IsConsumedAfterTransition()
    {
        var (scene, _, animator, runtime) = BuildAnimatorWithController(BuildIdleAttackController());
        Update(scene, frames: 1);

        animator.SetTrigger(TriggerParam);
        Update(scene, frames: 1); // Idle → Attack, trigger consumed.

        // The Attack state still has its (now-no-condition-needed) ExitTime transition; but the trigger
        // itself must be cleared so re-entering Idle doesn't immediately re-fire to Attack.
        Assert.False(runtime.GetTrigger(runtime.GetParameterIndex(AnimationNameHash.Hash(TriggerParam))));
    }

    [Fact]
    public void ExitTimeTransition_FiresWhenThresholdReached()
    {
        var (scene, _, animator, runtime) = BuildAnimatorWithController(BuildIdleAttackController());
        Update(scene, frames: 1); // Idle.

        animator.SetTrigger(TriggerParam);
        Update(scene, frames: 1); // → Attack (1s clip, ExitTime 0.9).

        // Advance well past 0.9 of the 1s Attack clip; the Attack→Idle ExitTime transition fires.
        Update(scene, frames: 60);

        Assert.Equal(0, runtime.CurrentStateIndex); // back to Idle.
    }

    [Fact]
    public void ZeroExitTimeTransition_WaitsForFirstCycleBoundary()
    {
        var hitClip = AnimationTestHelpers.MakeConstantPosClip("hit", "Hips", posX: 10f, duration: 0.7f);
        hitClip.Wrap = AnimationWrapMode.Once;
        var stayClip = AnimationTestHelpers.MakeConstantPosClip("stay", "Hips", posX: 20f, duration: 1f);
        var controller = new AnimatorControllerConstant
        {
            MotionClips = new[] { hitClip, stayClip },
            States = new[]
            {
                new StateDef
                {
                    NameHash = AnimationNameHash.Hash("Hit"), MotionIndex = 0, IsLooping = false,
                    Speed = 1f, TransitionsStart = 0, TransitionsCount = 1,
                },
                new StateDef { NameHash = AnimationNameHash.Hash("Stay"), MotionIndex = 1, Speed = 1f },
            },
            Transitions = new[]
            {
                new TransitionDef
                {
                    DestinationStateIndex = 1, HasExitTime = true, ExitTime = 0f,
                    HasFixedDuration = true,
                },
            },
            DefaultStateIndex = 0,
        };

        var (scene, _, _, runtime) = BuildAnimatorWithController(controller);
        Update(scene, frames: 1); // Enter Hit; exitTime=0 must not fire immediately.
        Assert.Equal(0, runtime.CurrentStateIndex);

        Update(scene, frames: 30); // 0.5 seconds, before the 0.7-second first cycle ends.
        Assert.Equal(0, runtime.CurrentStateIndex);

        Update(scene, frames: 20); // Cross the first cycle boundary.
        Assert.Equal(1, runtime.CurrentStateIndex);
    }

    [Fact]
    public void ExplicitPlay_DoesNotEvaluateDestinationExitTimeOnEntryFrame()
    {
        var hitClip = AnimationTestHelpers.MakeConstantPosClip("hit", "Hips", posX: 10f, duration: 0.7f);
        hitClip.Wrap = AnimationWrapMode.Once;
        var stayClip = AnimationTestHelpers.MakeConstantPosClip("stay", "Hips", posX: 20f, duration: 1f);
        var controller = new AnimatorControllerConstant
        {
            MotionClips = new[] { hitClip, stayClip },
            States = new[]
            {
                new StateDef
                {
                    NameHash = AnimationNameHash.Hash("Appear"), MotionIndex = 0, IsLooping = false,
                    Speed = 1f,
                },
                new StateDef { NameHash = AnimationNameHash.Hash("Stay"), MotionIndex = 1, Speed = 1f },
                new StateDef
                {
                    NameHash = AnimationNameHash.Hash("Hit"), MotionIndex = 0, IsLooping = false,
                    Speed = 1f, TransitionsStart = 0, TransitionsCount = 1,
                },
            },
            Transitions = new[]
            {
                new TransitionDef
                {
                    DestinationStateIndex = 1, HasExitTime = true, ExitTime = 0f,
                    Duration = 1f,
                },
            },
            DefaultStateIndex = 0,
        };

        var (scene, _, _, runtime) = BuildAnimatorWithController(controller);
        Update(scene, frames: 1); // Enter Appear and create the shared hit clip playable.
        Assert.Equal(0, runtime.CurrentStateIndex);

        Assert.True(runtime.Play(AnimationNameHash.Hash("Stay")), "explicit Play should resolve Stay");
        Update(scene, frames: 1);
        Assert.Equal(1, runtime.CurrentStateIndex);

        Assert.True(runtime.Play(AnimationNameHash.Hash("Hit")), "explicit Play should resolve Hit");
        Update(scene, frames: 1); // The entry frame samples Hit at time zero and must not fire its own exit.

        Assert.Equal(2, runtime.CurrentStateIndex);
        Assert.Equal(0f, runtime.CurrentNormalizedTime, precision: 5);
    }

    [Fact]
    public void NoTrigger_StaysInCurrentState()
    {
        var (scene, _, _, runtime) = BuildAnimatorWithController(BuildIdleAttackController());
        Update(scene, frames: 1); // Idle.

        Update(scene, frames: 30); // no trigger set → no transition.

        Assert.Equal(0, runtime.CurrentStateIndex); // still Idle.
    }

    [Fact]
    public void Transition_DrivesDestinationClipOntoSkeleton()
    {
        var (scene, root, animator, _) = BuildAnimatorWithController(BuildIdleAttackController());
        Update(scene, frames: 1); // Idle playing → Hips.PosX = 0.

        Assert.Equal(0f, root.Children[0].Transform.LocalPosition.X, precision: 3);

        animator.SetTrigger(TriggerParam);
        Update(scene, frames: 2); // → Attack (pos 10) + one frame to sample.

        Assert.Equal(10f, root.Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void AnyStateTransition_FiresFromAnyCurrentState()
    {
        // A Die state reachable from anywhere via the Die trigger.
        var idleClip = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", posX: 0f, duration: 1f);
        var dieClip = AnimationTestHelpers.MakeConstantPosClip("die", "Hips", posX: 99f, duration: 1f);
        var c = new AnimatorControllerConstant
        {
            Parameters = new[]
            {
                new ParameterDef { NameHash = AnimationNameHash.Hash("Die"), Type = AnimatorParameterType.Trigger },
            },
            MotionClips = new[] { idleClip, dieClip },
            States = new[]
            {
                new StateDef { NameHash = AnimationNameHash.Hash(IdleName), MotionIndex = 0, IsLooping = true },
                new StateDef { NameHash = AnimationNameHash.Hash(DieName), MotionIndex = 1, IsLooping = false },
            },
            Transitions = new[]
            {
                // AnyState → Die on the Die trigger.
                new TransitionDef { DestinationStateIndex = 1, Duration = 0f, ConditionsStart = 0, ConditionsCount = 1 },
            },
            Conditions = new[]
            {
                new ConditionDef { Mode = ConditionMode.If, ParamIndex = 0 },
            },
            AnyStateTransitionsStart = 0,
            AnyStateTransitionsCount = 1,
            DefaultStateIndex = 0,
        };

        var (scene, _, animator, runtime) = BuildAnimatorWithController(c);
        Update(scene, frames: 1); // enter Idle.

        animator.SetTrigger("Die");
        Update(scene, frames: 1);

        Assert.Equal(1, runtime.CurrentStateIndex); // Die, reached from Idle via AnyState.
    }

    [Fact]
    public void FloatCondition_GreaterThan_FiresAtThreshold()
    {
        var idleClip = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", posX: 0f, duration: 1f);
        var runClip = AnimationTestHelpers.MakeConstantPosClip("run", "Hips", posX: 5f, duration: 1f);
        var c = new AnimatorControllerConstant
        {
            Parameters = new[]
            {
                new ParameterDef { NameHash = AnimationNameHash.Hash("Speed"), Type = AnimatorParameterType.Float },
            },
            MotionClips = new[] { idleClip, runClip },
            States = new[]
            {
                new StateDef { NameHash = AnimationNameHash.Hash(IdleName), MotionIndex = 0, IsLooping = true, TransitionsStart = 0, TransitionsCount = 1 },
                new StateDef { NameHash = AnimationNameHash.Hash("Run"), MotionIndex = 1, IsLooping = true },
            },
            Transitions = new[]
            {
                new TransitionDef { DestinationStateIndex = 1, Duration = 0f, ConditionsStart = 0, ConditionsCount = 1 },
            },
            Conditions = new[]
            {
                new ConditionDef { Mode = ConditionMode.Greater, ParamIndex = 0, Threshold = 0.5f },
            },
            DefaultStateIndex = 0,
        };

        var (scene, _, animator, runtime) = BuildAnimatorWithController(c);
        Update(scene, frames: 1); // Idle.

        animator.SetFloat("Speed", 0.3f);
        Update(scene, frames: 1);
        Assert.Equal(0, runtime.CurrentStateIndex); // below threshold, stays Idle.

        animator.SetFloat("Speed", 0.7f);
        Update(scene, frames: 1);
        Assert.Equal(1, runtime.CurrentStateIndex); // above threshold, → Run.
    }

    [Fact]
    public void ZeroDurationClip_AdvancesStateClockAndFiresExitTime()
    {
        var marker = AnimationTestHelpers.MakeConstantPosClip("marker", "Hips", posX: 3f, duration: 0f);
        var destination = AnimationTestHelpers.MakeConstantPosClip("destination", "Hips", posX: 8f, duration: 1f);
        var controller = new AnimatorControllerConstant
        {
            MotionClips = new[] { marker, destination },
            States = new[]
            {
                new StateDef
                {
                    NameHash = AnimationNameHash.Hash("Marker"), MotionIndex = 0,
                    IsLooping = true, Speed = 1f, TransitionsStart = 0, TransitionsCount = 1,
                },
                new StateDef { NameHash = AnimationNameHash.Hash("Destination"), MotionIndex = 1, Speed = 1f },
            },
            Transitions = new[]
            {
                new TransitionDef
                {
                    DestinationStateIndex = 1, HasExitTime = true, ExitTime = 1f,
                    HasFixedDuration = true,
                },
            },
            DefaultStateIndex = 0,
        };

        var (scene, root, _, runtime) = BuildAnimatorWithController(controller);
        Update(scene, frames: 1);
        Assert.Equal(0, runtime.CurrentStateIndex);
        Assert.Equal(3f, root.Children[0].Transform.LocalPosition.X, precision: 3);
        Assert.InRange(runtime.CurrentNormalizedTime, 0f, 0.1f);

        Update(scene, frames: 50);
        Assert.Equal(0, runtime.CurrentStateIndex);
        Assert.InRange(runtime.CurrentNormalizedTime, 0.7f, 1f);

        Update(scene, frames: 20);
        Assert.Equal(1, runtime.CurrentStateIndex);
        Assert.Equal(8f, root.Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void ZeroDurationClip_PreservesNormalizedStartOffset()
    {
        var marker = AnimationTestHelpers.MakeConstantPosClip("marker", "Hips", posX: 3f, duration: 0f);
        var destination = AnimationTestHelpers.MakeConstantPosClip("destination", "Hips", posX: 8f, duration: 1f);
        var controller = new AnimatorControllerConstant
        {
            MotionClips = new[] { marker, destination },
            States = new[]
            {
                new StateDef { NameHash = AnimationNameHash.Hash("Marker"), MotionIndex = 0, Speed = 1f },
                new StateDef { NameHash = AnimationNameHash.Hash("Destination"), MotionIndex = 1, Speed = 1f },
            },
            DefaultStateIndex = 0,
        };
        var (scene, _, animator, runtime) = BuildAnimatorWithController(controller);
        Update(scene, frames: 1);

        Assert.True(runtime.Play(AnimationNameHash.Hash("Marker"), fadeDuration: 0f));
        // Direct Play starts at zero; a zero-duration playable must still expose a real clock.
        Assert.Equal(0f, runtime.CurrentNormalizedTime, precision: 4);
        Update(scene, frames: 16); // The entry frame is held at zero; 15 subsequent steps reach 0.25.
        var state = animator.GetState(marker);
        Assert.True(state != null, $"state missing current={runtime.CurrentStateIndex}");
        Assert.True(MathF.Abs(runtime.CurrentNormalizedTime - 0.25f) < 0.0001f,
            $"duration={state!.Duration} time={state.Time} normalized={state.NormalizedTime} state={state.State} inputs={animator.Layer?.InputCount}");
    }

    [Fact]
    public void ZeroDurationClip_ExitTimeBoundaryWaitsForNextEvaluation()
    {
        var marker = AnimationTestHelpers.MakeConstantPosClip("marker", "Hips", posX: 3f, duration: 0f);
        var destination = AnimationTestHelpers.MakeConstantPosClip("destination", "Hips", posX: 8f, duration: 1f);
        var controller = new AnimatorControllerConstant
        {
            MotionClips = new[] { marker, destination },
            States = new[]
            {
                new StateDef
                {
                    NameHash = AnimationNameHash.Hash("Marker"), MotionIndex = 0,
                    IsLooping = true, Speed = 1f, TransitionsStart = 0, TransitionsCount = 1,
                },
                new StateDef { NameHash = AnimationNameHash.Hash("Destination"), MotionIndex = 1, Speed = 1f },
            },
            Transitions = new[]
            {
                new TransitionDef
                {
                    DestinationStateIndex = 1, HasExitTime = true, ExitTime = 1f,
                    HasFixedDuration = true,
                },
            },
            DefaultStateIndex = 0,
        };

        var (scene, _, _, runtime) = BuildAnimatorWithController(controller);
        Update(scene, frames: 1); // Enter marker at time zero, matching Unity's entry-frame sample.
        Update(scene, frames: 60); // The playable reaches exactly normalized time 1.0.
        Assert.Equal(0, runtime.CurrentStateIndex);
        Assert.InRange(runtime.CurrentNormalizedTime, 0.999f, 1.001f);

        Update(scene, frames: 1); // The following evaluation sees the crossed marker boundary.
        Assert.Equal(1, runtime.CurrentStateIndex);
    }

    [Fact]
    public void OnceClip_ExitTimeBoundaryTransitionsWhenPendingClockReachesEnd()
    {
        var source = AnimationTestHelpers.MakeConstantPosClip("source", "Hips", posX: 3f, duration: 1f);
        var destination = AnimationTestHelpers.MakeConstantPosClip("destination", "Hips", posX: 8f, duration: 1f);
        var controller = new AnimatorControllerConstant
        {
            MotionClips = new[] { source, destination },
            States = new[]
            {
                new StateDef
                {
                    NameHash = AnimationNameHash.Hash("Source"), MotionIndex = 0,
                    IsLooping = false, Speed = 1f, TransitionsStart = 0, TransitionsCount = 1,
                },
                new StateDef { NameHash = AnimationNameHash.Hash("Destination"), MotionIndex = 1, Speed = 1f },
            },
            Transitions = new[]
            {
                new TransitionDef
                {
                    DestinationStateIndex = 1, HasExitTime = true, ExitTime = 1f,
                    HasFixedDuration = true,
                },
            },
            DefaultStateIndex = 0,
        };

        var (scene, _, _, runtime) = BuildAnimatorWithController(controller);
        Update(scene, frames: 1); // Enter source at time zero.
        Update(scene, frames: 60); // The pending graph clock reaches the final sample.
        Assert.Equal(1, runtime.CurrentStateIndex);
    }

    [Fact]
    public void ExitTimeTransition_CarriesRemainderIntoDestination()
    {
        var source = AnimationTestHelpers.MakeConstantPosClip("source", "Hips", posX: 3f, duration: 2f);
        var destination = AnimationTestHelpers.MakeConstantPosClip("destination", "Hips", posX: 8f, duration: 1f);
        var controller = new AnimatorControllerConstant
        {
            MotionClips = new[] { source, destination },
            States = new[]
            {
                new StateDef
                {
                    NameHash = AnimationNameHash.Hash("Source"), MotionIndex = 0,
                    IsLooping = false, Speed = 1f, TransitionsStart = 0, TransitionsCount = 1,
                },
                new StateDef { NameHash = AnimationNameHash.Hash("Destination"), MotionIndex = 1, Speed = 1f },
            },
            Transitions = new[]
            {
                new TransitionDef
                {
                    DestinationStateIndex = 1, HasExitTime = true, ExitTime = 1f,
                    HasFixedDuration = true,
                },
            },
            DefaultStateIndex = 0,
        };

        var (scene, _, animator, runtime) = BuildAnimatorWithController(controller);
        Update(scene, frames: 1); // Source starts at a finite float clock.
        Update(scene, frames: 120); // The pending clock crosses 2 s and carries its remainder.
        Assert.Equal(1, runtime.CurrentStateIndex);
        var state = animator.GetState(destination);
        Assert.NotNull(state);
        Assert.InRange(state!.Time, 0.0166d, 0.0167d);
    }
}
