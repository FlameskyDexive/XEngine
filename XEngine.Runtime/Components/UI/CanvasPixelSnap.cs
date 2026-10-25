// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.Runtime.UI;

/// <summary>
/// Pixel-perfect snap math shared by <see cref="GameCanvas"/> overlay rendering and host tests.
/// Design-space values map to whole framebuffer pixels at <c>round(design * scale) / scale</c>.
/// </summary>
public static class CanvasPixelSnap
{
    public static bool Applies(bool pixelPerfect, bool worldSpace)
        => pixelPerfect && !worldSpace;

    public static float Snap(float design, float scale)
    {
        float s = Maths.Max(scale, 0.001f);
        return Maths.Round(design * s) / s;
    }

    public static Float2 Snap(Float2 design, float scale)
        => new(Snap(design.X, scale), Snap(design.Y, scale));

    public static Rect SnapRect(Rect rect, float scale)
    {
        Float2 min = Snap(rect.Min, scale);
        Float2 max = Snap(rect.Max, scale);
        if (max.X < min.X) max.X = min.X;
        if (max.Y < min.Y) max.Y = min.Y;
        return new Rect(min.X, min.Y, max.X, max.Y);
    }
}
