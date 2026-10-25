// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// Stretches a <see cref="RectTransform"/> to its parent and insets it by platform safe-area
/// padding. Insets are physical framebuffer pixels; divide by the canvas scale factor to get
/// layout units. +Y is up, so <see cref="WindowSafeAreaInsets.Bottom"/> pads the lower edge.
/// </summary>
public static class SafeAreaLayout
{
    /// <summary>Test hook. When set, fitters use these insets instead of <see cref="Window.SafeAreaInsets"/>.</summary>
    public static WindowSafeAreaInsets? InsetsOverride { get; set; }

    public static WindowSafeAreaInsets ResolveInsets()
        => InsetsOverride ?? Window.SafeAreaInsets;

    public static void Apply(RectTransform rectTransform, WindowSafeAreaInsets insets, float scaleFactor,
        bool conformX, bool conformY)
    {
        float scale = Maths.Max(scaleFactor, 0.001f);
        float left = conformX ? insets.Left / scale : 0f;
        float right = conformX ? insets.Right / scale : 0f;
        float top = conformY ? insets.Top / scale : 0f;
        float bottom = conformY ? insets.Bottom / scale : 0f;

        rectTransform.AnchorMin = Float2.Zero;
        rectTransform.AnchorMax = Float2.One;
        rectTransform.SizeDelta = new Float2(-left - right, -bottom - top);
        Float2 pivot = rectTransform.Pivot;
        rectTransform.AnchoredPosition = new Float2(
            left * (1f - pivot.X) - pivot.X * right,
            bottom * (1f - pivot.Y) - pivot.Y * top);
    }
}
