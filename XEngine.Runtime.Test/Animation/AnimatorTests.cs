// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// End-to-end integration test for the <see cref="global::XEngine.Runtime.Animator"/> component:
/// a live scene with a bone hierarchy, an Animator driving a single clip via the PlayableGraph,
/// verified by ticking the simulation and reading back bone Transforms. This is the M1 acceptance
/// test — "Animator plays a clip and the bones move, behaviour on par with legacy AnimationComponent".
/// </summary>
public class AnimatorTests : RuntimeTestBase
{
    private GameObject BuildSkinnedHierarchy()
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var spine = CreateGameObject("Spine");
        spine.SetParent(hips);
        return root;
    }

    [Fact]
    public void Play_AdvancesBoneTransformOverTime()
    {
        var scene = CreateScene(enable: true);
        var root = BuildSkinnedHierarchy();
        scene.Add(root);

        var animator = new Animator { PlayAutomatically = false, Speed = 1f };
        root.AddComponent(animator);

        // Clip moves Hips.PosX 0 -> 10 over 1 second (linear).
        var clip = AnimationTestHelpers.MakeSingleBonePosXClip("move", "Hips", endX: 10f, duration: 1f);
        animator.Play(clip);

        Assert.True(animator.IsPlaying);
        Assert.Same(clip, animator.CurrentClip);

        Update(scene, frames: 1); // one frame at dt = 1/60.

        // After one frame, clip time = 1/60; linear 0->10 → Hips.PosX = 10/60.
        var hips = root.Children[0]; // "Hips"
        Assert.Equal(10f / 60f, hips.Transform.LocalPosition.X, precision: 4);
    }

    [Fact]
    public void MultipleFrames_KeepAdvancing()
    {
        var scene = CreateScene(enable: true);
        var root = BuildSkinnedHierarchy();
        scene.Add(root);

        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        var clip = AnimationTestHelpers.MakeSingleBonePosXClip("move", "Hips", endX: 10f, duration: 1f);
        animator.Play(clip);

        Update(scene, frames: 30); // 30 frames at 1/60 → t = 0.5 → PosX = 5.

        var hips = root.Children[0];
        Assert.Equal(5f, hips.Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void Pause_StopsAdvancing_ResumeContinues()
    {
        var scene = CreateScene(enable: true);
        var root = BuildSkinnedHierarchy();
        scene.Add(root);

        var animator = new Animator { PlayAutomatically = false, Wrap = AnimationWrapMode.ClampForever };
        root.AddComponent(animator);
        var clip = AnimationTestHelpers.MakeSingleBonePosXClip("move", "Hips", endX: 10f, duration: 1f);
        animator.Play(clip);

        Update(scene, frames: 30); // t = 0.5
        animator.Pause();
        Assert.False(animator.IsPlaying);
        Update(scene, frames: 30); // paused — should not advance
        Assert.Equal(5f, root.Children[0].Transform.LocalPosition.X, precision: 3);

        animator.Resume();
        Update(scene, frames: 30); // t = 0.5 + 0.5 = 1.0, clamped at duration → PosX = 10
        Assert.Equal(10f, root.Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void UnanimatedBone_RetainsGameplayTransform()
    {
        var scene = CreateScene(enable: true);
        var root = BuildSkinnedHierarchy();
        scene.Add(root);

        // Spine gets a gameplay-written transform before animation runs.
        root.Children[0].Children[0].Transform.LocalPosition = new Vector.Float3(3f, 0f, 0f);

        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        animator.Play(AnimationTestHelpers.MakeSingleBonePosXClip("move", "Hips", endX: 10f, duration: 1f));

        Update(scene, frames: 30);

        // Hips moved; Spine keeps its gameplay transform (3, 0, 0).
        Assert.Equal(5f, root.Children[0].Transform.LocalPosition.X, precision: 3);
        Assert.Equal(3f, root.Children[0].Children[0].Transform.LocalPosition.X);
    }

    [Fact]
    public void DisableEnable_PreservesReferencePoseAndRetargetedOutput()
    {
        var scene = CreateScene(enable: true);
        var root = BuildSkinnedHierarchy();
        var hips = root.Children[0].Transform;
        var targetReferencePosition = new Float3(1f, 2f, 3f);
        var targetReferenceRotation = Quaternion.AxisAngle(Float3.UnitY, 0.35f);
        var targetReferenceScale = new Float3(1.5f, 0.8f, 1.2f);
        hips.LocalPosition = targetReferencePosition;
        hips.LocalRotation = targetReferenceRotation;
        hips.LocalScale = targetReferenceScale;
        scene.Add(root);

        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        AnimationClip clip = MakeRetargetedConstantPoseClip();

        animator.Play(clip);
        animator.EvaluateGraph(0.25f);
        Float3 firstOutputPosition = hips.LocalPosition;
        Quaternion firstOutputRotation = hips.LocalRotation;
        Float3 firstOutputScale = hips.LocalScale;
        var initialReference = animator.Skeleton!.ReferencePose[0];

        Assert.True(Float3.Length(firstOutputPosition - targetReferencePosition) > 0.1f);
        Assert.True(Quaternion.Angle(firstOutputRotation, targetReferenceRotation) > 0.1f);

        // OnDisable intentionally leaves the live hierarchy at the sampled pose. OnEnable must
        // not capture that pose as the next graph's retargeting reference.
        animator.Enabled = false;
        animator.Enabled = true;

        var rebuiltReference = animator.Skeleton!.ReferencePose[0];
        Assert.True(Float3.Length(rebuiltReference.LocalPosition - initialReference.LocalPosition) < 1e-5f);
        Assert.True(Quaternion.Angle(rebuiltReference.LocalRotation, initialReference.LocalRotation) < 1e-5f);
        Assert.True(Float3.Length(rebuiltReference.LocalScale - initialReference.LocalScale) < 1e-5f);

        animator.Play(clip);
        animator.EvaluateGraph(0.25f);

        Assert.True(Float3.Length(hips.LocalPosition - firstOutputPosition) < 1e-5f);
        Assert.True(Quaternion.Angle(hips.LocalRotation, firstOutputRotation) < 1e-5f);
        Assert.True(Float3.Length(hips.LocalScale - firstOutputScale) < 1e-5f);
    }

    [Fact]
    public void RebuildGraph_OnlyRecapturesReferencePoseThroughExplicitColdPath()
    {
        var scene = CreateScene(enable: true);
        var root = BuildSkinnedHierarchy();
        var hips = root.Children[0].Transform;
        hips.LocalPosition = new Float3(1f, 2f, 3f);
        hips.LocalRotation = Quaternion.AxisAngle(Float3.UnitY, 0.25f);
        scene.Add(root);

        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        var initialReference = animator.Skeleton!.ReferencePose[0];

        var replacementPosition = new Float3(7f, 8f, 9f);
        var replacementRotation = Quaternion.AxisAngle(Float3.UnitX, 0.6f);
        hips.LocalPosition = replacementPosition;
        hips.LocalRotation = replacementRotation;

        animator.RebuildGraph();
        var preservedReference = animator.Skeleton!.ReferencePose[0];
        Assert.True(Float3.Length(preservedReference.LocalPosition - initialReference.LocalPosition) < 1e-5f);
        Assert.True(Quaternion.Angle(preservedReference.LocalRotation, initialReference.LocalRotation) < 1e-5f);

        animator.RebuildGraphAndRecaptureReferencePose();
        var recapturedReference = animator.Skeleton!.ReferencePose[0];
        Assert.True(Float3.Length(recapturedReference.LocalPosition - replacementPosition) < 1e-5f);
        Assert.True(Quaternion.Angle(recapturedReference.LocalRotation, replacementRotation) < 1e-5f);
    }

    [Fact]
    public void RebuildGraph_RootPinUsesThePreservedReferencePose()
    {
        var scene = CreateScene(enable: true);
        var root = BuildSkinnedHierarchy();
        var hips = root.Children[0].Transform;
        var referencePosition = new Float3(2f, 3f, 4f);
        hips.LocalPosition = referencePosition;
        root.AddComponent<SkinnedMeshRenderer>();
        scene.Add(root);

        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);

        // Simulate rebuilding while the hierarchy contains an evaluated pose, before the root pin
        // has taken its lazy snapshot. Both retargeting and the pin must still use the first pose.
        hips.LocalPosition = new Float3(9f, 8f, 7f);
        animator.RebuildGraph();
        animator.Play(AnimationTestHelpers.MakeConstantPosClip("travel", "Hips", posX: 12f));
        animator.EvaluateGraph(0.25f);

        Assert.True(Float3.Length(animator.Skeleton!.ReferencePose[0].LocalPosition - referencePosition) < 1e-5f);
        Assert.True(Float3.Length(hips.LocalPosition - referencePosition) < 1e-5f);
    }

    [Fact]
    public void InPlaceTravelPin_PinsRetargetedMotionInDestinationAxes()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var modelRoot = CreateGameObject("ModelRoot");
        modelRoot.SetParent(root);
        var travel = CreateGameObject("Bip001");
        travel.SetParent(modelRoot);
        var pelvis = CreateGameObject("Pelvis");
        pelvis.SetParent(travel);

        // A 90-degree destination reference mismatch rotates the child's source +Z position delta
        // onto destination -X. Looking only at the source curve's Z range therefore pins the wrong
        // axis and lets the whole skinned hierarchy travel sideways.
        modelRoot.Transform.LocalRotation = Quaternion.AxisAngle(Float3.UnitY, MathF.PI * 0.5f);
        var travelReference = new Float3(1f, 2f, 3f);
        travel.Transform.LocalPosition = travelReference;
        root.AddComponent<SkinnedMeshRenderer>();
        scene.Add(root);

        var animator = new Animator { PlayAutomatically = false, ApplyRootMotion = false };
        root.AddComponent(animator);
        var clip = new AnimationClip
        {
            Name = "retargeted-travel",
            Duration = 1f,
            Wrap = AnimationWrapMode.ClampForever,
            RootBonePath = "ModelRoot",
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
            PosX = AnimationTestHelpers.ConstantCurve(0f),
            PosY = AnimationTestHelpers.ConstantCurve(0f),
            PosZ = AnimationTestHelpers.LinearCurve(0f, 2f, 1f),
        });

        animator.Play(clip);
        animator.EvaluateGraph(0.5f);

        Assert.True(Float3.Length(travel.Transform.LocalPosition - travelReference) < 1e-5f);
        Assert.True(Float3.Length(pelvis.Transform.LocalPosition) < 1e-5f);
    }

    private static AnimationClip MakeRetargetedConstantPoseClip()
    {
        Quaternion sourceReferenceRotation = Quaternion.AxisAngle(Float3.UnitZ, 0.2f);
        Quaternion sampledRotation = Quaternion.AxisAngle(Float3.UnitX, 0.65f) * sourceReferenceRotation;
        var clip = new AnimationClip
        {
            Name = "retargeted-pose",
            Duration = 1f,
            Wrap = AnimationWrapMode.ClampForever,
        };
        clip.AddBone(new AnimationClip.AnimBone
        {
            BoneName = "Hips",
            HasSourceReferencePose = true,
            SourcePosition = new Float3(4f, 5f, 6f),
            SourceRotation = sourceReferenceRotation,
            SourceScale = Float3.One,
            PosX = AnimationTestHelpers.ConstantCurve(4.5f),
            PosY = AnimationTestHelpers.ConstantCurve(5.25f),
            PosZ = AnimationTestHelpers.ConstantCurve(5.75f),
            RotX = AnimationTestHelpers.ConstantCurve(sampledRotation.X),
            RotY = AnimationTestHelpers.ConstantCurve(sampledRotation.Y),
            RotZ = AnimationTestHelpers.ConstantCurve(sampledRotation.Z),
            RotW = AnimationTestHelpers.ConstantCurve(sampledRotation.W),
            ScaleX = AnimationTestHelpers.ConstantCurve(1.2f),
            ScaleY = AnimationTestHelpers.ConstantCurve(0.9f),
            ScaleZ = AnimationTestHelpers.ConstantCurve(1.1f),
        });
        return clip;
    }
}
