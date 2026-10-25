// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Animation.Humanoid;

/// <summary>
/// M55 — plays a baked humanoid (muscle-curve) clip onto ANY avatar exposing the same roles
/// (Mecanim humanoid retargeting): muscle channels reconstruct each target bone's local rotation
/// against the target avatar's T-pose and limits; Hips TDoF places the root (scaled by the
/// leg-length ratio of target to source rig). The owning clip playable keeps the playback clock
/// and events, and samples accessory curves before this cache writes the mapped muscle bones.
/// </summary>
internal sealed class HumanoidPoseSampler
{
    private readonly Avatar _avatar;
    private readonly AnimationClip _clip;

    // Bind caches (cold path).
    private int[] _curveMapping = Array.Empty<int>();   // per clip muscle curve → target mapping index (-1 skip).
    private byte[] _curveKind = Array.Empty<byte>();    // 0-2 rotation axis, 3-5 TDoF axis.
    private int[] _mappingBoneIndex = Array.Empty<int>(); // per avatar mapping → binding bone index (-1 skip).
    // Per-mapping scratch: [mi*6 + kind] muscle value this frame.
    private float[] _muscleScratch = Array.Empty<float>();
    private float _legScale = 1f;

    /// <summary>Mirror playback: left and right roles swap, and the Hips X translation flips across
    /// the sagittal plane. Rotation muscles transfer unchanged — the per-role canonical frame IS the
    /// avatar's own T-pose, so a symmetric rig mirrors by swapping sides alone.</summary>
    public bool Mirror;

    // M61 — root-motion TDoF tracking: the applied Hips translation delta since the last query.
    private Float3 _lastTDoF;
    private bool _tDoFInitialized;
    private Float3 _appliedTDoF;

    /// <summary>The Hips bone's T-pose local position (pin target for the root-motion carrier).</summary>
    internal Float3 HipsTPoseLocalPosition
    {
        get
        {
            int mi = _avatar.IndexOf(HumanoidBone.Hips);
            return mi >= 0 ? _avatar.Mappings[mi].LocalPosition : Float3.Zero;
        }
    }

    /// <summary>Binding index of the Hips bone (for the root-motion carrier pin), or -1.</summary>
    internal int HipsBindingIndex
    {
        get
        {
            int mi = _avatar.IndexOf(HumanoidBone.Hips);
            return mi >= 0 && _mappingBoneIndex != null && _mappingBoneIndex.Length == _avatar.Mappings.Count
                ? _mappingBoneIndex[mi]
                : -1;
        }
    }

    /// <summary>
    /// M61 — the Hips TDoF translation (scaled) applied to the pose this frame, in the hips' T-pose
    /// parent frame. The root-motion extractor consumes the frame-to-frame delta and pins the
    /// carrier so travel is not double-applied.
    /// </summary>
    internal Float3 AppliedTDoF => _appliedTDoF;


    /// <summary>Frame-to-frame TDoF delta (skeleton-root local axes); false until initialized.</summary>

    internal bool TryGetTDoFRootDelta(out Float3 delta)
    {
        delta = default;
        if (!_tDoFInitialized)
        {
            _lastTDoF = _appliedTDoF;
            _tDoFInitialized = true;
            return false;
        }
        delta = _appliedTDoF - _lastTDoF;
        _lastTDoF = _appliedTDoF;
        return true;
    }

    internal HumanoidPoseSampler(AnimationClip clip, Avatar avatar)
    {
        if (!avatar.IsHumanoid)
            throw new ArgumentException("Humanoid playback requires a Humanoid Avatar.", nameof(avatar));
        _avatar = avatar;
        _clip = clip;
    }

    /// <summary>Builds the target-rig caches after the base binding resolved the skeleton paths.</summary>
    internal void Bind(SkeletonBinding binding)
    {
        var mappings = _avatar.Mappings;
        int mappingCount = mappings.Count;
        _mappingBoneIndex = new int[mappingCount];
        for (int mi = 0; mi < mappingCount; mi++)
            _mappingBoneIndex[mi] = binding.IndexOfBonePath(mappings[mi].Path);

        var curves = _clip.MuscleCurves;
        int curveCount = curves.Count;
        _curveMapping = new int[curveCount];
        _curveKind = new byte[curveCount];
        for (int ci = 0; ci < curveCount; ci++)
        {
            MuscleCurve curve = curves[ci];
            int mi = _avatar.IndexOf(Mirror ? MirrorRole(curve.Bone) : curve.Bone);
            _curveMapping[ci] = mi;
            _curveKind[ci] = (byte)curve.Channel;
        }

        _muscleScratch = new float[mappingCount * 6];

        // Root trajectory rescale: taller targets stretch the same TDoF meters.
        float sourceLeg = _clip.HumanoidSourceLegLength;
        _legScale = sourceLeg > 1e-4f && _avatar.LegLength > 1e-4f ? _avatar.LegLength / sourceLeg : 1f;
    }

