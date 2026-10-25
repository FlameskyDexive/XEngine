// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.Animation;

/// <summary>
/// A single frame's worth of bone local transforms and blend-shape weights, indexed against a
/// <see cref="SkeletonBinding"/>. Owned by an <see cref="AnimationPlayable"/> node and reused every
/// frame — never re-allocated on the hot path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Blend model.</b> A mixer calls <see cref="Clear"/> (zeroes every entry and resets
/// <see cref="BonePose.Channels"/> to <see cref="PoseChannel.None"/>), then
/// <see cref="Accumulate"/> for each child with that child's normalized weight, then
/// <see cref="NormalizeRotations"/>. Unanimated bones (channels left <see cref="PoseChannel.None"/>)
/// are skipped on output so they keep their gameplay-written local transform.
/// </para>
/// <para>
/// <b>Zero allocation.</b> <see cref="Clear"/>/<see cref="Accumulate"/>/<see cref="NormalizeRotations"/>
/// use indexed <c>for</c> loops over the backing arrays — no enumerator, no LINQ, no temporaries
/// beyond stack struct copies. PR0002–PR0007 compliant.
/// </para>
/// </remarks>
public sealed class Pose
{
    /// <summary>Per-bone local TRS, indexed to match <see cref="SkeletonBinding"/>.</summary>
    public BonePose[] Bones;

    /// <summary>Per-blend-shape weight, indexed to match <see cref="SkeletonBinding"/>.</summary>
    public float[] BlendShapeWeights;

    public int BoneCount => Bones.Length;
    public int BlendShapeCount => BlendShapeWeights.Length;

    public Pose(int boneCount, int blendShapeCount)
    {
        Bones = new BonePose[boneCount];
        BlendShapeWeights = new float[blendShapeCount];
    }

    /// <summary>
    /// Resets every bone to "not driven" and zeroes all blend-shape weights. Call this on the
    /// mixer's output pose before accumulating children.
    /// </summary>
    public void Clear()
    {
        Array.Clear(Bones, 0, Bones.Length);
        Array.Clear(BlendShapeWeights, 0, BlendShapeWeights.Length);
    }

    /// <summary>
    /// Copies every bone (channels + values) and blend-shape weight from <paramref name="other"/>.
    /// Used by the layer mixer to seed its result with the base layer's pose before blending overlays.
    /// </summary>
    public void CopyFrom(Pose other)
    {
        BonePose[] src = other.Bones;
        BonePose[] dst = Bones;
        int n = dst.Length;
        if (src.Length < n) n = src.Length;
        for (int i = 0; i < n; i++)
            dst[i] = src[i];

        float[] srcW = other.BlendShapeWeights;
        float[] dstW = BlendShapeWeights;
        int m = dstW.Length;
        if (srcW.Length < m) m = srcW.Length;
        for (int j = 0; j < m; j++)
            dstW[j] = srcW[j];
    }

    /// <summary>
    /// Accumulates <paramref name="other"/> into this pose scaled by <paramref name="weight"/>,
    /// touching only the channels <paramref name="other"/> actually drives. Rotation is accumulated
    /// component-wise (weighted sum); call <see cref="NormalizeRotations"/> once after all children.
    /// </summary>
    public void Accumulate(Pose other, float weight)
    {
        BonePose[] dst = Bones;
        BonePose[] src = other.Bones;
        int n = dst.Length;
        for (int i = 0; i < n; i++)
        {
            ref BonePose d = ref dst[i];
            BonePose s = src[i];
            PoseChannel ch = s.Channels;
            if (ch == PoseChannel.None)
                continue;

            d.Channels |= ch;

            if ((ch & PoseChannel.Position) != 0)
            {
                d.LocalPosition.X += s.LocalPosition.X * weight;
                d.LocalPosition.Y += s.LocalPosition.Y * weight;
                d.LocalPosition.Z += s.LocalPosition.Z * weight;
            }

            if ((ch & PoseChannel.Rotation) != 0)
            {
                d.LocalRotation.X += s.LocalRotation.X * weight;
                d.LocalRotation.Y += s.LocalRotation.Y * weight;
                d.LocalRotation.Z += s.LocalRotation.Z * weight;
                d.LocalRotation.W += s.LocalRotation.W * weight;
            }

            if ((ch & PoseChannel.Scale) != 0)
            {
                d.LocalScale.X += s.LocalScale.X * weight;
                d.LocalScale.Y += s.LocalScale.Y * weight;
                d.LocalScale.Z += s.LocalScale.Z * weight;
            }
        }

        float[] dstW = BlendShapeWeights;
        float[] srcW = other.BlendShapeWeights;
        int m = dstW.Length;
        for (int j = 0; j < m; j++)
            dstW[j] += srcW[j] * weight;
    }

    /// <summary>
    /// Renormalizes every driven bone's rotation quaternion. Required once after the final
    /// <see cref="Accumulate"/> so the weighted-sum blend yields a unit quaternion.
    /// </summary>
    public void NormalizeRotations()
    {
        BonePose[] bones = Bones;
        int n = bones.Length;
        for (int i = 0; i < n; i++)
        {
            if ((bones[i].Channels & PoseChannel.Rotation) == 0)
                continue;

            ref Quaternion r = ref bones[i].LocalRotation;
            float len = Maths.Sqrt(r.X * r.X + r.Y * r.Y + r.Z * r.Z + r.W * r.W);
            if (len > 1e-6f)
            {
                float inv = 1.0f / len;
                r.X *= inv;
                r.Y *= inv;
                r.Z *= inv;
                r.W *= inv;
            }
            else
            {
                bones[i].LocalRotation = Quaternion.Identity;
            }
        }
    }
}
