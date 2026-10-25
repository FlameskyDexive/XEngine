// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Playables;
using XEngine.Runtime;

namespace XEngine.Animation;

/// <summary>
/// A serializable "what to play and how" description (Animancer-style): the concrete data behind a
/// crossfade — fade duration/mode/easing, playback speed, normalized start time — plus the ability to
/// create and configure the playable state it describes. Transitions are plain data objects that can
/// live as fields on components or be wrapped by a <see cref="TransitionAsset"/> on disk.
/// </summary>
public interface ITransition
{
    /// <summary>Fade duration in seconds; non-positive snaps to full weight.</summary>
    float FadeDuration { get; }

    /// <summary>How <see cref="FadeDuration"/> is interpreted (fixed vs normalized, speed vs duration).</summary>
    FadeMode FadeMode { get; }

    /// <summary>Easing curve applied to the fade progress (Linear = historical behaviour).</summary>
    FadeEasing FadeEasing { get; }

    /// <summary>Playback speed applied when playing; 0 or NaN leaves the state's current speed.</summary>
    float Speed { get; }

    /// <summary>
    /// Normalized start time applied on every play. <c>float.NaN</c> leaves the state's current time
    /// untouched (continue / whatever the state was last left at).
    /// </summary>
    float NormalizedStartTime { get; }

    /// <summary>Whether the underlying motion loops (informational, for end-event scheduling).</summary>
    bool IsLooping { get; }

    /// <summary>Longest duration among the motions this transition can play, in seconds.</summary>
    float MaximumDuration { get; }

    /// <summary>
    /// Stable identity under which <c>Animator.Play(ITransition)</c> caches the state. The transition
    /// object itself by default; <see cref="TransitionAsset"/> play uses the asset so every reference
    /// to one asset shares a single state.
    /// </summary>
    object Key { get; }

    /// <summary>Creates (unparented from any cache) the playable state for this transition.</summary>
    AnimationPlayable CreateState(Animator animator);

    /// <summary>Applies speed / start time / loop overrides onto a state created for this transition.</summary>
    void Apply(AnimationPlayable state, Animator animator);
}
