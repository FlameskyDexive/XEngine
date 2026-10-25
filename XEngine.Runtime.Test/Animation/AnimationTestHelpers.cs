// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>Builders for synthetic AnimationClips / curves with deterministic (linear) interpolation,
/// so tests can assert exact sampled values without depending on the default smooth-tangent math.</summary>
internal static class AnimationTestHelpers
{
    /// <summary>A two-key curve whose interpolation is exactly linear (v0 at t=0, v1 at t=tEnd).</summary>
    internal static AnimationCurve LinearCurve(float v0, float v1, float tEnd)
    {
        var c = new AnimationCurve(new[] { new KeyFrame(0, v0), new KeyFrame(tEnd, v1) });
        c.SmoothTangents(CurveTangent.Linear);
        return c;
    }

    /// <summary>A single-key curve that always returns <paramref name="v"/> regardless of time.</summary>
    internal static AnimationCurve ConstantCurve(float v)
        => new(new[] { new KeyFrame(0, v) });

    /// <summary>
    /// Builds a clip that holds one bone's position X constant at <paramref name="posX"/> for its whole
    /// duration. Useful for crossfade tests where the blend result must depend only on weights, not on
    /// per-clip sample time.
    /// </summary>
    internal static AnimationClip MakeConstantPosClip(string name, string bonePath, float posX, float duration = 1f)
    {
        var clip = new AnimationClip { Name = name };
        clip.Duration = duration;
        clip.Wrap = AnimationWrapMode.Loop;
        var bone = new AnimationClip.AnimBone { BoneName = bonePath };
        bone.PosX = ConstantCurve(posX);
        bone.PosY = ConstantCurve(0f);
        bone.PosZ = ConstantCurve(0f);
        clip.AddBone(bone);
        return clip;
    }

    /// <summary>
    /// Builds a 1-second clip animating one bone's position X from 0 to <paramref name="endX"/> on the
    /// bone at <paramref name="bonePath"/>. Rotation and scale curves are left null so only the
    /// Position channel is driven (verifies the PoseChannel flagging).
    /// </summary>
    internal static AnimationClip MakeSingleBonePosXClip(string name, string bonePath, float endX, float duration = 1f)
    {
        var clip = new AnimationClip { Name = name };
        clip.Duration = duration;
        clip.Wrap = AnimationWrapMode.Loop;

        var bone = new AnimationClip.AnimBone { BoneName = bonePath };
        bone.PosX = LinearCurve(0f, endX, duration);
        bone.PosY = ConstantCurve(0f);
        bone.PosZ = ConstantCurve(0f);
        // RotX/Y/Z/W + ScaleX/Y/Z stay null → only Position channel driven.
        clip.AddBone(bone);
        return clip;
    }

    /// <summary>
    /// Builds a clip that slerps one bone's local rotation from <paramref name="from"/> to
    /// <paramref name="to"/> over <paramref name="duration"/> seconds (linear component curves).
    /// </summary>
    internal static AnimationClip MakeSingleBoneRotClip(
        string name, string bonePath, Quaternion from, Quaternion to, float duration = 1f)
    {
        var clip = new AnimationClip { Name = name };
        clip.Duration = duration;
        clip.Wrap = AnimationWrapMode.ClampForever;

        var bone = new AnimationClip.AnimBone { BoneName = bonePath };
        bone.PosX = ConstantCurve(0f);
        bone.PosY = ConstantCurve(0f);
        bone.PosZ = ConstantCurve(0f);
        bone.RotX = LinearCurve(from.X, to.X, duration);
        bone.RotY = LinearCurve(from.Y, to.Y, duration);
        bone.RotZ = LinearCurve(from.Z, to.Z, duration);
        bone.RotW = LinearCurve(from.W, to.W, duration);
        clip.AddBone(bone);
        return clip;
    }
}
