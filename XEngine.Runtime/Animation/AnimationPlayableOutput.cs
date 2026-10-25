// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Playables;
using XEngine.Runtime;
using XEngine.Vector;
using XEngine;

namespace XEngine.Animation;

/// <summary>
/// Flushes the graph's animation output into the engine: writes each driven bone's local TRS to its
/// <see cref="Transform"/> and each driven blend-shape weight to its <see cref="SkinnedMeshRenderer"/>.
/// Driven channels are those the source pose flagged (see <see cref="BonePose.Channels"/>); bones no
/// clip touched this frame keep their existing local transform (so gameplay/parent-layer writes win).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="SkinnedMeshRenderer"/>'s <c>Transform.Version</c>-based dirty check picks up the
/// bone writes automatically and re-skins — this output never calls into the renderer directly.
/// </para>
/// <para>
/// Hot path: indexed loops over the binding arrays, no per-frame string lookups, no allocations.
/// </para>
/// </remarks>
public sealed class AnimationPlayableOutput : PlayableOutput
{
    /// <summary>The skeleton binding used to resolve bone indices to Transforms and blend-shape targets.</summary>
    public SkeletonBinding? Binding { get; set; }

    [HotPath]
    public override void Apply(PlayableContext ctx)
    {
        SkeletonBinding? binding = Binding;
        Pose? pose = (Source as AnimationPlayable)?.Output;
        if (binding == null || pose == null)
            return;

        BonePose[] srcBones = pose.Bones;
        Transform?[] dstBones = binding.Bones;
        int n = srcBones.Length;
        if (n > dstBones.Length) n = dstBones.Length; // defensive; should always match

        for (int i = 0; i < n; i++)
        {
            BonePose bp = srcBones[i];
            PoseChannel ch = bp.Channels;
            if (ch == PoseChannel.None)
                continue;

            Transform? t = dstBones[i];
            if (t == null)
                continue;

            if ((ch & PoseChannel.Position) != 0)
                t.LocalPosition = bp.LocalPosition;
            if ((ch & PoseChannel.Rotation) != 0)
                t.LocalRotation = bp.LocalRotation;
            if ((ch & PoseChannel.Scale) != 0)
                t.LocalScale = bp.LocalScale;
        }

        // Blend shapes.
        BlendShapeTarget[] targets = binding.BlendShapes;
        float[] weights = pose.BlendShapeWeights;
        int m = targets.Length;
        if (m > weights.Length) m = weights.Length;

        for (int j = 0; j < m; j++)
        {
            SkinnedMeshRenderer? smr = targets[j].Renderer;
            if (smr == null)
                continue;
            smr.SetBlendShapeWeight(targets[j].ShapeIndex, weights[j]);
        }
    }
}
