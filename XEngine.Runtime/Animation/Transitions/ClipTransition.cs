// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Animation;

/// <summary>
/// Plays one <see cref="AnimationClip"/> with fade/speed/start-time settings — the Animancer
/// <c>ClipTransition</c> equivalent, serializable as a field or via <see cref="TransitionAsset"/>.
/// </summary>
public class ClipTransition : AnimationTransition
{
    /// <summary>The clip to play. Must be resident when <c>Animator.Play</c> creates the state.</summary>
    public AssetRef<AnimationClip> Clip = default;

    /// <summary>When true, forces loop wrapping regardless of the clip's authored wrap mode.</summary>
    public bool Loop;

    /// <summary>True when the referenced clip is loaded (a transition whose clip is still streaming
    /// cannot create a state; pre-load via the asset pipeline like controller motions).</summary>
    public bool IsValid => Clip.Res != null;

    public override bool IsLooping => Loop
        || (Clip.Res != null && Clip.Res.Wrap == AnimationWrapMode.Loop);

    public override float MaximumDuration => (float)(Clip.Res?.Duration ?? 0d);

    public override AnimationPlayable CreateState(Animator animator)
        => animator.CreateTransitionClipState(this);
}
