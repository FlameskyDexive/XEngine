// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Echo;

namespace XEngine.Runtime;

/// <summary>Fill and angle-range edge ids for a SpriteShape. Sprites stay as string ids so bake is headless.</summary>
[CreateAssetMenu("2D/Sprite Shape Profile", Extension = ".spriteshape", Order = 80)]
public sealed class SpriteShapeProfile : ScriptableObject
{
    public string FillSpriteId = "";
    public List<SpriteShapeAngleRange> AngleRanges = [];

    public string? ResolveEdge(float angleDegrees)
    {
        float angle = WrapDegrees(angleDegrees);
        for (int i = 0; i < AngleRanges.Count; i++)
        {
            SpriteShapeAngleRange range = AngleRanges[i];
            if (ContainsAngle(angle, WrapDegrees(range.Start), WrapDegrees(range.End)))
                return string.IsNullOrEmpty(range.EdgeSpriteId) ? null : range.EdgeSpriteId;
        }

        return null;
    }

    public string? ResolveCorner(float angleDegrees)
    {
        float angle = WrapDegrees(angleDegrees);
        for (int i = 0; i < AngleRanges.Count; i++)
        {
            SpriteShapeAngleRange range = AngleRanges[i];
            if (ContainsAngle(angle, WrapDegrees(range.Start), WrapDegrees(range.End)))
                return string.IsNullOrEmpty(range.CornerSpriteId) ? null : range.CornerSpriteId;
        }

        return null;
    }

    private static bool ContainsAngle(float angle, float start, float end)
    {
        if (start <= end)
            return angle >= start && angle <= end;
        return angle >= start || angle <= end;
    }

    private static float WrapDegrees(float degrees)
    {
        float wrapped = degrees % 360f;
        if (wrapped > 180f)
            wrapped -= 360f;
        else if (wrapped <= -180f)
            wrapped += 360f;
        return wrapped;
    }
}
