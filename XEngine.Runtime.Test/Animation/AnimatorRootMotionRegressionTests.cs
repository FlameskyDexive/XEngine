// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Playables;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

public sealed class AnimatorRootMotionRegressionTests : RuntimeTestBase
{
    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void GroundMarker_WithVisualAxisCorrection_UsesTrueSmallStepAtEveryFrameRate(int fps)
    {
        var (animator, carrier, _) = BuildMarkerRig();
        animator.Play(MarkerClip("Step", -4.5f, -4.7f));
        Tick(animator, 1f / fps, fps);
        Near(new Float3(0f, 0f, 0.2f), animator.Transform.Position);
        Near(new Float3(0f, 1f, 0f), carrier.LocalPosition);
    }

    [Fact]
    public void GroundMarker_ConstantRecoveryOffset_DoesNotMoveBodyOrLeaveVisualTravel()
    {
        var (animator, carrier, marker) = BuildMarkerRig();
        animator.Play(MarkerClip("Recovery", -9f, -9f));
        Tick(animator, 0.1f, 5);
        Near(Float3.Zero, animator.Transform.Position);
        Near(new Float3(0f, 1f, 0f), carrier.LocalPosition);
        Near(Float3.Zero, marker.LocalPosition);
    }

    [Fact]
    public void ExplicitGroundMarker_StillPinsItsSiblingSkinCarrier()
    {
        var (animator, carrier, _) = BuildMarkerRig();
        AnimationClip clip = MarkerClip("Explicit", -4f, -6f);
        clip.RootBonePath = "Model/Root";
        animator.Play(clip);
        Tick(animator, 0.5f);
        Near(new Float3(0f, 0f, 1f), animator.Transform.Position);
        Near(new Float3(0f, 1f, 0f), carrier.LocalPosition);
    }

    [Fact]
    public void WithoutMarker_SmallSkinCarrierStepIsExtractedWithoutTravelThreshold()
    {
        var (animator, carrier, _) = BuildMarkerRig();
        AnimationClip clip = MarkerClip("NoMarker", -4f, -4.2f);
        clip.Bones.RemoveAt(2);
        animator.Play(clip);
        Tick(animator, 0.5f);
        Near(new Float3(0f, 0f, 0.1f), animator.Transform.Position);
        Near(new Float3(0f, 1f, 0f), carrier.LocalPosition);
    }

    [Fact]
    public void DefaultFullRootMotion_ConvertsRotationThroughModelParentBasis()
    {
        var (animator, carrier, _) = BuildMarkerRig();
        animator.RootMotionPlanar = false;
        animator.ApplyRootMotionRotation = true;
        // Source X rotations become body -X through BattleVisual's half-turn about Y.
        AnimationClip clip = MarkerClip("Turn", 0f, 0f);
        clip.RootBonePath = "Model/Bip001";
        SetRotation(clip.Bones[1], Quaternion.Identity, Quaternion.FromEuler(90f, 0f, 0f));
        animator.Play(clip);
        Tick(animator, 0.1f, 10);
        SameRotation(Quaternion.FromEuler(-90f, 0f, 0f), animator.Transform.Rotation);
        SameRotation(Quaternion.Identity, carrier.LocalRotation);
    }

    [Fact]
    public void DefaultRootRotation_LoopCrossingPreservesAccumulatedTurn()
    {
        var (animator, _, _) = BuildMarkerRig();
        animator.ApplyRootMotionRotation = true;
        AnimationClip clip = MarkerClip("TurnLoop", 0f, 0f);
        SetRotation(clip.Bones[2], Quaternion.Identity, Quaternion.FromEuler(0f, 90f, 0f));
        AnimationClipPlayable state = animator.Play(clip);
        state.Wrap = AnimationWrapMode.Loop;
        Tick(animator, 0.25f, 8);
        SameRotation(Quaternion.FromEuler(0f, 180f, 0f), animator.Transform.Rotation);
    }

