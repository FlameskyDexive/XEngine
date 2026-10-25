// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Clay.Importer;
using XEngine.Clay.Internal.Intermediate;
using XEngine.Vector;

namespace XEngine.Clay.PostProcess;

/// <summary>
/// Detects bind matrices that disagree with the (already coordinate-converted) node chain and
/// rebuilds them from the chain. Ripped/re-exported FBX files occasionally carry bind poses in
/// a different axis convention than the skeleton transforms: every bone's
/// <c>boneWorld * inverseBind</c> then resolves to a large (~180°, axis-family dependent)
/// rotation instead of identity, which turns the bind pose inside-out — the model renders
/// upside-down/squashed in every view that doesn't play an animation over it (the ZZZ
/// character exports hit this; playing Idle masked it because the curves are in the correct
/// space). When a strong majority of bones disagrees by large rotations, the node chain is by
/// definition the space the (converted) mesh vertices live in, so the chain is the truth and
/// the bind matrices are rebuilt as <c>inverse(boneWorld)</c>.
/// </summary>
internal sealed class BindPoseConsistencyStep : IPostProcess
{
    public PostProcessFlags Flag => PostProcessFlags.ValidateBindPoses;
    public string Name => "ValidateBindPoses";

    private const float DisagreementAngleDeg = 30f;
    private const float MajorityRatio = 0.6f;

    public void Execute(IntermediateScene scene, ImportContext context)
    {
        if (scene.Skins.Count == 0) return;

        // Node → world matrix, computed lazily from the (already coordinate-converted) chain.
        var worldOf = new Dictionary<IntermediateNode, Float4x4>();
        foreach (var skin in scene.Skins)
        {
            int boneCount = Math.Min(skin.BoneNodes.Count, skin.InverseBindPoses.Count);
            if (boneCount == 0) continue;

            // Rotation angle of net_i = boneWorld_i * InvBind_i; a consistent bind yields ~identity.
            // The wrong rotations need not share one axis — an axis-convention mismatch in the
            // source distributes the 180° error across two diagonal axis families depending on
            // each bone's parent orientation. The majority-angle test is the trigger; rebuilding
            // is always safe because a bind pose is by definition the node chain's reference
            // pose in the (converted) vertex space.
            var angles = new float[boneCount];
            var boneWorld = new Float4x4[boneCount];
            int disagree = 0;
            for (int b = 0; b < boneCount; b++)
            {
                var node = skin.BoneNodes[b];
                boneWorld[b] = WorldOf(node, worldOf);
                angles[b] = RotationAngleDeg(Quaternion.FromMatrix(boneWorld[b] * skin.InverseBindPoses[b]));
                if (angles[b] > DisagreementAngleDeg) disagree++;
            }
            if (disagree < MajorityRatio * boneCount) continue;

            // Rebuild from the node chain.
            for (int b = 0; b < boneCount; b++)
                skin.InverseBindPoses[b] = boneWorld[b].Invert();
            System.Array.Sort(angles);
            context.Log.Warning(
                $"Rebuilt {boneCount} bind matrices from the node chain: " +
                $"{disagree}/{boneCount} bones disagreed with the chain by large rotations " +
                $"(median ~{angles[boneCount / 2]:F0}°; bind-pose axis convention mismatch in the source file).",
                Name);
        }
    }

    /// <summary>World matrix of an intermediate node, memoized per run (parents resolve first).</summary>
    private static Float4x4 WorldOf(IntermediateNode node, Dictionary<IntermediateNode, Float4x4> memo)
    {
        if (memo.TryGetValue(node, out Float4x4 cached)) return cached;
        Float4x4 local = Float4x4.CreateTRS(node.LocalPosition, node.LocalRotation, node.LocalScale);
        Float4x4 result = node.Parent != null ? WorldOf(node.Parent, memo) * local : local;
        memo[node] = result;
        return result;
    }

    private static float RotationAngleDeg(Quaternion q)
    {
        float w = MathF.Min(1f, MathF.Abs(q.W));
        return 2f * MathF.Acos(w) * 180f / MathF.PI;
    }
}
