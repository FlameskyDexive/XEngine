// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Playables;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine;

namespace XEngine.Animation;

/// <summary>
/// Blends N <see cref="AnimationPlayable"/> layers into one output <see cref="Pose"/>. Layer 0 is the
/// Override base (copied verbatim); each subsequent layer combines per its
/// <see cref="AnimatorLayerBlendingMode"/> (Override lerps toward the layer's pose, Additive adds the
/// layer's delta), scaled by the layer weight and a per-bone <see cref="AvatarMask"/> weight array.
/// </summary>
/// <remarks>
/// <para>
/// Mask weight arrays are pre-resolved from <see cref="AvatarMask"/> at add time against the binding
/// (path → bone index, with subtree propagation), so the per-frame blend is pure indexed math.
/// </para>
/// <para>
/// <b>Additive convention.</b> An additive layer's clip should be authored as a delta from the
/// reference pose (e.g. a breathing clip with small offsets). Position is added; rotation is composed
/// as <c>base * Slerp(Identity, layerRot, w)</c>; scale is multiplied around 1. Without a
/// <see cref="SetAdditiveReferencePose"/> the reference is identity/zero (historical behaviour); with
/// one (M51, Mecanim "additive reference pose" semantics) the layer contributes the delta
/// <c>pose − reference</c> per bone channel, so the same clip can be authored on top of a non-neutral
/// stance.
/// </para>
/// </remarks>
public sealed class AnimationLayerMixerPlayable : AnimationPlayable
{
    private readonly List<LayerInput> _layers = new();

    public override int InputCount => _layers.Count;
    public override Playable GetInput(int index) => _layers[index].Source;

    /// <summary>Sets the mixer weight for layer <paramref name="index"/>. No-op if out of range.</summary>
    public void SetLayerWeight(int index, float weight)
    {
        if ((uint)index >= (uint)_layers.Count)
            return;
        LayerInput layer = _layers[index];
        layer.Weight = weight;
        _layers[index] = layer;
    }

    /// <summary>Current mixer weight for layer <paramref name="index"/>, or 0 if out of range.</summary>
    public float GetLayerWeight(int index)
    {
        if ((uint)index >= (uint)_layers.Count)
            return 0f;
        return _layers[index].Weight;
    }

    /// <summary>
    /// M51 — sets the per-bone additive reference pose for <paramref name="layerIndex"/> (aligned to
    /// the mixer's binding, e.g. from <see cref="BuildAdditiveReferencePose"/>). Null restores the
    /// identity/zero reference (historical additive behaviour).
    /// </summary>
    public void SetAdditiveReferencePose(int layerIndex, BonePose[]? referencePose)
    {
        if ((uint)layerIndex >= (uint)_layers.Count)
            return;
        LayerInput layer = _layers[layerIndex];
        layer.ReferencePose = referencePose;
        _layers[layerIndex] = layer;
    }

    /// <summary>
    /// M51 — samples <paramref name="clip"/>'s bone channels at <paramref name="time"/> against
    /// <paramref name="binding"/>, producing the per-bone reference pose array for
    /// <see cref="SetAdditiveReferencePose"/> (Mecanim uses the reference clip's first frame).
    /// </summary>
    public static BonePose[] BuildAdditiveReferencePose(AnimationClip clip, SkeletonBinding binding, double time = 0d)
    {
        var reference = new BonePose[binding.BoneCount];
        var bones = clip.Bones;
        for (int i = 0; i < bones.Count; i++)
        {
            AnimationClip.AnimBone cb = bones[i];
            int idx = binding.IndexOfBonePath(cb.BoneName);
            if (idx < 0)
                continue;
            PoseChannel ch = PoseChannel.None;
            if (cb.PosX != null)
            {
                reference[idx].LocalPosition = cb.EvaluatePositionAt((float)time);
                ch |= PoseChannel.Position;
            }
            if (cb.RotX != null)
            {
                reference[idx].LocalRotation = cb.EvaluateRotationAt((float)time);
                ch |= PoseChannel.Rotation;
            }
            reference[idx].Channels = ch;
        }
        return reference;
    }

    /// <summary>Allocates the output pose against <paramref name="binding"/>. Add layers via <see cref="AddLayer"/>.</summary>
    public void Bind(SkeletonBinding binding)
    {
        Binding = binding;
        AllocateOutput();
    }

    /// <summary>
    /// Appends a layer. <paramref name="boneWeights"/> is the pre-resolved mask (from
    /// <see cref="AvatarMask.BuildBoneWeights"/>) or <c>null</c> for the full body.
    /// </summary>
    public void AddLayer(AnimationPlayable source, float weight, AnimatorLayerBlendingMode mode, float[]? boneWeights)
    {
        _layers.Add(new LayerInput
        {
            Source = source,
            Weight = weight,
            Mode = mode,
            BoneWeights = boneWeights,
        });
    }