    [Fact]
    public void NonCommutingRootTurns_ComposeLocalIncrements()
    {
        var (animator, root) = BuildSingleRootRig();
        Quaternion x = Quaternion.FromEuler(90f, 0f, 0f);
        Quaternion y = Quaternion.FromEuler(0f, 90f, 0f);
        animator.Play(TurningClip(Quaternion.Identity, x, y));
        Tick(animator, 0.5f, 2);
        SameRotation(y, animator.Transform.Rotation);
        SameRotation(Quaternion.Identity, root.LocalRotation);
    }

    [Fact]
    public void RootTurn_WithNonIdentityClipOrigin_DiscardsOnlyTheInitialConstantRotation()
    {
        var (animator, _) = BuildSingleRootRig();
        Quaternion origin = Quaternion.FromEuler(90f, 0f, 0f);
        Quaternion middle = Quaternion.FromEuler(0f, 45f, 0f) * origin;
        Quaternion end = Quaternion.FromEuler(0f, 0f, 90f) * origin;
        animator.Play(TurningClip(origin, middle, end));
        Tick(animator, 0.5f, 2);
        SameRotation(end * Quaternion.Inverse(origin), animator.Transform.Rotation);
    }

    [Theory]
    [InlineData(0.5f, 3)]
    [InlineData(1.5f, 1)]
    public void NonCommutingLoopTurns_ComposeTailWholeCyclesAndHead(float dt, int frames)
    {
        var (animator, _) = BuildSingleRootRig();
        Quaternion x = Quaternion.FromEuler(90f, 0f, 0f);
        Quaternion y = Quaternion.FromEuler(0f, 90f, 0f);
        animator.Play(TurningClip(Quaternion.Identity, x, y)).Wrap = AnimationWrapMode.Loop;
        Tick(animator, dt, frames);
        SameRotation(y * x, animator.Transform.Rotation);
    }

    [Fact]
    public void NonCommutingReverseLoop_ComposesTheInverseCycle()
    {
        var (animator, _) = BuildSingleRootRig();
        Quaternion x = Quaternion.FromEuler(90f, 0f, 0f);
        Quaternion y = Quaternion.FromEuler(0f, 90f, 0f);
        AnimationClipPlayable state = animator.Play(TurningClip(Quaternion.Identity, x, y));
        state.Wrap = AnimationWrapMode.Loop;
        state.SetTime(0.5d);
        state.Speed = -1f;
        Tick(animator, 1f);
        SameRotation(Quaternion.Inverse(x) * Quaternion.Inverse(y) * x, animator.Transform.Rotation);
    }

    [Theory]
    [InlineData(false, 0.5f, 2)]
    [InlineData(true, 0.5f, 4)]
    [InlineData(true, 2f, 1)]
    public void TurningRootTrajectory_DoesNotRotateAuthoredDisplacementTwice(bool loop, float dt, int frames)
    {
        var (animator, _) = BuildSingleRootRig();
        Quaternion turn = Quaternion.FromEuler(0f, 90f, 0f);
        AnimationClip clip = TurningClip(Quaternion.Identity, turn, turn);
        clip.Bones[0].PosX = ThreeKeyCurve(0f, 0f, 1f);
        clip.Bones[0].PosZ = ThreeKeyCurve(0f, 1f, 1f);
        animator.Play(clip).Wrap = loop ? AnimationWrapMode.Loop : AnimationWrapMode.Once;
        Tick(animator, dt, frames);
        Near(loop ? new Float3(2f, 0f, 0f) : new Float3(1f, 0f, 1f), animator.Transform.Position);
        SameRotation(loop ? turn * turn : turn, animator.Transform.Rotation);
    }

    [Fact]
    public void MarkerForwardTravel_PreservesIndependentHorizontalVisualSway()
    {
        var (animator, carrier, _) = BuildMarkerRig();
        AnimationClip clip = MarkerClip("Sway", 0f, -2f);
        clip.Bones[1].PosX = AnimationTestHelpers.LinearCurve(0f, 0.2f, 1f);
        animator.Play(clip);
        Tick(animator, 0.5f);
        Near(new Float3(0f, 0f, 1f), animator.Transform.Position);
        Near(new Float3(0.1f, 1f, 0f), carrier.LocalPosition);
    }

