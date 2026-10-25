// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Animation;

public partial class AnimationClipPlayable
{
    // Clip and binding indices are resolved once. Root motion must use each state's own time
    // interval: differencing the mixed absolute pose invents movement when weights change.
    private int _rootMotionClipBone = -1;
    private int _rootMotionVisualBone = -1;
    private double _frameUnwrappedTime;
    private TravelProjection _rootMotionPinProjection;

    private void CaptureRootMotionBinding(AnimationClip clip, SkeletonBinding binding)
    {
        if (BoundRootMotionPolicy is not (ClipRootMotionPolicy.LegacyAutomatic or ClipRootMotionPolicy.ExplicitBone))
            return;
        List<AnimationClip.AnimBone> bones = clip.Bones;
        if (BoundRootMotionPolicy == ClipRootMotionPolicy.ExplicitBone && clip.RootBonePath == ".")
        {
            CaptureModelRootMotionBinding(bones);
            return;
        }
        if (!string.IsNullOrEmpty(clip.RootBonePath))
        {
            for (int i = 0; i < bones.Count; i++)
            {
                if (_boneMap![i] >= 0 && bones[i].BoneName == clip.RootBonePath)
                {
                    _rootMotionClipBone = i;
                    _rootMotionVisualBone = BoundRootMotionPolicy == ClipRootMotionPolicy.ExplicitBone
                        ? _boneMap[i] : ResolveVisualCarrier(binding, _boneMap[i]);
                    return;
                }
            }
        }

        // Explicit selection is literal. No curve or missing path must not silently become
        // a sibling marker or another animated carrier.
        if (BoundRootMotionPolicy == ClipRootMotionPolicy.ExplicitBone)
            return;

        // Some rigs author a separate, non-skinned ground trajectory named Root beside the
        // skeleton carrier (e.g. Model/Root and Model/Bip001). It is not a skin-root ancestor.
        // Restrict this convention to siblings of that chain, never arbitrary weapon markers.
        Transform? skinRoot = binding.MotionRoot;
        if (skinRoot != null)
        {
            for (int i = 0; i < bones.Count; i++)
            {
                int index = _boneMap![i];
                if (index < 0 || bones[i].PosX == null)
                    continue;
                Transform? marker = binding.Bones[index];
                if (marker == null || !string.Equals(marker.GameObject.Name, "Root", StringComparison.OrdinalIgnoreCase))
                    continue;
                for (Transform? carrier = skinRoot; carrier != null && carrier != binding.Root; carrier = carrier.Parent)
                {
                    if (carrier == marker || carrier.Parent != marker.Parent)
                        continue;
                    int carrierIndex = FindBoundTransform(binding, carrier);
                    if (carrierIndex < 0)
                        continue;
                    _rootMotionClipBone = i;
                    _rootMotionVisualBone = carrierIndex;
                    return;
                }
            }
        }

        // Without a ground marker, use the shallowest animated carrier on the skin-root chain.
        // Unlike the in-place heuristic, small authored steps are meaningful root motion too.
        int bestDepth = int.MaxValue;
        int bestRank = -1;
        for (int i = 0; i < bones.Count; i++)
        {
            int index = _boneMap![i];
            if (index < 0)
                continue;
            Transform? bone = binding.Bones[index];
            if (bone == null || (skinRoot != null && !SkeletonBinding.IsAncestorOf(bone, skinRoot)))
                continue;
            // A partial clip can contain only a weapon track. "Shallowest in this clip" is not
            // evidence of a body carrier: without a resolved skin root, accept only an actual
            // top-level binding bone (explicit RootBonePath was handled above).
            if (skinRoot == null && bone.Parent != binding.Root)
                continue;
            AnimationClip.AnimBone curve = bones[i];
            int rank = CurveRange(curve.PosX) > 1e-5f || CurveRange(curve.PosY) > 1e-5f
                || CurveRange(curve.PosZ) > 1e-5f ? 2 : curve.PosX != null || curve.RotX != null ? 1 : 0;
            // An unresolved/non-skinned hierarchy provides no evidence that a moving descendant
            // carries the body. Preserve the conservative topmost fallback; never promote a
            // weapon merely because it has more motion than its otherwise stationary parents.
            if (skinRoot == null)
                rank = 0;
            int depth = 0;
            string path = binding.Paths[index];
            for (int c = 0; c < path.Length; c++)
                if (path[c] == '/') depth++;
            if (rank < bestRank || (rank == bestRank && depth >= bestDepth))
                continue;
            bestRank = rank;
            bestDepth = depth;
            _rootMotionClipBone = i;
            _rootMotionVisualBone = index;
        }
    }

