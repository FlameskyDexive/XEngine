// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Regression coverage for in-place clips whose shallow rig carrier contains translation.
/// Travel suppression must follow every contributing state through a crossfade and remove only
/// the large source-space travel component after retargeting, leaving authored bob/sway intact.
/// </summary>
public sealed class AnimatorTravelPinRegressionTests : RuntimeTestBase
{
    [Fact]
    public void VisualSkeletonOrigin_RotationDoesNotAlterBoneReferenceOrGameplayHeading()
    {
        var (animator, travel) = BuildSkinnedRig(Quaternion.Identity);
        Float3 reference = travel.LocalPosition;
        GameObject model = travel.GameObject.Parent!;
        GameObject visual = CreateGameObject("Visual");
        visual.SetParent(animator.GameObject);
        model.SetParent(visual);
        visual.Transform.LocalRotation = Quaternion.AxisAngle(Float3.UnitY, MathF.PI);
        animator.SkeletonRoot = visual.Transform;
        animator.RebuildGraph();
        animator.Play(MakeTravelClip("Run", new Float3(0f, 0f, 6f)));
        animator.EvaluateGraph(0.25f);
        AssertPosition(reference, travel.LocalPosition, "unchanged local reference");
        AssertPosition(Float3.UnitZ, animator.Transform.Forward, "gameplay heading");
        AssertPosition(-Float3.UnitZ, visual.Transform.Forward, "visual model axis");
        Assert.True(animator.Skeleton!.IndexOfBonePath("ModelRoot/Bip001") >= 0);
    }

    [Fact]
    public void ConstantRecoveryTravel_IsPinnedDuringPlaybackAndFadeOut()
    {
        var (animator, travel) = BuildSkinnedRig(Quaternion.Identity);
        Float3 reference = travel.LocalPosition;
        AnimationClip recovery = MakeTravelClip("Recovery", Float3.Zero);
        recovery.Bones[1].PosZ = AnimationTestHelpers.ConstantCurve(6f);
        animator.Play(recovery);
        animator.EvaluateGraph(0.25f);
        AssertPosition(reference, travel.LocalPosition, "constant recovery offset");
        animator.CrossFade(MakeTravelClip("Idle", Float3.Zero), 0.5f);
        for (int i = 0; i < 30; i++)
        {
            animator.EvaluateGraph(1f / 60f);
            AssertPosition(reference, travel.LocalPosition, "recovery fade");
        }
    }

    [Fact]
    public void AttachmentTravel_BelowCommonSkinRoot_IsNotPinned()
    {
        var (animator, travel) = BuildSkinnedRig(Quaternion.Identity);
        GameObject weapon = CreateGameObject("Weapon");
        weapon.SetParent(travel.GameObject);
        weapon.AddComponent<SkinnedMeshRenderer>().RootBonePath = "ModelRoot/Bip001/Weapon";
        animator.RebuildGraph();
        AnimationClip clip = MakeTravelClip("WeaponSwing", Float3.Zero);
        clip.AddBone(new AnimationClip.AnimBone
        {
            BoneName = "ModelRoot/Bip001/Weapon",
            PosX = AnimationTestHelpers.LinearCurve(2f, 8f, 1f),
            PosY = AnimationTestHelpers.ConstantCurve(0f),
            PosZ = AnimationTestHelpers.ConstantCurve(0f),
        });
        animator.Play(clip);
        animator.EvaluateGraph(0.5f);
        Assert.Equal(5f, weapon.Transform.LocalPosition.X, precision: 4);
    }

