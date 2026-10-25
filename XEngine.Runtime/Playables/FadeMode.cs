// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Playables;

/// <summary>
/// How a weight fade interprets its duration (Animancer-compatible semantics). The default
/// <see cref="FixedDuration"/> matches the historical linear <see cref="Playable.StartFade"/> behaviour,
/// so existing callers are unaffected.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="FixedDuration"/> — the fade always takes exactly <c>duration</c> seconds, regardless
/// of the node's current weight (fade speed = distance / duration).</item>
/// <item><see cref="FixedSpeed"/> — the fade speed is always 1/<c>duration</c> weight units per second,
/// so fading from half weight finishes in half the time.</item>
/// <item><see cref="FromStart"/> — like <see cref="FixedDuration"/>, but when the same motion is already
/// playing at a meaningful weight, a weightless clone fades in from time zero while the old instance
/// fades out (restart-without-snap). Clone management lives in <c>Animator.Play</c>; a bare
/// <see cref="Playable.StartFade"/> treats it as <see cref="FixedDuration"/>.</item>
/// <item><see cref="NormalizedFixedDuration"/> / <see cref="NormalizedFixedSpeed"/> /
/// <see cref="NormalizedFromStart"/> — the duration is multiplied by the node's
/// <see cref="Playable.Duration"/> first (e.g. 0.5 = fade over half the clip), falling back to the raw
/// duration for indefinite nodes.</item>
/// </list>
/// </remarks>
public enum FadeMode : byte
{
    /// <summary>Fade completes in exactly <c>duration</c> seconds (historical default).</summary>
    FixedDuration = 0,

    /// <summary>Fade advances at 1/<c>duration</c> weight per second regardless of the starting weight.</summary>
    FixedSpeed = 1,

    /// <summary>Fixed-duration fade that restarts an already-active motion via a weightless clone.</summary>
    FromStart = 2,

    /// <summary><see cref="FixedDuration"/> with the duration scaled by the node's length.</summary>
    NormalizedFixedDuration = 3,

    /// <summary><see cref="FixedSpeed"/> with the duration scaled by the node's length.</summary>
    NormalizedFixedSpeed = 4,

    /// <summary><see cref="FromStart"/> with the duration scaled by the node's length.</summary>
    NormalizedFromStart = 5,
}

/// <summary>Classification helpers over <see cref="FadeMode"/> values.</summary>
public static class FadeModes
{
    /// <summary>True for the FromStart variants (restart semantics handled by <c>Animator</c>).</summary>
    public static bool IsFromStart(FadeMode mode)
        => mode is FadeMode.FromStart or FadeMode.NormalizedFromStart;

    /// <summary>The pure timing mode with the restart flag stripped.</summary>
    public static FadeMode TimingOf(FadeMode mode) => mode switch
    {
        FadeMode.FromStart => FadeMode.FixedDuration,
        FadeMode.NormalizedFromStart => FadeMode.NormalizedFixedDuration,
        _ => mode,
    };
}