    private static int FindBoundTransform(SkeletonBinding binding, Transform transform)
    {
        for (int i = 0; i < binding.BoneCount; i++)
            if (ReferenceEquals(binding.Bones[i], transform)) return i;
        return -1;
    }

    private static int ResolveVisualCarrier(SkeletonBinding binding, int sourceIndex)
    {
        Transform? source = binding.Bones[sourceIndex];
        Transform? skinRoot = binding.MotionRoot;
        if (source != null && skinRoot != null && !SkeletonBinding.IsAncestorOf(source, skinRoot))
        {
            for (Transform? carrier = skinRoot; carrier != null && carrier != binding.Root; carrier = carrier.Parent)
                if (carrier.Parent == source.Parent)
                    return FindBoundTransform(binding, carrier);
        }
        return sourceIndex;
    }

    private void CaptureRootMotionProjection(AnimationClip clip, SkeletonBinding binding)
    {
        _rootMotionPinProjection = default;
        int index = _rootMotionClipBone;
        if (index < 0 || BoundRootMotionUsesModelRoot)
            return;
        AnimationClip.AnimBone curve = clip.Bones[index];
        Quaternion basis = _retarget![index] ? _sourceParentToTargetBasis![index] : Quaternion.Identity;
        Float3 start = curve.EvaluatePositionAt(0f);
        byte count = 0;
        Float3 axis0 = default, axis1 = default, axis2 = default;
        // Ground-marker reference metadata may contain the previous action's final translation.
        // Include its authored offset from the destination ground origin, so a constant recovery
        // still pins the matching visual travel without emitting a body increment.
        int boneIndex = _boneMap![index];
        AddRootMotionAxis(basis * start - binding.ReferencePose[boneIndex].LocalPosition,
            ref count, ref axis0, ref axis1, ref axis2);
        CaptureCurveDirections(curve.PosX, curve, start, basis, ref count, ref axis0, ref axis1, ref axis2);
        CaptureCurveDirections(curve.PosY, curve, start, basis, ref count, ref axis0, ref axis1, ref axis2);
        CaptureCurveDirections(curve.PosZ, curve, start, basis, ref count, ref axis0, ref axis1, ref axis2);
        _rootMotionPinProjection = new TravelProjection(boneIndex, count, axis0, axis1, axis2);
    }

    private static void CaptureCurveDirections(XEngine.Runtime.AnimationCurve? times, AnimationClip.AnimBone curve,
        Float3 start, Quaternion basis, ref byte count, ref Float3 axis0, ref Float3 axis1, ref Float3 axis2)
    {
        if (times == null)
            return;
        for (int i = 0; i < times.Keys.Count && count < 3; i++)
        {
            AddRootMotionAxis(basis * (curve.EvaluatePositionAt(times.Keys[i].Position) - start),
                ref count, ref axis0, ref axis1, ref axis2);
            // Equal-valued Hermite endpoints can still travel between keys. Inspect the segment
            // interior too; imported baked trajectories and directly authored curves both work.
            if (i == 0)
                continue;
            float previous = times.Keys[i - 1].Position;
            float interval = times.Keys[i].Position - previous;
            for (int sample = 1; sample < 4 && count < 3; sample++)
                AddRootMotionAxis(basis * (curve.EvaluatePositionAt(previous + interval * sample * 0.25f) - start),
                    ref count, ref axis0, ref axis1, ref axis2);
        }
    }

    private static void AddRootMotionAxis(Float3 axis, ref byte count,
        ref Float3 axis0, ref Float3 axis1, ref Float3 axis2)
    {
        if (count > 0) axis -= axis0 * Float3.Dot(axis, axis0);
        if (count > 1) axis -= axis1 * Float3.Dot(axis, axis1);
        if (count > 2) return;
        float lengthSquared = Float3.LengthSquared(axis);
        if (lengthSquared <= 1e-8f)
            return; // Reject exporter noise, not the previous 0.5 m locomotion threshold.
        axis /= MathF.Sqrt(lengthSquared);
        if (count == 0) axis0 = axis;
        else if (count == 1) axis1 = axis;
        else axis2 = axis;
        count++;
    }

