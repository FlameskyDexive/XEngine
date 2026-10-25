// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.XUI.Core;

/// <summary>
/// The easing curves a <c>&lt;Anim&gt;</c> can name.
/// </summary>
/// <remarks>
/// A deliberately short list. UI motion needs "slows into place" far more than it needs a catalogue,
/// and every extra name is one more thing an author has to choose between with no way to preview the
/// difference from a document.
/// </remarks>
public enum XuiEase
{
    /// <summary>No easing. For a snap, or when something else is doing the shaping.</summary>
    Linear,

    /// <summary>Slows into place. The default, and the right answer for most UI.</summary>
    OutQuad,

    /// <summary>Starts slowly. For something leaving.</summary>
    InQuad,

    /// <summary>Eases at both ends. For a longer move where the middle should feel fast.</summary>
    InOutQuad,

    /// <summary>Overshoots slightly and settles. For a pop that should feel physical.</summary>
    OutBack,
}

/// <summary>Evaluates <see cref="XuiEase"/>. Pure BCL, so lint and the runtime agree on the names.</summary>
public static class XuiEasing
{
    /// <summary>Overshoot amount for <see cref="XuiEase.OutBack"/>; the conventional constant.</summary>
    private const float BackOvershoot = 1.70158f;

    /// <summary>Maps normalised time 0..1 through a curve. Input outside the range is clamped.</summary>
    public static float Evaluate(XuiEase ease, float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;

        switch (ease)
        {
            case XuiEase.Linear:
                return t;
            case XuiEase.InQuad:
                return t * t;
            case XuiEase.InOutQuad:
                return t < 0.5f ? 2f * t * t : 1f - (-2f * t + 2f) * (-2f * t + 2f) / 2f;
            case XuiEase.OutBack:
            {
                float inverted = t - 1f;
                return 1f + (BackOvershoot + 1f) * inverted * inverted * inverted
                     + BackOvershoot * inverted * inverted;
            }
            case XuiEase.OutQuad:
            default:
                return 1f - (1f - t) * (1f - t);
        }
    }

    public static bool TryParse(string text, out XuiEase ease)
    {
        switch (text)
        {
            case "linear": ease = XuiEase.Linear; return true;
            case "out-quad": ease = XuiEase.OutQuad; return true;
            case "in-quad": ease = XuiEase.InQuad; return true;
            case "in-out-quad": ease = XuiEase.InOutQuad; return true;
            case "out-back": ease = XuiEase.OutBack; return true;
            default: ease = XuiEase.OutQuad; return false;
        }
    }
}
