// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.Runtime.UI;

/// <summary>
/// Shared native-size math for package Graphics and legacy UIImage. UI design pixels match
/// sprite/texture pixels; <c>pixelsPerUnitMultiplier</c> is a display scale, not world PPU.
/// </summary>
public static class GraphicNativeSize
{
    public static bool TryFromSprite(Sprite? sprite, float pixelsPerUnitMultiplier, out Float2 size)
    {
        size = default;
        if (sprite == null || sprite.Rect.Width <= 0 || sprite.Rect.Height <= 0)
            return false;

        float multiplier = Maths.Max(0.0001f, pixelsPerUnitMultiplier);
        size = new Float2(sprite.Rect.Width * multiplier, sprite.Rect.Height * multiplier);
        return true;
    }

    public static bool TryFromTexture(Texture2D? texture, Rect uvRect, out Float2 size)
    {
        size = default;
        if (texture == null || texture.Width == 0 || texture.Height == 0)
            return false;

        float width = texture.Width * MathF.Abs(uvRect.Size.X);
        float height = texture.Height * MathF.Abs(uvRect.Size.Y);
        if (width <= 0f || height <= 0f)
            return false;

        size = new Float2(width, height);
        return true;
    }

    public static void Apply(RectTransform rectTransform, Float2 size)
    {
        rectTransform.AnchorMax = rectTransform.AnchorMin;
        rectTransform.SizeDelta = size;
    }
}