    /// <summary>Returns this frame's retargeted increments in the trajectory bone's parent space.</summary>
    [HotPath]
    internal bool TryGetRootMotionDelta(bool includeRotation, out Float3 positionDelta,
        out Quaternion rotationDelta, out int boneIndex, out int visualBoneIndex, out TravelProjection pinProjection)
    {
        positionDelta = default;
        rotationDelta = Quaternion.Identity;
        boneIndex = -1;
        visualBoneIndex = _rootMotionVisualBone;
        pinProjection = _rootMotionPinProjection;
        int clipIndex = _rootMotionClipBone;
        if (clipIndex < 0 || Clip == null || _boneMap == null || Duration <= 0d)
            return false;
        boneIndex = _boneMap[clipIndex];
        if (_frameUnwrappedTime == _framePrevTime)
            return true; // paused, sampled, or zero speed: no seek/enable catch-up delta

        double previous = AdvanceTime(_framePrevTime, Duration, Wrap);
        double cycles = Wrap == AnimationWrapMode.Loop
            ? Math.Floor(_frameUnwrappedTime / Duration) - Math.Floor(_framePrevTime / Duration)
            : 0d;
        AnimationClip.AnimBone curve = Clip.Bones[clipIndex];
        if (includeRotation && curve.RotX != null)
        {
            Float3 previousPosition = SampleRootPosition(curve, clipIndex, (float)previous);
            Float3 currentPosition = SampleRootPosition(curve, clipIndex, (float)_frameCurTime);
            Quaternion previousRotation = SampleRootRotation(curve, clipIndex, (float)previous);
            Quaternion currentRotation = SampleRootRotation(curve, clipIndex, (float)_frameCurTime);
            Quaternion start = SampleRootRotation(curve, clipIndex, 0f);
            if (cycles != 0d)
            {
                Quaternion end = SampleRootRotation(curve, clipIndex, (float)Duration);
                Quaternion cycleRotation = end * Quaternion.Inverse(start);
                Float3 cyclePosition = SampleRootPosition(curve, clipIndex, (float)Duration)
                    - cycleRotation * SampleRootPosition(curve, clipIndex, 0f);
                MotionPower(cycleRotation, cyclePosition, cycles, out Quaternion wrapRotation, out Float3 wrapPosition);
                currentRotation = wrapRotation * currentRotation;
                currentPosition = wrapPosition + wrapRotation * currentPosition;
            }
            // The actor composes on the right. First compute a local rigid-transform increment,
            // then express it in the fixed clip-start axes. Using a spatial difference here would
            // conjugate non-commuting turns by the actor's already extracted previous rotation.
            Quaternion previousInverse = Quaternion.Inverse(previousRotation);
            rotationDelta = Quaternion.NormalizeSafe(start * (previousInverse * currentRotation) * Quaternion.Inverse(start));
            positionDelta = start * (previousInverse * (currentPosition - previousPosition));
        }
        else
        {
            positionDelta = curve.EvaluatePositionAt((float)_frameCurTime) - curve.EvaluatePositionAt((float)previous);
            if (cycles != 0d)
            {
                Float3 cycleDelta = curve.EvaluatePositionAt((float)Duration) - curve.EvaluatePositionAt(0f);
                positionDelta += cycleDelta * (float)cycles;
            }
            if (_retarget![clipIndex])
                positionDelta = _sourceParentToTargetBasis![clipIndex] * positionDelta;
        }
        if (BoundRootMotionUsesModelRoot)
            ConvertModelRootMotion(ref positionDelta, ref rotationDelta);
        return true;
    }

    [HotPath]
    private Float3 SampleRootPosition(AnimationClip.AnimBone curve, int index, float time)
    {
        Float3 position = curve.EvaluatePositionAt(time);
        return _retarget![index]
            ? _targetReferencePose![index].LocalPosition + _sourceParentToTargetBasis![index] * (position - curve.SourcePosition)
            : position;
    }

    [HotPath]
    private Quaternion SampleRootRotation(AnimationClip.AnimBone curve, int index, float time)
    {
        Quaternion rotation = curve.EvaluateRotationAt(time);
        return _retarget![index]
            ? Quaternion.NormalizeSafe(_sourceParentToTargetBasis![index] * (rotation * _targetToSourceBasis![index]))
            : rotation;
    }

    [HotPath]
    private static void MotionPower(Quaternion rotation, Float3 position, double exponent,
        out Quaternion resultRotation, out Float3 resultPosition)
    {
        if (exponent < 0d)
        {
            rotation = Quaternion.Inverse(rotation);
            position = -(rotation * position);
            exponent = -exponent;
        }
        resultRotation = Quaternion.Identity;
        resultPosition = Float3.Zero;
        // Repeated squaring handles multi-loop catch-up without a per-loop walk.
        while (exponent >= 1d)
        {
            if (exponent % 2d >= 1d)
            {
                resultPosition += resultRotation * position;
                resultRotation = Quaternion.NormalizeSafe(resultRotation * rotation);
            }
            position += rotation * position;
            rotation = Quaternion.NormalizeSafe(rotation * rotation);
            exponent = Math.Floor(exponent * 0.5d);
        }
    }
}
