// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Playables;

namespace XEngine.Animation;

/// <summary>
/// Base for every playable node that produces a <see cref="Pose"/> — the animation specialization of
/// <see cref="Playable"/>. Owns one pooled <see cref="Output"/> pose sized to the
/// <see cref="SkeletonBinding"/>, which leaf nodes sample into and mixers blend into.
/// </summary>
/// <remarks>
/// Subclasses: <see cref="AnimationClipPlayable"/> (leaf, samples a clip) and
/// <see cref="AnimationMixerPlayable"/> (blends N children). Future: blend-tree, layer mixer.
/// </remarks>
public abstract class AnimationPlayable : Playable
{
    /// <summary>The skeleton this node's output pose is sized against. Set before <see cref="AllocateOutput"/>.</summary>
    public SkeletonBinding? Binding { get; protected set; }

    /// <summary>The pose this node produced this frame, or <c>null</c> until allocated.</summary>
    public Pose? Output { get; protected set; }

    /// <summary>Allocates (or re-allocates) <see cref="Output"/> against the current <see cref="Binding"/>.</summary>
    protected void AllocateOutput()
    {
        if (Binding != null)
            Output = new Pose(Binding.BoneCount, Binding.BlendShapeCount);
        else
            Output = null;
    }
}