    [Fact]
    public void DiagonalMarkerTravel_PreservesHorizontalPoseOrthogonalToTheTrajectory()
    {
        var (animator, carrier, _) = BuildMarkerRig();
        AnimationClip clip = MarkerClip("DiagonalSway", 0f, 2f);
        clip.Bones[2].PosX = AnimationTestHelpers.LinearCurve(0f, 2f, 1f);
        clip.Bones[1].PosX = AnimationTestHelpers.LinearCurve(0f, 2.2f, 1f);
        clip.Bones[1].PosZ = AnimationTestHelpers.LinearCurve(0f, 1.8f, 1f);
        animator.Play(clip);
        Tick(animator, 0.5f);
        Near(new Float3(-1f, 0f, -1f), animator.Transform.Position);
        Near(new Float3(0.1f, 1f, -0.1f), carrier.LocalPosition);
    }

    [Fact]
    public void MarkerTravelBetweenEqualHermiteEndpoints_StillPinsTheTravelDirection()
    {
        var (animator, carrier, _) = BuildMarkerRig();
        AnimationClip clip = MarkerClip("CurvedStep", 0f, 0f);
        var curve = new AnimationCurve(new[] { new KeyFrame(0f, 0f, 0f, 2f), new KeyFrame(1f, 0f, -2f, 0f) });
        curve.Keys[0].TangentOut = 2f;
        curve.Keys[1].TangentIn = -2f;
        clip.Bones[1].PosX = curve;
        clip.Bones[2].PosX = curve;
        animator.Play(clip);
        Tick(animator, 0.5f);
        Near(new Float3(-0.5f, 0f, 0f), animator.Transform.Position);
        Near(new Float3(0f, 1f, 0f), carrier.LocalPosition);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void UnknownSkinRoot_DoesNotPromoteWeaponAnimationToBodyMotion(bool unresolvedSkin, bool hasRootCurve)
    {
        var (animator, root) = BuildSingleRootRig();
        GameObject weapon = CreateGameObject("Weapon");
        weapon.SetParent(root.GameObject, worldPositionStays: false);
        if (unresolvedSkin)
            weapon.AddComponent<SkinnedMeshRenderer>().RootBonePath = "Missing/Root";
        animator.RebuildGraph();
        var clip = new AnimationClip { Name = "UnresolvedSkin", Duration = 1f };
        if (hasRootCurve)
            clip.AddBone(PositionBone("Root", Float3.Zero, Float3.Zero));
        clip.AddBone(PositionBone("Root/Weapon", Float3.Zero, new Float3(8f, 0f, 0f)));
        animator.Play(clip);
        Tick(animator, 0.5f);
        Near(Float3.Zero, animator.Transform.Position);
        Near(new Float3(4f, 0f, 0f), weapon.Transform.LocalPosition);
    }

    [Fact]
    public void GroundMarker_OverridesDifferentVisualMotion_AndNeverExtractsWeaponMovement()
    {
        var (animator, carrier, _) = BuildMarkerRig();
        GameObject weapon = CreateGameObject("Weapon");
        weapon.SetParent(carrier.GameObject, worldPositionStays: false);
        animator.RebuildGraph();
        AnimationClip clip = MarkerClip("Attack", 0f, -2f);
        clip.Bones[1].PosZ = AnimationTestHelpers.LinearCurve(0f, -7f, 1f);
        clip.AddBone(PositionBone("Model/Bip001/Weapon", Float3.Zero, new Float3(10f, 0f, 0f)));
        animator.Play(clip);
        Tick(animator, 0.5f);
        Near(new Float3(0f, 0f, 1f), animator.Transform.Position);
        Near(new Float3(0f, 1f, 0f), carrier.LocalPosition);
        Near(new Float3(5f, 0f, 0f), weapon.Transform.LocalPosition);
    }

    [Fact]
    public void PlanarMode_PreservesVerticalJumpAndBoneSpin_WhileHandlerReceivesOnlyHorizontalDelta()
    {
        var (animator, carrier, _) = BuildMarkerRig();
        AnimationClip clip = MarkerClip("JumpSpin", 0f, -2f);
        clip.Bones[1].PosY = AnimationTestHelpers.LinearCurve(1f, 4f, 1f);
        SetRotation(clip.Bones[1], Quaternion.Identity, Quaternion.FromEuler(0f, 120f, 0f));
        animator.Play(clip);
        Float3 received = default;
        Quaternion receivedRotation = default;
        animator.OnAnimatorMove = (delta, rotation) => { received += delta; receivedRotation = rotation; };
        Tick(animator, 0.5f);
        Near(new Float3(0f, 0f, 1f), received);
        Near(Float3.Zero, animator.Transform.Position);
        Assert.Equal(2.5f, carrier.LocalPosition.Y, 4);
        Assert.True(MathF.Abs(carrier.LocalRotation.Y) > 0.4f);
        SameRotation(Quaternion.Identity, receivedRotation);
    }

    [Fact]
    public void RetargetedParentAndActorScale_ProduceBodySpaceDeltaAndCorrectWorldMotion()
    {
        var (animator, carrier, _) = BuildMarkerRig();
        Transform model = carrier.Parent!;
        model.LocalRotation = Quaternion.FromEuler(0f, 90f, 0f);
        model.LocalScale = new Float3(2f, 2f, 2f);
        animator.RebuildGraphAndRecaptureReferencePose();
        animator.Transform.Rotation = Quaternion.FromEuler(0f, 90f, 0f);
        animator.Transform.LocalScale = new Float3(0.5f, 0.5f, 0.5f);
        animator.Play(MarkerClip("RetargetedStep", 0f, -2f));
        Float3 received = default;
        animator.OnAnimatorMove = (delta, _) =>
        {
            received += delta;
            animator.Transform.Position += animator.Transform.TransformVector(delta);
        };
        Tick(animator, 0.5f);
        Near(new Float3(0f, 0f, 2f), received);
        Near(new Float3(1f, 0f, 0f), animator.Transform.Position);
        Near(new Float3(0f, 1f, 0f), carrier.LocalPosition);
    }

    [Fact]
    public void CrossFade_BlendsVelocityWithoutDifferencingUnrelatedAbsoluteClipOrigins()
    {
        var (animator, _, _) = BuildMarkerRig();
        animator.Play(MarkerClip("First", 0f, -2f));
        Tick(animator, 0.1f, 4);
        float before = animator.Transform.Position.Z;
        AnimationClipPlayable second = animator.CrossFade(MarkerClip("Second", -50f, -54f), 0.4f);
        Tick(animator, 0.1f);
        // First contributes 0.75 * 0.2; second 0.25 * 0.4. Neither absolute origin participates.
        Assert.Equal(0.25f, animator.Transform.Position.Z - before, 4);
        Assert.Equal(0.25f, second.Weight, 4);
        Tick(animator, 0.1f, 3);
        Assert.InRange(animator.Transform.Position.Z, 2.0f, 2.2f);
    }

    [Fact]
    public void CrossFade_ToStationaryRecovery_NeverPullsBodyBack()
    {
        var (animator, carrier, _) = BuildMarkerRig();
        animator.Play(MarkerClip("Attack", 0f, -6f));
        Tick(animator, 0.1f, 10);
        float before = animator.Transform.Position.Z;
        animator.CrossFade(MarkerClip("Recovery", -30f, -30f), 0.4f);
        for (int i = 0; i < 5; i++)
        {
            Tick(animator, 0.1f);
            Assert.Equal(before, animator.Transform.Position.Z, 4);
            Near(new Float3(0f, 1f, 0f), carrier.LocalPosition);
        }
    }

    [Theory]
    [InlineData(0.9f, 0.3f, 1f, 0.6f)]
    [InlineData(0.9f, 2.3f, 1f, 4.6f)]
    [InlineData(0.1f, 2.3f, -1f, -4.6f)]
    public void LoopWrap_AccumulatesTailHeadAndWholeCycles(float start, float dt, float speed, float expected)
    {
        var (animator, _, _) = BuildMarkerRig();
        AnimationClipPlayable state = animator.Play(MarkerClip("Loop", -20f, -22f));
        state.Wrap = AnimationWrapMode.Loop;
        state.Speed = speed;
        state.SetTime(start);
        Tick(animator, dt);
        Assert.Equal(expected, animator.Transform.Position.Z, 4);
    }

    [Fact]
    public void PauseSeekReplayAndEnable_DoNotApplyHistoricalPosition()
    {
        var (animator, _, _) = BuildMarkerRig();
        AnimationClip clip = MarkerClip("Step", -4f, -6f);
        AnimationClipPlayable state = animator.Play(clip);
        Tick(animator, 0.1f);
        state.Pause();
        state.SetTime(0.7d);
        Tick(animator, 0.1f);
        Assert.Equal(0.2f, animator.Transform.Position.Z, 4);
        state.Play();
        Tick(animator, 0.1f);
        Assert.Equal(0.4f, animator.Transform.Position.Z, 4);
        animator.Play(clip);
        Tick(animator, 0.1f);
        Assert.Equal(0.6f, animator.Transform.Position.Z, 4);
        animator.Enabled = false;
        animator.Enabled = true;
        animator.Play(clip);
        Tick(animator, 0.1f);
        Assert.Equal(0.8f, animator.Transform.Position.Z, 4);
    }

    [Fact]
    public void RootMotionToggle_DoesNotCatchUpPreviouslyDiscardedTravel()
    {
        var (animator, _, _) = BuildMarkerRig();
        animator.Play(MarkerClip("Step", 0f, -2f));
        animator.ApplyRootMotion = false;
        Tick(animator, 0.1f, 5);
        animator.ApplyRootMotion = true;
        Tick(animator, 0.1f);
        Assert.Equal(0.2f, animator.Transform.Position.Z, 4);
    }

    [Fact]
    public void MaximumDeltaTime_CapsLivePlayheadAndRootMotionTogetherWithoutLosingClipTravel()
    {
        var (animator, _, _) = BuildMarkerRig();
        animator.MaximumDeltaTime = 1f / 30f;
        AnimationClipPlayable state = animator.Play(MarkerClip("Delayed", 0f, -2f));
        Tick(animator, 0.2f);
        Assert.Equal(1f / 30f, state.NormalizedTime, 5);
        Assert.Equal(2f / 30f, animator.Transform.Position.Z, 5);
        Tick(animator, 0.2f, 29);
        Assert.Equal(1f, state.NormalizedTime, 5);
        Assert.Equal(2f, animator.Transform.Position.Z, 4);
    }

    [Fact]
    public void MaximumDeltaTime_CapsCrossFadeWeightsAndDestinationTimeByTheSameStep()
    {
        var (animator, _, _) = BuildMarkerRig();
        animator.MaximumDeltaTime = 1f / 30f;
        animator.Play(MarkerClip("First", 0f, -2f));
        Tick(animator, 0.2f);
        AnimationClipPlayable destination = animator.CrossFade(MarkerClip("Second", -50f, -54f), 0.2f);
        Tick(animator, 0.2f);
        Assert.Equal(1f / 6f, destination.Weight, 5);
        Assert.Equal(1f / 30f, destination.NormalizedTime, 5);
    }

    [Fact]
    public void MaximumDeltaTime_ControllerExitTimeFollowsCappedGraphTime()
    {
        var (animator, _, _) = BuildMarkerRig();
        animator.MaximumDeltaTime = 0.1f;
        AnimatorControllerRuntime runtime = animator.SetController(new AnimatorControllerConstant
        {
            MotionClips = new[] { MarkerClip("Attack", 0f, -2f), MarkerClip("Recovery", -2f, -2f) },
            States = new[]
            {
                new StateDef { NameHash = 1, MotionIndex = 0, Speed = 1f, TransitionsStart = 0, TransitionsCount = 1 },
                new StateDef { NameHash = 2, MotionIndex = 1, Speed = 1f },
            },
            Transitions = new[] { new TransitionDef { DestinationStateIndex = 1, HasExitTime = true, ExitTime = 0.5f } },
            DefaultStateIndex = 0,
        });
        Tick(animator, 0.4f, 3);
        Assert.Equal(0, runtime.CurrentStateIndex);
        Assert.Equal(0.3f, runtime.CurrentNormalizedTime, 5);
        Tick(animator, 0.4f, 3);
        Assert.Equal(1, runtime.CurrentStateIndex);
        Assert.Equal(0.1f, runtime.CurrentNormalizedTime, 5);
    }

    [Fact]
    public void MaximumDeltaTime_LeavesExplicitInspectorSamplingUnclamped()
    {
        var (animator, _, _) = BuildMarkerRig();
        animator.MaximumDeltaTime = 1f / 30f;
        AnimationClipPlayable state = animator.Play(MarkerClip("Preview", 0f, -2f));
        animator.EvaluateGraph(0.5f);
        Assert.Equal(0.5f, state.NormalizedTime, 5);
        Near(Float3.Zero, animator.Transform.Position);
    }

    [Fact]
    public void StationaryClip_StillInvokesManualHandlerEveryFrame()
    {
        var (animator, _, _) = BuildMarkerRig();
        animator.Play(MarkerClip("Hold", -4f, -4f));
        int calls = 0;
        animator.OnAnimatorMove = (delta, rotation) =>
        {
            calls++;
            Near(Float3.Zero, delta);
            SameRotation(Quaternion.Identity, rotation);
        };
        Tick(animator, 0.1f, 3);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void InspectorEvaluation_PinsVisualTravelWithoutMovingBodyOrCallingGameplay()
    {
        var (animator, carrier, _) = BuildMarkerRig();
        animator.Play(MarkerClip("Preview", -20f, -22f));
        int calls = 0;
        animator.OnAnimatorMove = (_, _) => calls++;
        animator.EvaluateGraph(0.5f);
        Near(new Float3(0f, 1f, 0f), carrier.LocalPosition);
        Near(Float3.Zero, animator.Transform.Position);
        Assert.Equal(0, calls);
        Tick(animator, 0.1f);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void NestedBlendTree_UsesEvaluatedParameterWeights()
    {
        var (animator, _, _) = BuildMarkerRig();
        var tree = new BlendTreePlayable { ParameterValue = 0.25f };
        tree.Bind(animator.Skeleton!);
        var slow = new AnimationClipPlayable(MarkerClip("Slow", 0f, -2f));
        var fast = new AnimationClipPlayable(MarkerClip("Fast", -9f, -15f));
        slow.Bind(animator.Skeleton!);
        fast.Bind(animator.Skeleton!);
        slow.Play();
        fast.Play();
        tree.AddChild(slow, 0f);
        tree.AddChild(fast, 1f);
        animator.PlayPlayable(tree);
        Tick(animator, 0.1f);
        Assert.Equal(0.3f, animator.Transform.Position.Z, 4);
    }

    [Fact]
    public void StableRootMotionEvaluation_AllocatesZeroBytes()
    {
        var (animator, _, _) = BuildMarkerRig();
        animator.Play(MarkerClip("Loop", 0f, -2f)).Wrap = AnimationWrapMode.Loop;
        Time.TimeStack.Push(new TimeData { DeltaTime = 1f / 60f });
        try
        {
            for (int i = 0; i < 2048; i++) animator.LateUpdate();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) animator.LateUpdate();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }
        finally { Time.TimeStack.Pop(); }
    }

    private (Animator animator, Transform carrier, Transform marker) BuildMarkerRig()
    {
        Scene scene = CreateScene(enable: true);
        GameObject actor = CreateGameObject("Actor");
        GameObject visual = CreateGameObject("BattleVisual");
        visual.SetParent(actor, worldPositionStays: false);
        visual.Transform.LocalRotation = Quaternion.FromEuler(0f, 180f, 0f);
        GameObject model = CreateGameObject("Model");
        model.SetParent(visual, worldPositionStays: false);
        GameObject carrier = CreateGameObject("Bip001");
        carrier.SetParent(model, worldPositionStays: false);
        carrier.Transform.LocalPosition = new Float3(0f, 1f, 0f);
        GameObject marker = CreateGameObject("Root");
        marker.SetParent(model, worldPositionStays: false);
        carrier.AddComponent<SkinnedMeshRenderer>().RootBonePath = "Model/Bip001";
        scene.Add(actor);
        var animator = new Animator
        {
            PlayAutomatically = false,
            ApplyRootMotion = true,
            RootMotionPlanar = true,
            ApplyRootMotionRotation = false,
            Wrap = AnimationWrapMode.Once,
            SkeletonRoot = visual.Transform,
        };
        actor.AddComponent(animator);
        return (animator, carrier.Transform, marker.Transform);
    }

    private (Animator animator, Transform root) BuildSingleRootRig()
    {
        Scene scene = CreateScene(enable: true);
        GameObject actor = CreateGameObject("Actor");
        GameObject root = CreateGameObject("Root");
        root.SetParent(actor, worldPositionStays: false);
        scene.Add(actor);
        var animator = new Animator { PlayAutomatically = false, ApplyRootMotion = true, Wrap = AnimationWrapMode.Once };
        actor.AddComponent(animator);
        return (animator, root.Transform);
    }

    private static AnimationClip TurningClip(Quaternion start, Quaternion middle, Quaternion end)
    {
        var clip = new AnimationClip { Name = "Turn", Duration = 1f, RootBonePath = "Root" };
        AnimationClip.AnimBone bone = PositionBone("Root", Float3.Zero, Float3.Zero);
        bone.RotX = ThreeKeyCurve(start.X, middle.X, end.X);
        bone.RotY = ThreeKeyCurve(start.Y, middle.Y, end.Y);
        bone.RotZ = ThreeKeyCurve(start.Z, middle.Z, end.Z);
        bone.RotW = ThreeKeyCurve(start.W, middle.W, end.W);
        clip.AddBone(bone);
        return clip;
    }

    private static AnimationCurve ThreeKeyCurve(float start, float middle, float end)
    {
        var curve = new AnimationCurve(new[] { new KeyFrame(0f, start), new KeyFrame(0.5f, middle), new KeyFrame(1f, end) });
        curve.SmoothTangents(CurveTangent.Linear);
        return curve;
    }

    private static AnimationClip MarkerClip(string name, float start, float end)
    {
        var clip = new AnimationClip { Name = name, Duration = 1f };
        clip.AddBone(PositionBone("Model", Float3.Zero, Float3.Zero));
        var carrier = PositionBone("Model/Bip001", new Float3(0f, 1f, start), new Float3(0f, 1f, end));
        carrier.SourcePosition = new Float3(0f, 1f, 0f);
        clip.AddBone(carrier);
        var marker = PositionBone("Model/Root", new Float3(0f, 0f, start), new Float3(0f, 0f, end));
        marker.SourcePosition = new Float3(0f, 0f, start); // Ground marker references can contain a prior action offset.
        clip.AddBone(marker);
        return clip;
    }

    private static AnimationClip.AnimBone PositionBone(string path, Float3 start, Float3 end)
        => new()
        {
            BoneName = path,
            HasSourceReferencePose = true,
            SourceRotation = Quaternion.Identity,
            SourceScale = Float3.One,
            PosX = AnimationTestHelpers.LinearCurve(start.X, end.X, 1f),
            PosY = AnimationTestHelpers.LinearCurve(start.Y, end.Y, 1f),
            PosZ = AnimationTestHelpers.LinearCurve(start.Z, end.Z, 1f),
        };

    private static void SetRotation(AnimationClip.AnimBone bone, Quaternion from, Quaternion to)
    {
        bone.RotX = AnimationTestHelpers.LinearCurve(from.X, to.X, 1f);
        bone.RotY = AnimationTestHelpers.LinearCurve(from.Y, to.Y, 1f);
        bone.RotZ = AnimationTestHelpers.LinearCurve(from.Z, to.Z, 1f);
        bone.RotW = AnimationTestHelpers.LinearCurve(from.W, to.W, 1f);
    }

    private static void Tick(Animator animator, float dt, int count = 1)
    {
        Time.TimeStack.Push(new TimeData { DeltaTime = dt });
        try { for (int i = 0; i < count; i++) animator.LateUpdate(); }
        finally { Time.TimeStack.Pop(); }
    }

    private static void Near(Float3 expected, Float3 actual)
        => Assert.True(Float3.Length(expected - actual) < 2e-4f, $"expected {expected}, actual {actual}");

    private static void SameRotation(Quaternion expected, Quaternion actual)
        => Assert.True(MathF.Abs(Quaternion.Dot(expected, actual)) > 0.9999f);
}
