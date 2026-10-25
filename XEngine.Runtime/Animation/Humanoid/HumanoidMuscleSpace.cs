// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.Animation.Humanoid;

/// <summary>
/// M54 — the humanoid muscle space (≈ Mecanim muscle space, engine-own layout): a normalized pose
/// representation where each mapped humanoid bone contributes per-local-axis rotation "muscles"
/// (angle relative to the avatar's T-pose, normalized by a per-role limit in degrees), and Hips
/// additionally carries 3 translation TDoF channels (meters relative to the T-pose hips position).
///
/// <para><b>Convention.</b> For a bone with T-pose local rotation <c>RefQ</c> and animated local
/// rotation <c>q</c>, the delta <c>D = q · RefQ⁻¹</c> is decomposed to Euler angles; muscle_i =
/// angle_i / limit_i (unclamped — poses beyond the limit stay representable, replay is exact).
/// Reconstruction is <c>FromEuler(m·limit) · RefQ</c>. Roles are the channel keys, so a baked clip
/// plays on ANY avatar whose rig exposes those roles (cross-rig retargeting, M55).</para>
/// </summary>
public static class HumanoidMuscleSpace
{
    /// <summary>Symmetric per-axis rotation limits in degrees, by role (editor-tunable later).</summary>
    public static void GetLimits(HumanoidBone bone, out float degX, out float degY, out float degZ)
    {
        switch (bone)
        {
            case HumanoidBone.Hips: degX = 40f; degY = 40f; degZ = 40f; break;
            case HumanoidBone.Spine: degX = 24f; degY = 24f; degZ = 24f; break;
            case HumanoidBone.Chest: degX = 30f; degY = 30f; degZ = 30f; break;
            case HumanoidBone.UpperChest: degX = 12f; degY = 12f; degZ = 12f; break;
            case HumanoidBone.Neck: degX = 40f; degY = 40f; degZ = 40f; break;
            case HumanoidBone.Head: degX = 40f; degY = 40f; degZ = 40f; break;
            case HumanoidBone.LeftShoulder:
            case HumanoidBone.RightShoulder: degX = 26f; degY = 26f; degZ = 26f; break;
            case HumanoidBone.LeftUpperArm:
            case HumanoidBone.RightUpperArm: degX = 90f; degY = 90f; degZ = 90f; break;
            case HumanoidBone.LeftLowerArm:
            case HumanoidBone.RightLowerArm: degX = 80f; degY = 80f; degZ = 80f; break;
            case HumanoidBone.LeftHand:
            case HumanoidBone.RightHand: degX = 40f; degY = 40f; degZ = 40f; break;
            case HumanoidBone.LeftUpperLeg:
            case HumanoidBone.RightUpperLeg: degX = 90f; degY = 90f; degZ = 90f; break;
            case HumanoidBone.LeftLowerLeg:
            case HumanoidBone.RightLowerLeg: degX = 90f; degY = 90f; degZ = 90f; break;
            case HumanoidBone.LeftFoot:
            case HumanoidBone.RightFoot: degX = 30f; degY = 30f; degZ = 30f; break;
            case HumanoidBone.LeftToes:
            case HumanoidBone.RightToes: degX = 30f; degY = 30f; degZ = 30f; break;
            default: degX = degY = degZ = 45f; break;
        }
    }

    /// <summary>Rotation muscles (normalized angles) of <paramref name="localRotation"/> relative to
    /// the T-pose rotation <paramref name="referenceRotation"/> for one bone. Angles are wrapped to
    /// [−180, 180] per axis so curve interpolation between baked keys takes the short path.</summary>
    public static Float3 RotationMuscles(Quaternion referenceRotation, Quaternion localRotation)
    {
        Quaternion delta = localRotation * Quaternion.Inverse(referenceRotation);
        Float3 euler = Quaternion.ToEuler(delta); // degrees, matching FromEuler's order.
        euler = new Float3(WrapDegrees(euler.X), WrapDegrees(euler.Y), WrapDegrees(euler.Z));
        return euler;
    }

    /// <summary>Wraps an angle to (−180, 180].</summary>
    public static float WrapDegrees(float angle)
    {
        if (angle > 180f)
            return angle - 360f;
        if (angle <= -180f)
            return angle + 360f;
        return angle;
    }

    /// <summary>Reconstructs the local rotation from normalized muscle angles (limits from
    /// <see cref="GetLimits"/>). Exact inverse of <see cref="RotationMuscles"/>.</summary>
    public static Quaternion ApplyRotationMuscles(HumanoidBone bone, Quaternion referenceRotation, Float3 muscles)
    {
        GetLimits(bone, out float lx, out float ly, out float lz);
        Quaternion delta = Quaternion.FromEuler(muscles.X * lx, muscles.Y * ly, muscles.Z * lz);
        return delta * referenceRotation;
    }

    /// <summary>Normalizes raw degrees to muscles for a role (baking helper).</summary>
    public static Float3 NormalizeAngles(HumanoidBone bone, Float3 degrees)
    {
        GetLimits(bone, out float lx, out float ly, out float lz);
        return new Float3(degrees.X / lx, degrees.Y / ly, degrees.Z / lz);
    }

    /// <summary>Hips TDoF normalization scale (meters per unit muscle ≈ half the leg length).</summary>
    public const float HipsTDoFRangeMeters = 0.5f;
}
