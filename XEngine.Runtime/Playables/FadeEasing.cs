// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Playables;

/// <summary>
/// Easing curves applicable to weight fades. Selected as an enum (not a delegate) so the per-frame
/// evaluation in <see cref="Playable.UpdateFade"/> stays branch-direct and allocation-free (PR0002–PR0007).
/// Formulas follow easings.net. Overshooting curves (Back/Elastic) intentionally exceed [0,1] mid-fade;
/// the fade snaps to the exact target weight at completion.
/// </summary>
public enum FadeEasing : byte
{
    Linear = 0,
    QuadIn,
    QuadOut,
    QuadInOut,
    CubicIn,
    CubicOut,
    CubicInOut,
    QuarticIn,
    QuarticOut,
    QuarticInOut,
    QuinticIn,
    QuinticOut,
    QuinticInOut,
    SineIn,
    SineOut,
    SineInOut,
    ExpoIn,
    ExpoOut,
    ExpoInOut,
    CircIn,
    CircOut,
    CircInOut,
    BackIn,
    BackOut,
    BackInOut,
    BounceIn,
    BounceOut,
    BounceInOut,
    ElasticIn,
    ElasticOut,
    ElasticInOut,
}

/// <summary>Allocation-free evaluation of <see cref="FadeEasing"/> curves over normalized progress.</summary>
public static class FadeEasingMath
{
    /// <summary>Evaluates <paramref name="easing"/> at normalized progress <paramref name="t"/>.</summary>
    public static float Evaluate(FadeEasing easing, float t)
    {
        switch (easing)
        {
            case FadeEasing.Linear: return t;
            case FadeEasing.QuadIn: return t * t;
            case FadeEasing.QuadOut: return 1f - (1f - t) * (1f - t);
            case FadeEasing.QuadInOut: return t < 0.5f ? 2f * t * t : 1f - MathF.Pow(-2f * t + 2f, 2f) / 2f;
            case FadeEasing.CubicIn: return t * t * t;
            case FadeEasing.CubicOut: return 1f - MathF.Pow(1f - t, 3f);
            case FadeEasing.CubicInOut: return t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;
            case FadeEasing.QuarticIn: return t * t * t * t;
            case FadeEasing.QuarticOut: return 1f - MathF.Pow(1f - t, 4f);
            case FadeEasing.QuarticInOut: return t < 0.5f ? 8f * t * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 4f) / 2f;
            case FadeEasing.QuinticIn: return t * t * t * t * t;
            case FadeEasing.QuinticOut: return 1f - MathF.Pow(1f - t, 5f);
            case FadeEasing.QuinticInOut: return t < 0.5f ? 16f * t * t * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 5f) / 2f;
            case FadeEasing.SineIn: return 1f - MathF.Cos(t * MathF.PI / 2f);
            case FadeEasing.SineOut: return MathF.Sin(t * MathF.PI / 2f);
            case FadeEasing.SineInOut: return -(MathF.Cos(MathF.PI * t) - 1f) / 2f;
            case FadeEasing.ExpoIn: return t == 0f ? 0f : MathF.Pow(2f, 10f * t - 10f);
            case FadeEasing.ExpoOut: return t == 1f ? 1f : 1f - MathF.Pow(2f, -10f * t);
            case FadeEasing.ExpoInOut:
                if (t == 0f) return 0f;
                if (t == 1f) return 1f;
                return t < 0.5f ? MathF.Pow(2f, 20f * t - 10f) / 2f : (2f - MathF.Pow(2f, -20f * t + 10f)) / 2f;
            case FadeEasing.CircIn: return 1f - MathF.Sqrt(1f - t * t);
            case FadeEasing.CircOut: return MathF.Sqrt(1f - (t - 1f) * (t - 1f));
            case FadeEasing.CircInOut:
                return t < 0.5f
                    ? (1f - MathF.Sqrt(1f - MathF.Pow(2f * t, 2f))) / 2f
                    : (MathF.Sqrt(1f - MathF.Pow(-2f * t + 2f, 2f)) + 1f) / 2f;
            case FadeEasing.BackIn:
                {
                    const float c1 = 1.70158f;
                    const float c3 = c1 + 1f;
                    return c3 * t * t * t - c1 * t * t;
                }
            case FadeEasing.BackOut:
                {
                    const float c1 = 1.70158f;
                    const float c3 = c1 + 1f;
                    float u = t - 1f;
                    return 1f + c3 * u * u * u + c1 * u * u;
                }
            case FadeEasing.BackInOut:
                {
                    const float c1 = 1.70158f;
                    const float c2 = c1 * 1.525f;
                    return t < 0.5f
                        ? (MathF.Pow(2f * t, 2f) * ((c2 + 1f) * 2f * t - c2)) / 2f
                        : (MathF.Pow(2f * t - 2f, 2f) * ((c2 + 1f) * (t * 2f - 2f) + c2) + 2f) / 2f;
                }
            case FadeEasing.BounceOut:
                {
                    const float n1 = 7.5625f;
                    const float d1 = 2.75f;
                    if (t < 1f / d1) return n1 * t * t;
                    if (t < 2f / d1) { float u = t - 1.5f / d1; return n1 * u * u + 0.75f; }
                    if (t < 2.5f / d1) { float u = t - 2.25f / d1; return n1 * u * u + 0.9375f; }
                    float v = t - 2.625f / d1;
                    return n1 * v * v + 0.984375f;
                }
            case FadeEasing.BounceIn: return 1f - Evaluate(FadeEasing.BounceOut, 1f - t);
            case FadeEasing.BounceInOut:
                return t < 0.5f
                    ? (1f - Evaluate(FadeEasing.BounceOut, 1f - 2f * t)) / 2f
                    : (1f + Evaluate(FadeEasing.BounceOut, 2f * t - 1f)) / 2f;
            case FadeEasing.ElasticIn:
                {
                    if (t == 0f) return 0f;
                    if (t == 1f) return 1f;
                    const float c4 = 2f * MathF.PI / 3f;
                    return -MathF.Pow(2f, 10f * t - 10f) * MathF.Sin((t * 10f - 10.75f) * c4);
                }
            case FadeEasing.ElasticOut:
                {
                    if (t == 0f) return 0f;
                    if (t == 1f) return 1f;
                    const float c4 = 2f * MathF.PI / 3f;
                    return MathF.Pow(2f, -10f * t) * MathF.Sin((t * 10f - 0.75f) * c4) + 1f;
                }
            case FadeEasing.ElasticInOut:
                {
                    if (t == 0f) return 0f;
                    if (t == 1f) return 1f;
                    const float c5 = 2f * MathF.PI / 4.5f;
                    return t < 0.5f
                        ? -(MathF.Pow(2f, 20f * t - 10f) * MathF.Sin((20f * t - 11.125f) * c5)) / 2f
                        : (MathF.Pow(2f, -20f * t + 10f) * MathF.Sin((20f * t - 11.125f) * c5)) / 2f + 1f;
                }
            default: return t;
        }
    }
}
