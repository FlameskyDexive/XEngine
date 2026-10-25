// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime;

namespace XEngine.Animation;

public static partial class AnimatorControllerCompiler
{
    /// <summary>
    /// Replaces motion clips on a compiled constant using <paramref name="ov"/>'s From→To map.
    /// BlendTrees are cloned so the authored <c>.motion</c> asset is not mutated.
    /// </summary>
    internal static void ApplyOverrides(AnimatorControllerConstant c, AnimatorOverrideController ov)
    {
        var clips = c.MotionClips;
        for (int i = 0; i < clips.Length; i++)
            clips[i] = RemapClip(clips[i], ov);

        var trees = c.MotionBlendTrees;
        for (int i = 0; i < trees.Length; i++)
        {
            if (trees[i] != null)
                trees[i] = CloneAndRemap(trees[i]!, ov);
        }
    }

    internal static AnimationClip? RemapClip(AnimationClip? clip, AnimatorOverrideController ov)
    {
        if (clip == null) return null;
        var list = ov.Overrides;
        for (int i = 0; i < list.Count; i++)
        {
            AnimationClip? from = list[i].From.Res;
            if (from == null) continue;
            bool match = ReferenceEquals(from, clip)
                || (from.AssetID != Guid.Empty && from.AssetID == clip.AssetID);
            if (!match) continue;
            return list[i].To.Res ?? clip;
        }
        return clip;
    }

    static BlendTree CloneAndRemap(BlendTree src, AnimatorOverrideController ov)
    {
        var dst = new BlendTree
        {
            Name = src.Name,
            Type = src.Type,
            ParameterName = src.ParameterName,
            ParameterNameY = src.ParameterNameY,
            UseAutomaticThresholds = src.UseAutomaticThresholds,
        };
        var children = src.Children;
        for (int i = 0; i < children.Count; i++)
        {
            BlendTree.Child c = children[i];
            BlendTree? nested = c.Tree.Res;
            dst.Children.Add(new BlendTree.Child
            {
                Clip = new AssetRef<AnimationClip>(RemapClip(c.Clip.Res, ov)),
                Tree = nested != null
                    ? new AssetRef<BlendTree>(CloneAndRemap(nested, ov))
                    : default,
                Threshold = c.Threshold,
                Position = c.Position,
                DirectParameterName = c.DirectParameterName,
            });
        }
        return dst;
    }
}