    // A refreshed cache starts at the new source's current sample, rather than subtracting the
    // previous asset's absolute Hips value or dropping the first real increment after publication.
    internal void PrimeRootMotion(float time)
    {
        Float3 tdof = default;
        var curves = _clip.MuscleCurves;
        for (int i = 0; i < curves.Count; i++)
        {
            if (_curveMapping[i] < 0 || curves[i].Bone != HumanoidBone.Hips) continue;
            float value = curves[i].Evaluate(time) * _legScale;
            switch (_curveKind[i])
            {
                case 3: tdof.X = Mirror ? -value : value; break;
                case 4: tdof.Y = value; break;
                case 5: tdof.Z = value; break;
            }
        }
        _lastTDoF = tdof;
        _tDoFInitialized = true;
    }

    [HotPath]
    internal void ApplyMuscles(Pose pose, float time)
    {
        var curves = _clip.MuscleCurves;
        int curveCount = curves.Count;
        if (curveCount == 0)
            return;

        var scratch = _muscleScratch;
        Array.Clear(scratch, 0, scratch.Length);
        for (int ci = 0; ci < curveCount; ci++)
        {
            int mi = _curveMapping[ci];
            if (mi < 0)
                continue;
            float value = curves[ci].Evaluate(time);
            if (Mirror && _curveKind[ci] == 3)
                value = -value; // Hips TDoF X flips across the sagittal plane.
            scratch[mi * 6 + _curveKind[ci]] = value;
        }

        var mappings = _avatar.Mappings;
        BonePose[] bones = pose.Bones;
        for (int mi = 0; mi < mappings.Count; mi++)
        {
            int boneIndex = _mappingBoneIndex[mi];
            if (boneIndex < 0)
                continue;

            Avatar.BoneMapping mapping = mappings[mi];
            ref BonePose bp = ref bones[boneIndex];
            Float3 muscles = new(scratch[mi * 6], scratch[mi * 6 + 1], scratch[mi * 6 + 2]);
            Quaternion rotation = HumanoidMuscleSpace.ApplyRotationMuscles(
                mapping.Bone, mapping.LocalRotation, muscles);
            bp.LocalRotation = rotation;
            bp.LocalScale = mapping.LocalScale;
            PoseChannel channels = bp.Channels | PoseChannel.Rotation | PoseChannel.Scale;

            if (mapping.Bone == HumanoidBone.Hips)
            {
                Float3 tdof = new(
                    scratch[mi * 6 + 3] * _legScale,
                    scratch[mi * 6 + 4] * _legScale,
                    scratch[mi * 6 + 5] * _legScale);
                bp.LocalPosition = mapping.LocalPosition + tdof;
                channels |= PoseChannel.Position;
                _appliedTDoF = tdof; // M61 - consumed by the root-motion extractor.
            }
            bp.Channels = channels;
        }
        pose.NormalizeRotations();
    }

    private static HumanoidBone MirrorRole(HumanoidBone bone) => bone switch
    {
        HumanoidBone.LeftShoulder => HumanoidBone.RightShoulder,
        HumanoidBone.LeftUpperArm => HumanoidBone.RightUpperArm,
        HumanoidBone.LeftLowerArm => HumanoidBone.RightLowerArm,
        HumanoidBone.LeftHand => HumanoidBone.RightHand,
        HumanoidBone.LeftUpperLeg => HumanoidBone.RightUpperLeg,
        HumanoidBone.LeftLowerLeg => HumanoidBone.RightLowerLeg,
        HumanoidBone.LeftFoot => HumanoidBone.RightFoot,
        HumanoidBone.LeftToes => HumanoidBone.RightToes,
        HumanoidBone.RightShoulder => HumanoidBone.LeftShoulder,
        HumanoidBone.RightUpperArm => HumanoidBone.LeftUpperArm,
        HumanoidBone.RightLowerArm => HumanoidBone.LeftLowerArm,
        HumanoidBone.RightHand => HumanoidBone.LeftHand,
        HumanoidBone.RightUpperLeg => HumanoidBone.LeftUpperLeg,
        HumanoidBone.RightLowerLeg => HumanoidBone.LeftLowerLeg,
        HumanoidBone.RightFoot => HumanoidBone.LeftFoot,
        HumanoidBone.RightToes => HumanoidBone.LeftToes,
        _ => bone,
    };
}
