// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Playables;
using XEngine.Runtime;

namespace XEngine.Animation;

/// <summary>
/// M49 — one serializable timed event on a transition: a normalized time plus a name that routes to
/// the Animator's named-handler table (<c>Animator.Events</c>). Object-reference-free by design so it
/// survives Echo round-trips on components and assets without delegate serialization.
/// </summary>
public class TransitionEvent
{
    /// <summary>Normalized time in [0,1] when the event fires.</summary>
    public float NormalizedTime;

    /// <summary>Handler name routed through <c>Animator.Events</c> at flush time.</summary>
    public string Name = "";
}

/// <summary>
/// Common serializable fields for every transition (Animancer <c>Transition&lt;TState&gt;</c> equivalent).
/// Concrete subclasses add their motion payload and implement <see cref="CreateState"/>.
/// </summary>
public abstract class AnimationTransition : ITransition
{
    /// <summary>Fade duration in seconds; non-positive snaps to full weight.</summary>
    public float FadeDuration = 0.25f;

    /// <summary>How <see cref="FadeDuration"/> is interpreted.</summary>
    public FadeMode FadeMode = FadeMode.FixedDuration;

    /// <summary>Easing curve applied to the fade progress.</summary>
    public FadeEasing FadeEasing = FadeEasing.Linear;

    /// <summary>Playback speed applied when playing; 0 or NaN leaves the state's current speed.</summary>
    public float Speed = 1f;

    /// <summary>Normalized start time applied on every play; NaN leaves the state's current time.</summary>
    public float NormalizedStartTime = float.NaN;

    /// <summary>
    /// M49 — named timed events applied to the played state on every <c>Animator.Play(transition)</c>
    /// (replacing the state's previous transition events). Bind handlers via <c>Animator.Events.Add</c>.
    /// </summary>
    public List<TransitionEvent> Events = new();

    /// <summary>Whether the underlying motion loops.</summary>
    public abstract bool IsLooping { get; }

    /// <summary>Longest duration among the motions this transition can play, in seconds.</summary>
    public abstract float MaximumDuration { get; }

    /// <summary>State-cache identity: the transition object itself.</summary>
    public object Key => this;

    float ITransition.FadeDuration => FadeDuration;
    FadeMode ITransition.FadeMode => FadeMode;
    FadeEasing ITransition.FadeEasing => FadeEasing;
    float ITransition.Speed => Speed;
    float ITransition.NormalizedStartTime => NormalizedStartTime;

    /// <summary>Creates the playable state for this transition on <paramref name="animator"/>'s base layer.</summary>
    public abstract AnimationPlayable CreateState(Animator animator);

    /// <summary>Applies speed and normalized start time; subclasses add their own overrides.</summary>
    public virtual void Apply(AnimationPlayable state, Animator animator)
    {
        if (Speed != 0f && !float.IsNaN(Speed))
            state.Speed = Speed;
        if (!float.IsNaN(NormalizedStartTime) && state.Duration > 0d)
            state.Time = (double)NormalizedStartTime * state.Duration;
    }
}