    [Fact]
    public void CrossFade_RunToIdle_PinsTravelBoneForEntireFadeOutWindow()
    {
        var (animator, travel) = BuildSkinnedRig(Quaternion.Identity);
        Float3 bindPosition = travel.LocalPosition;
        AnimationClip run = MakeTravelClip(
            "Run",
            sourceEndPosition: new Float3(0.2f, 0.1f, 6f));
        AnimationClip idle = MakeTravelClip(
            "Idle",
            sourceEndPosition: Float3.Zero);

        AnimationClipPlayable runState = animator.Play(run);
        animator.EvaluateGraph(0.25f);
        AssertPosition(
            bindPosition + new Float3(0.05f, 0.025f, 0f),
            travel.LocalPosition,
            "run before crossfade");

        const float fadeDuration = 0.5f;
        AnimationClipPlayable idleState = animator.CrossFade(idle, fadeDuration);
        const int fadeFrames = 30;
        for (int frame = 1; frame <= fadeFrames; frame++)
        {
            animator.EvaluateGraph(1f / 60f);

            float normalizedRunWeight = runState.Weight / (runState.Weight + idleState.Weight);
            Float3 expectedBob = new(
                0.2f * (float)runState.Time * normalizedRunWeight,
                0.1f * (float)runState.Time * normalizedRunWeight,
                0f);
            AssertPosition(
                bindPosition + expectedBob,
                travel.LocalPosition,
                $"fade frame {frame}/{fadeFrames}, runWeight={runState.Weight}, idleWeight={idleState.Weight}");
        }

        Assert.Equal(0f, runState.Weight, precision: 4);
        Assert.Equal(1f, idleState.Weight, precision: 4);
    }

    [Fact]
    public void RetargetedTravelPin_RemovesMappedSourceZTravel_ButPreservesSmallSourceXYMotion()
    {
        Quaternion targetParentReference = Quaternion.AxisAngle(Float3.UnitY, MathF.PI * 0.5f);
        var (animator, travel) = BuildSkinnedRig(targetParentReference);
        Float3 bindPosition = travel.LocalPosition;

        // Z is deliberate locomotion travel. X/Y remain below the travel threshold and represent
        // authored sway/bob that must survive the in-place pin after the parent-basis retarget.
        Float3 sourceEndPosition = new(0.2f, 0.1f, 6f);
        AnimationClip run = MakeTravelClip("RetargetedRun", sourceEndPosition);

        animator.Play(run);
        animator.EvaluateGraph(0.5f);

        Float3 sampledSmallMotion = new(sourceEndPosition.X * 0.5f, sourceEndPosition.Y * 0.5f, 0f);
        Float3 expected = bindPosition + Quaternion.Inverse(targetParentReference) * sampledSmallMotion;
        Float3 actual = travel.LocalPosition;

        // Source +Z maps onto destination -X under this reference basis and must be removed.
        Assert.Equal(bindPosition.X, actual.X, precision: 4);
        // Source Y bob remains destination Y, while source X sway maps onto destination Z.
        Assert.Equal(expected.Y, actual.Y, precision: 4);
        Assert.Equal(expected.Z, actual.Z, precision: 4);
        AssertPosition(expected, actual, "retargeted travel pin result");
    }

    [Fact]
    public void DirectRootTravelCarrier_RemovesMappedSourceZTravel_ButPreservesSmallSourceXYMotion()
    {
        Quaternion targetParentReference = Quaternion.AxisAngle(Float3.UnitY, MathF.PI * 0.5f);
        var (animator, travel) = BuildSkinnedRig(targetParentReference);
        Float3 bindPosition = travel.LocalPosition;
        Float3 sourceEndPosition = new(0.2f, 0.1f, 6f);
        AnimationClip run = MakeTravelClip(
            "DirectRootRetargetedRun",
            sourceEndPosition,
            rootBonePath: "ModelRoot/Bip001");

        animator.Play(run);
        animator.EvaluateGraph(0.5f);

        Float3 sampledSmallMotion = new(sourceEndPosition.X * 0.5f, sourceEndPosition.Y * 0.5f, 0f);
        Float3 expected = bindPosition + Quaternion.Inverse(targetParentReference) * sampledSmallMotion;
        Float3 actual = travel.LocalPosition;

        Assert.Equal(bindPosition.X, actual.X, precision: 4);
        Assert.Equal(expected.Y, actual.Y, precision: 4);
        Assert.Equal(expected.Z, actual.Z, precision: 4);
        AssertPosition(expected, actual, "direct root travel carrier result");
    }

