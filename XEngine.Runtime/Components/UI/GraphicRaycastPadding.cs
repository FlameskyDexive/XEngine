// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// Expands or shrinks a Graphic's pointer hit rectangle. Padding is
/// (left, bottom, right, top) in canvas design pixels; positive values grow the hit area.
/// </summary>
public static class GraphicRaycastPadding
{
    public static bool ContainsLocalPoint(Float2 size, Float2 pivot, Float4 padding, Float2 local)
    {
        float minX = -pivot.X * size.X - padding.X;
        float maxX = (1f - pivot.X) * size.X + padding.Z;
        float minY = -pivot.Y * size.Y - padding.Y;
        float maxY = (1f - pivot.Y) * size.Y + padding.W;
        if (maxX <= minX || maxY <= minY) return false;
        return local.X >= minX && local.X <= maxX && local.Y >= minY && local.Y <= maxY;
    }
}