    [HotPath]
    public override void ProcessFrame(PlayableContext ctx)
    {
        Pose? pose = Output;
        if (pose == null)
            return;
        pose.Clear();

        int n = _layers.Count;
        if (n == 0)
            return;

        // Layer 0 is the Override base: copy its pose verbatim.
        Pose? basePose = _layers[0].Source.Output;
        if (basePose != null)
            pose.CopyFrom(basePose);

        // Blend subsequent layers over the accumulated result.
        BonePose[] dstBones = pose.Bones;
        int boneCount = dstBones.Length;
        for (int li = 1; li < n; li++)
        {
            LayerInput layer = _layers[li];
            Pose? lp = layer.Source.Output;
            if (lp == null)
                continue;

            float lw = layer.Weight;
            if (lw <= 0f)
                continue;

            float[]? mask = layer.BoneWeights;
            bool additive = layer.Mode == AnimatorLayerBlendingMode.Additive;
            BonePose[] srcBones = lp.Bones;

            for (int bi = 0; bi < boneCount; bi++)
            {
                BonePose src = srcBones[bi];
                PoseChannel srcCh = src.Channels;
                if (srcCh == PoseChannel.None)
                    continue;

                float w = lw * (mask != null ? mask[bi] : 1f);
                if (w <= 0f)
                    continue;

                ref BonePose dst = ref dstBones[bi];
                PoseChannel dstCh = dst.Channels;
                dst.Channels = dstCh | srcCh;

                if (additive)
                {
                    BonePose[]? reference = layer.ReferencePose;
                    if ((srcCh & PoseChannel.Position) != 0)
                    {
                        float rx = 0f, ry = 0f, rz = 0f;
                        if (reference != null)
                        {
                            BonePose r = reference[bi];
                            rx = r.LocalPosition.X; ry = r.LocalPosition.Y; rz = r.LocalPosition.Z;
                        }
                        dst.LocalPosition.X += (src.LocalPosition.X - rx) * w;
                        dst.LocalPosition.Y += (src.LocalPosition.Y - ry) * w;
                        dst.LocalPosition.Z += (src.LocalPosition.Z - rz) * w;
                    }
                    if ((srcCh & PoseChannel.Rotation) != 0)
                    {
                        Quaternion delta = src.LocalRotation;
                        if (reference != null && (reference[bi].Channels & PoseChannel.Rotation) != 0)
                            delta = Quaternion.Inverse(reference[bi].LocalRotation) * src.LocalRotation;
                        dst.LocalRotation = dst.LocalRotation * Quaternion.Slerp(Quaternion.Identity, delta, w);
                    }
                    if ((srcCh & PoseChannel.Scale) != 0)
                    {
                        float sx = src.LocalScale.X, sy = src.LocalScale.Y, sz = src.LocalScale.Z;
                        if (reference != null)
                        {
                            BonePose r = reference[bi];
                            sx = MathF.Abs(r.LocalScale.X) > 1e-7f ? sx / r.LocalScale.X : sx;
                            sy = MathF.Abs(r.LocalScale.Y) > 1e-7f ? sy / r.LocalScale.Y : sy;
                            sz = MathF.Abs(r.LocalScale.Z) > 1e-7f ? sz / r.LocalScale.Z : sz;
                        }
                        dst.LocalScale.X *= 1f + (sx - 1f) * w;
                        dst.LocalScale.Y *= 1f + (sy - 1f) * w;
                        dst.LocalScale.Z *= 1f + (sz - 1f) * w;
                    }
                }
                else // Override.
                {
                    if ((srcCh & PoseChannel.Position) != 0)
                    {
                        if ((dstCh & PoseChannel.Position) != 0)
                        {
                            dst.LocalPosition.X += (src.LocalPosition.X - dst.LocalPosition.X) * w;
                            dst.LocalPosition.Y += (src.LocalPosition.Y - dst.LocalPosition.Y) * w;
                            dst.LocalPosition.Z += (src.LocalPosition.Z - dst.LocalPosition.Z) * w;
                        }
                        else
                            dst.LocalPosition = src.LocalPosition; // base didn't drive; layer takes over.
                    }
                    if ((srcCh & PoseChannel.Rotation) != 0)
                    {
                        if ((dstCh & PoseChannel.Rotation) != 0)
                            dst.LocalRotation = Quaternion.Slerp(dst.LocalRotation, src.LocalRotation, w);
                        else
                            dst.LocalRotation = src.LocalRotation;
                    }
                    if ((srcCh & PoseChannel.Scale) != 0)
                    {
                        if ((dstCh & PoseChannel.Scale) != 0)
                        {
                            dst.LocalScale.X += (src.LocalScale.X - dst.LocalScale.X) * w;
                            dst.LocalScale.Y += (src.LocalScale.Y - dst.LocalScale.Y) * w;
                            dst.LocalScale.Z += (src.LocalScale.Z - dst.LocalScale.Z) * w;
                        }
                        else
                            dst.LocalScale = src.LocalScale;
                    }
                }
            }

            // Blend shapes accumulate additively across layers (masks are bone-based; shapes aren't masked).
            float[] dstW = pose.BlendShapeWeights;
            float[] srcW = lp.BlendShapeWeights;
            int m = dstW.Length;
            if (srcW.Length < m) m = srcW.Length;
            for (int j = 0; j < m; j++)
                dstW[j] += srcW[j] * lw;
        }
    }

    private struct LayerInput
    {
        public AnimationPlayable Source;
        public float Weight;
        public AnimatorLayerBlendingMode Mode;
        public float[]? BoneWeights; // null = full body (all 1).
        public BonePose[]? ReferencePose; // M51 — additive reference (null = identity/zero, historical).
    }
}
