// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Animation;
using XEngine.Animation.Humanoid;
using XEngine.Vector;

namespace XEngine.Runtime;

public partial class Animator
{
    /// <summary>
    /// Extract only body-space XZ translation, retaining vertical bone animation. Useful for a
    /// grounded CharacterController. Defaults to false so full 3D root motion remains available.
    /// </summary>
    public bool RootMotionPlanar;

    /// <summary>
    /// Extract root rotation as well as translation. Disable to keep authored bone turns/spins
    /// in the visual pose while gameplay controls the actor's heading. Defaults to true.
    /// </summary>
    public bool ApplyRootMotionRotation = true;

    private byte[] _rootMotionPinFlags = Array.Empty<byte>();
    private int[] _rootMotionPinIndices = Array.Empty<int>();
    private int _rootMotionPinCount;

    private void PrepareRootMotionBuffers(int boneCount)
    {
        if (_rootMotionPinFlags.Length != boneCount)
        {
            _rootMotionPinFlags = new byte[boneCount];
            _rootMotionPinIndices = new int[boneCount];
        }
        else
            Array.Clear(_rootMotionPinFlags, 0, _rootMotionPinFlags.Length);
        _rootMotionPinCount = 0;
    }

    /// <summary>
    /// Blend per-state increments, never absolute positions. Each clip's start offset, a fading
    /// previous state, and a wrap from end to start therefore cannot become a teleport.
    /// </summary>
    [HotPath]
    private void ExtractRootMotion(bool applyToBody = true)
    {
        if (_layer == null || _binding == null)
            return;
        Float3 positionDelta = default;
        Quaternion rotationSum = default;
        float baseWeight = _layerMixer?.GetLayerWeight(0) ?? 1f;
        AccumulateRootMotion(_layer, baseWeight, ref positionDelta, ref rotationSum);
        PinRootMotionPose();
        if (!applyToBody)
            return; // Inspector evaluation updates/pins the pose without moving the preview actor.
        if (RootMotionPlanar)
            positionDelta.Y = 0f;
        Quaternion rotationDelta = ApplyRootMotionRotation
            ? Quaternion.NormalizeSafe(rotationSum) : Quaternion.Identity;
        if (OnAnimatorMove != null)
            OnAnimatorMove(positionDelta, rotationDelta);
        else
        {
            Transform.Position += Transform.TransformVector(positionDelta);
            if (ApplyRootMotionRotation)
                Transform.Rotation = Quaternion.NormalizeSafe(Transform.Rotation * rotationDelta);
        }
    }

    [HotPath]
    private void AccumulateRootMotion(AnimationPlayable playable, float weight,
        ref Float3 positionDelta, ref Quaternion rotationSum)
    {
        if (weight <= 1e-5f)
            return;
        // M61 — humanoid (muscle) states contribute the Hips TDoF translation as root motion and
        // pin the carrier back to its T-pose so the travel is not double-applied visually.
        if (playable is AnimationClipPlayable { HumanoidSampler: { } muscle })
        {
            if (muscle.TryGetTDoFRootDelta(out Float3 tdofDelta))
            {
                int hipsIndex = muscle.HipsBindingIndex;
                Transform? hips = hipsIndex >= 0 && _binding != null ? _binding.Bones[hipsIndex] : null;
                Transform? parent = hips?.Parent;
                Float3 worldDelta = parent != null ? parent.TransformVector(tdofDelta) : tdofDelta;
                positionDelta += Transform.InverseTransformVector(worldDelta) * weight;
                if (hips != null)
                    hips.LocalPosition = muscle.HipsTPoseLocalPosition; // Pin: reset the carrier to its T-pose (Apply rewrote the full TDoF).
            }
            return;
        }
        if (playable is AnimationClipPlayable clip)
        {
            Quaternion bodyRotation = Quaternion.Identity;
            if (clip.TryGetRootMotionDelta(ApplyRootMotionRotation, out Float3 localDelta,
                out Quaternion localRotation, out int sourceIndex, out int visualIndex,
                out AnimationClipPlayable.TravelProjection pinProjection))
            {
                bool modelRoot = clip.BoundRootMotionUsesModelRoot;
                Transform? source = modelRoot ? null : _binding!.Bones[sourceIndex];
                Transform? parent = modelRoot ? _binding!.Root : source?.Parent;
                Float3 worldDelta = parent != null ? parent.TransformVector(localDelta) : localDelta;
                positionDelta += Transform.InverseTransformVector(worldDelta) * weight;
                if (ApplyRootMotionRotation)
                {
                    Quaternion parentToBody = Quaternion.Inverse(Transform.Rotation)
                        * (parent?.Rotation ?? Quaternion.Identity);
                    bodyRotation = parentToBody * localRotation * Quaternion.Inverse(parentToBody);
                }
                if (!modelRoot && !_binding.PinRootMotionPerState)
                {
                    RegisterRootMotionPin(sourceIndex, ApplyRootMotionRotation, in pinProjection, parent);
                    RegisterRootMotionPin(visualIndex, visualIndex == sourceIndex && ApplyRootMotionRotation, in pinProjection, parent);
                }
            }
            float signedWeight = bodyRotation.W < 0f ? -weight : weight;
            rotationSum.X += bodyRotation.X * signedWeight;
            rotationSum.Y += bodyRotation.Y * signedWeight;
            rotationSum.Z += bodyRotation.Z * signedWeight;
            rotationSum.W += bodyRotation.W * signedWeight;
            return;
        }

        int count = playable.InputCount;
        if (playable is BlendTreePlayable tree)
        {
            for (int i = 0; i < count; i++)
                if (tree.GetInput(i) is AnimationPlayable child)
                    AccumulateRootMotion(child, weight * tree.GetChildWeight(i), ref positionDelta, ref rotationSum);
            return;
        }
        if (playable is not AnimationMixerPlayable)
            return;
        float totalWeight = 0f;
        for (int i = 0; i < count; i++)
            if (playable.GetInput(i) is AnimationPlayable child && child.Output != null && child.Weight > 0f)
                totalWeight += child.Weight;
        if (totalWeight <= 0f)
            return;
        for (int i = 0; i < count; i++)
            if (playable.GetInput(i) is AnimationPlayable child && child.Output != null && child.Weight > 0f)
                AccumulateRootMotion(child, weight * child.Weight / totalWeight, ref positionDelta, ref rotationSum);
    }