    [Fact]
    public void MultiAxisSkillTravel_RemovesVerticalAndForwardTravel_WithoutUndoingBodyMovement()
    {
        var (animator, travel) = BuildSkinnedRig(Quaternion.Identity);
        Float3 bindPosition = travel.LocalPosition;
        Float3 bodyPosition = new(4f, 0f, -3f);
        animator.Transform.Position = bodyPosition;

        // Big-skill style clips can drive both the vertical and forward axes by metres. The
        // visual skeleton must stay in place while gameplay-owned CharacterController/body
        // movement remains authoritative. Small orthogonal sway is still authored animation.
        Float3 sourceEndPosition = new(0.2f, 2.7f, -6.5f);
        AnimationClip skill = MakeTravelClip("MultiAxisSkill", sourceEndPosition);

        animator.Play(skill);
        animator.EvaluateGraph(0.5f);

        AssertPosition(bindPosition + new Float3(0.1f, 0f, 0f), travel.LocalPosition,
            "multi-axis skill travel pin result");
        AssertPosition(bodyPosition, animator.Transform.Position,
            "gameplay-owned body translation");
    }

    private (Animator animator, Transform travel) BuildSkinnedRig(Quaternion modelRootReferenceRotation)
    {
        Scene scene = CreateScene(enable: true);
        GameObject root = CreateGameObject("Armature");
        GameObject modelRoot = CreateGameObject("ModelRoot");
        modelRoot.SetParent(root);
        modelRoot.Transform.LocalRotation = modelRootReferenceRotation;

        GameObject travel = CreateGameObject("Bip001");
        travel.SetParent(modelRoot);
        travel.Transform.LocalPosition = new Float3(1f, 2f, 3f);

        GameObject pelvis = CreateGameObject("Pelvis");
        pelvis.SetParent(travel);

        root.AddComponent<SkinnedMeshRenderer>().RootBonePath = "ModelRoot/Bip001/Pelvis";
        scene.Add(root);

        var animator = new Animator
        {
            PlayAutomatically = false,
            ApplyRootMotion = false,
        };
        root.AddComponent(animator);
        return (animator, travel.Transform);
    }

    private static AnimationClip MakeTravelClip(
        string name,
        Float3 sourceEndPosition,
        string rootBonePath = "ModelRoot")
    {
        var clip = new AnimationClip
        {
            Name = name,
            Duration = 1f,
            Wrap = AnimationWrapMode.ClampForever,
            RootBonePath = rootBonePath,
        };
        clip.AddBone(new AnimationClip.AnimBone
        {
            BoneName = "ModelRoot",
            HasSourceReferencePose = true,
            SourcePosition = Float3.Zero,
            SourceRotation = Quaternion.Identity,
            SourceScale = Float3.One,
            PosX = AnimationTestHelpers.ConstantCurve(0f),
            PosY = AnimationTestHelpers.ConstantCurve(0f),
            PosZ = AnimationTestHelpers.ConstantCurve(0f),
        });
        clip.AddBone(new AnimationClip.AnimBone
        {
            BoneName = "ModelRoot/Bip001",
            HasSourceReferencePose = true,
            SourcePosition = Float3.Zero,
            SourceRotation = Quaternion.Identity,
            SourceScale = Float3.One,
            PosX = AnimationTestHelpers.LinearCurve(0f, sourceEndPosition.X, 1f),
            PosY = AnimationTestHelpers.LinearCurve(0f, sourceEndPosition.Y, 1f),
            PosZ = AnimationTestHelpers.LinearCurve(0f, sourceEndPosition.Z, 1f),
        });
        return clip;
    }

    private static void AssertPosition(Float3 expected, Float3 actual, string phase)
    {
        Assert.True(
            Float3.Length(actual - expected) < 1e-4f,
            $"{phase}: expected {expected}, actual {actual}");
    }
}
