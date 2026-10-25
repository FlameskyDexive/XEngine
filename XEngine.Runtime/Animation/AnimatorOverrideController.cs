// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.Animation;

/// <summary>
/// ★ M44 — A clip-swap overlay on a base <see cref="AnimatorController"/>. Assign this asset to
/// <see cref="Animator.Controller"/>; compile remaps the motion table without mutating authored
/// BlendTrees. States/parameters live on <see cref="Base"/>, not on this overlay.
/// </summary>
[CreateAssetMenu("Animation/Animator Override Controller", Extension = ".overridecontroller", Order = 93)]
public sealed class AnimatorOverrideController : AnimatorController
{
    /// <summary>The controller whose motion table is remapped.</summary>
    public AssetRef<AnimatorController> Base;

    /// <summary>Clip pairs: play <see cref="ClipOverride.To"/> wherever the base uses <see cref="ClipOverride.From"/>.</summary>
    public List<ClipOverride> Overrides = new();

    /// <summary>One From→To clip substitution.</summary>
    public class ClipOverride
    {
        public AssetRef<AnimationClip> From;
        public AssetRef<AnimationClip> To;
    }

    /// <inheritdoc />
    public override List<AssetRef<AnimationClip>> GetClipReferences()
    {
        AnimatorController? src = Base.Res;
        var refs = src != null ? src.GetClipReferences() : base.GetClipReferences();
        var list = Overrides;
        for (int i = 0; i < list.Count; i++)
            refs.Add(list[i].To);
        return refs;
    }
}