    [HotPath]
    private void RegisterRootMotionPin(int index, bool rotation,
        in AnimationClipPlayable.TravelProjection projection, Transform? sourceParent)
    {
        if ((uint)index >= (uint)_rootMotionPinFlags.Length)
            return;
        if (_rootMotionPinFlags[index] == 0)
            _rootMotionPinIndices[_rootMotionPinCount++] = index;
        _rootMotionPinFlags[index] |= rotation ? (byte)3 : (byte)1;
        if (RootMotionPlanar)
        {
            // These shared accumulators are cleared after either pin path. For root motion they
            // hold body-space trajectory directions; in-place playback uses bone-local axes.
            ref TravelPinAccumulator accumulator = ref _travelPinAccumulators[index];
            for (int i = 0; i < projection.AxisCount; i++)
            {
                Float3 axis = projection.GetAxis(i);
                Float3 worldAxis = sourceParent != null ? sourceParent.TransformVector(axis) : axis;
                Float3 bodyAxis = Transform.InverseTransformVector(worldAxis);
                bodyAxis.Y = 0f;
                accumulator.AddAxis(bodyAxis);
            }
        }
    }

    [HotPath]
    private void PinRootMotionPose()
    {
        SkeletonBinding binding = _binding!;
        for (int i = 0; i < _rootMotionPinCount; i++)
        {
            int index = _rootMotionPinIndices[i];
            Transform? bone = binding.Bones[index];
            if (bone == null)
                continue;
            Float3 reference = binding.ReferencePose[index].LocalPosition;
            if (RootMotionPlanar)
            {
                Transform? parent = bone.Parent;
                Float3 localOffset = bone.LocalPosition - reference;
                Float3 worldOffset = parent != null ? parent.TransformVector(localOffset) : localOffset;
                Float3 bodyOffset = Transform.InverseTransformVector(worldOffset);
                _travelPinAccumulators[index].RemoveFrom(ref bodyOffset);
                Float3 worldResidual = Transform.TransformVector(bodyOffset);
                bone.LocalPosition = reference + (parent != null ? parent.InverseTransformVector(worldResidual) : worldResidual);
            }
            else
                bone.LocalPosition = reference;
        }
        // Change rotations after all position projections so a parent's pin cannot rotate the
        // coordinate system halfway through removing a child's translation.
        for (int i = 0; i < _rootMotionPinCount; i++)
        {
            int index = _rootMotionPinIndices[i];
            if ((_rootMotionPinFlags[index] & 2) != 0 && binding.Bones[index] is Transform bone)
                bone.LocalRotation = binding.ReferencePose[index].LocalRotation;
            _rootMotionPinFlags[index] = 0;
            _travelPinAccumulators[index].AxisCount = 0;
        }
        _rootMotionPinCount = 0;
    }
}
