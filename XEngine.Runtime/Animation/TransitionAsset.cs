// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;

namespace XEngine.Animation;

/// <summary>
/// An on-disk asset (<c>.transition</c>) wrapping a polymorphic <see cref="AnimationTransition"/>
/// payload — the Animancer <c>TransitionAsset</c> equivalent. Every <c>Animator.Play(asset)</c> of the
/// same asset shares one cached state keyed by the asset.
/// </summary>
[CreateAssetMenu("Animation/Transition", Extension = ".transition", Order = 91)]
public class TransitionAsset : EngineObject
{
    /// <summary>
    /// The transition payload. Serialized with the Echo <c>$type</c> envelope so concrete subclasses
    /// (<see cref="ClipTransition"/>, mixer transitions, ...) survive the round trip.
    /// </summary>
    public AnimationTransition? Transition;
}
