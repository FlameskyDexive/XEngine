using System;
using XEngine.Runtime;

namespace XEngine.Animation.Humanoid;

/// <summary>
/// A clip state initially configured for muscle-space retargeting. Sampling lives in a
/// replaceable cache so an imported Avatar/clip can change kind without replacing graph nodes.
/// </summary>
public sealed class HumanoidClipPlayable : AnimationClipPlayable
{
    /// <summary>Swap left/right muscle roles and mirror Hips translation; rebind after changing.</summary>
    public bool Mirror;

    public override void Bind(SkeletonBinding binding)
    {
        HumanoidMirror = Mirror;
        base.Bind(binding);
    }

    public HumanoidClipPlayable(AnimationClip clip, Avatar avatar) : base(clip)
    {
        if (!avatar.IsHumanoid)
            throw new ArgumentException("Humanoid playback requires a Humanoid Avatar.", nameof(avatar));
        ConfigureHumanoidSampler(avatar);
    }
}
