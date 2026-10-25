// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Playables;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies <see cref="AnimationClipPlayable"/> samples its clip's curves into the output
/// <see cref="Pose"/> at the right time, flags only the channels whose curves exist, advances local
/// time with the correct wrap mode, and skips bones whose path didn't resolve in the binding.
/// </summary>
public class AnimationClipPlayableTests : RuntimeTestBase
{
    private static readonly PlayableContext Ctx = new(0.5f, 0.5, isScrubbing: false);

    private (SkeletonBinding binding, AnimationClipPlayable playable) BuildWithClip(string bonePath, float endX, float duration = 1f)
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var binding = SkeletonBinding.Build(root.Transform);

        var clip = AnimationTestHelpers.MakeSingleBonePosXClip("t", bonePath, endX, duration);
        var playable = new AnimationClipPlayable(clip);
        playable.Bind(binding);
        playable.Play();
        playable.Weight = 1f; // an actively-sampled clip is at full weight; the Weight<=0 early-out would otherwise skip it.
        return (binding, playable);
    }

    [Fact]
    public void ProcessFrame_AtTimeZero_SamplesStartValues()
    {
        var (_, p) = BuildWithClip("Hips", endX: 10f);

        p.ProcessFrame(new PlayableContext(0f, 0d, false));

        Assert.Equal(0f, p.Output!.Bones[0].LocalPosition.X);
    }

    [Fact]
    public void ProcessFrame_AtDuration_SamplesEndValues()
    {
        var (_, p) = BuildWithClip("Hips", endX: 10f, duration: 1f);

        p.SetTime(1.0);
        p.ProcessFrame(Ctx);

        Assert.Equal(10f, p.Output!.Bones[0].LocalPosition.X);
    }

    [Fact]
    public void ProcessFrame_AtMidpoint_SamplesInterpolatedValue()
    {
        // Linear curve 0 -> 10 over 1s; at t=0.5 the value is exactly 5.
        var (_, p) = BuildWithClip("Hips", endX: 10f, duration: 1f);

        p.SetTime(0.5);
        p.ProcessFrame(Ctx);

        Assert.Equal(5f, p.Output!.Bones[0].LocalPosition.X);
    }

    [Fact]
    public void ProcessFrame_FlagsOnlyPositionChannel_WhenRotationScaleCurvesAbsent()
    {
        // MakeSingleBonePosXClip leaves RotX/ScaleX null → only Position channel should be flagged.
        var (_, p) = BuildWithClip("Hips", endX: 10f);

        p.ProcessFrame(Ctx);

        Assert.Equal(PoseChannel.Position, p.Output!.Bones[0].Channels);
    }

    [Fact]
    public void ProcessFrame_ClearsBeforeSampling_FreshEachFrame()
    {
        var (_, p) = BuildWithClip("Hips", endX: 10f);

        // Frame 1: drive the bone.
        p.SetTime(0.5);
        p.ProcessFrame(Ctx);
        Assert.Equal(PoseChannel.Position, p.Output!.Bones[0].Channels);

        // Frame 2: swap to a clip that drives nothing on this bone — channels must reset to None.
        var emptyClip = new global::XEngine.Runtime.AnimationClip { Name = "empty" };
        emptyClip.Duration = 1f;
        var binding = p.Binding!;
        p.SetClip(emptyClip, binding);
        p.ProcessFrame(Ctx);

        Assert.Equal(PoseChannel.None, p.Output!.Bones[0].Channels);
    }

    [Fact]
    public void PrepareFrame_AdvancesTimeByDeltaTimesSpeed()
    {
        var (_, p) = BuildWithClip("Hips", endX: 10f, duration: 2f);
        p.Speed = 2f;

        p.PrepareFrame(new PlayableContext(0.25f, 0d, false));

        Assert.Equal(0.5, p.Time, precision: 6); // 0.25 * 2
    }

    [Fact]
    public void PrepareFrame_Loop_WrapsPastDuration()
    {
        var (_, p) = BuildWithClip("Hips", endX: 10f, duration: 1f);
        p.SetTime(0.9);

        p.PrepareFrame(new PlayableContext(0.3f, 0d, false)); // 0.9 + 0.3 = 1.2 → wraps to 0.2

        Assert.Equal(0.2, p.Time, precision: 5);
    }

    [Fact]
    public void ProcessFrame_SkipsUnresolvedBonePaths()
    {
        // Clip references a bone "Tail" that doesn't exist in the hierarchy.
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var binding = SkeletonBinding.Build(root.Transform);

        var clip = AnimationTestHelpers.MakeSingleBonePosXClip("tailclip", "Tail", endX: 99f);
        var p = new AnimationClipPlayable(clip);
        p.Bind(binding);
        p.Play();

        p.ProcessFrame(Ctx);

        // No resolved bones → pose stays cleared (all Channels None), no throw.
        Assert.Equal(PoseChannel.None, p.Output!.Bones[0].Channels);
    }

    [Fact]
    public void ProcessFrame_SourceReferencePose_PreservesAuthoredFirstFrameAcrossBoneFrames()
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var spine = CreateGameObject("Spine");
        spine.SetParent(hips);

        Quaternion targetHips = Quaternion.AxisAngle(Float3.UnitY, 0.7f);
        Quaternion targetSpine = Quaternion.AxisAngle(Float3.UnitX, 0.35f);
        hips.Transform.LocalPosition = new Float3(1f, 2f, 3f);
        hips.Transform.LocalRotation = targetHips;
        hips.Transform.LocalScale = new Float3(1f, 1.5f, 1f);
        spine.Transform.LocalPosition = new Float3(0f, 2f, 0f);
        spine.Transform.LocalRotation = targetSpine;

        var binding = SkeletonBinding.Build(root.Transform);

        Quaternion sourceHipsRef = Quaternion.AxisAngle(Float3.UnitX, 0.8f);
        Quaternion sourceSpineRef = Quaternion.AxisAngle(Float3.UnitZ, -0.45f);
        Quaternion authoredHips = sourceHipsRef * Quaternion.AxisAngle(Float3.UnitZ, 0.2f);
        Quaternion authoredSpine = sourceSpineRef * Quaternion.AxisAngle(Float3.UnitY, -0.3f);

        var clip = new global::XEngine.Runtime.AnimationClip { Name = "retarget", Duration = 1f };
        var hipsBone = MakeReferenceBone(
            "Hips",
            new Float3(4f, 5f, 6f), sourceHipsRef, new Float3(2f, 2f, 2f),
            new Float3(4.5f, 5f, 6f), authoredHips, new Float3(3f, 2f, 2f));
        var spineBone = MakeReferenceBone(
            "Hips/Spine",
            new Float3(0f, 2f, 0f), sourceSpineRef, Float3.One,
            new Float3(0.25f, 2f, 0f), authoredSpine, Float3.One);
        clip.AddBone(hipsBone);
        clip.AddBone(spineBone);

        var playable = new AnimationClipPlayable(clip) { Weight = 1f };
        playable.Bind(binding);
        playable.Play();
        playable.ProcessFrame(new PlayableContext(0f, 0d, false));

        Quaternion targetToSourceHips = Quaternion.Inverse(sourceHipsRef) * targetHips;
        Quaternion expectedHips = authoredHips * targetToSourceHips;
        Quaternion targetToSourceSpine = Quaternion.Inverse(sourceSpineRef)
            * (targetToSourceHips * targetSpine);
        Quaternion expectedSpine = Quaternion.Inverse(targetToSourceHips)
            * (authoredSpine * targetToSourceSpine);

        Assert.True(Quaternion.Angle(expectedHips, playable.Output!.Bones[0].LocalRotation) < 1e-4f);
        Assert.True(Quaternion.Angle(expectedSpine, playable.Output.Bones[1].LocalRotation) < 1e-4f);
        Assert.True(Quaternion.Angle(targetHips, playable.Output.Bones[0].LocalRotation) > 0.05f,
            "authored first-frame motion was incorrectly collapsed to the destination bind pose");
        Assert.Equal(1.5f, playable.Output.Bones[0].LocalPosition.X, precision: 4);
        Float3 expectedSpinePosition = new Float3(0f, 2f, 0f)
            + Quaternion.Inverse(targetToSourceHips) * new Float3(0.25f, 0f, 0f);
        Assert.Equal(expectedSpinePosition.X, playable.Output.Bones[1].LocalPosition.X, precision: 4);
        Assert.Equal(expectedSpinePosition.Y, playable.Output.Bones[1].LocalPosition.Y, precision: 4);
        Assert.Equal(expectedSpinePosition.Z, playable.Output.Bones[1].LocalPosition.Z, precision: 4);
        Assert.Equal(1.5f, playable.Output.Bones[0].LocalScale.X, precision: 4);
        Assert.Equal(1.5f, playable.Output.Bones[0].LocalScale.Y, precision: 4);
    }

    [Fact]
    public void ProcessFrame_ClipWithoutSourceReferencePose_SamplesAbsoluteRotation()
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        hips.Transform.LocalRotation = Quaternion.AxisAngle(Float3.UnitX, 0.8f);
        var binding = SkeletonBinding.Build(root.Transform);

        Quaternion authored = Quaternion.AxisAngle(Float3.UnitZ, 0.4f);
        var clip = AnimationTestHelpers.MakeSingleBoneRotClip("absolute", "Hips", authored, authored);
        var playable = new AnimationClipPlayable(clip) { Weight = 1f };
        playable.Bind(binding);
        playable.Play();
        playable.ProcessFrame(new PlayableContext(0f, 0d, false));

        Assert.True(Quaternion.Angle(authored, playable.Output!.Bones[0].LocalRotation) < 1e-4f);
    }

    private static global::XEngine.Runtime.AnimationClip.AnimBone MakeReferenceBone(
        string path,
        Float3 sourcePosition,
        Quaternion sourceRotation,
        Float3 sourceScale,
        Float3 authoredPosition,
        Quaternion authoredRotation,
        Float3 authoredScale)
    {
        return new global::XEngine.Runtime.AnimationClip.AnimBone
        {
            BoneName = path,
            HasSourceReferencePose = true,
            SourcePosition = sourcePosition,
            SourceRotation = sourceRotation,
            SourceScale = sourceScale,
            PosX = AnimationTestHelpers.ConstantCurve(authoredPosition.X),
            PosY = AnimationTestHelpers.ConstantCurve(authoredPosition.Y),
            PosZ = AnimationTestHelpers.ConstantCurve(authoredPosition.Z),
            RotX = AnimationTestHelpers.ConstantCurve(authoredRotation.X),
            RotY = AnimationTestHelpers.ConstantCurve(authoredRotation.Y),
            RotZ = AnimationTestHelpers.ConstantCurve(authoredRotation.Z),
            RotW = AnimationTestHelpers.ConstantCurve(authoredRotation.W),
            ScaleX = AnimationTestHelpers.ConstantCurve(authoredScale.X),
            ScaleY = AnimationTestHelpers.ConstantCurve(authoredScale.Y),
            ScaleZ = AnimationTestHelpers.ConstantCurve(authoredScale.Z),
        };
    }
}
