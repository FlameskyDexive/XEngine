// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>One SpriteShape spline control point in local XY.</summary>
public struct SpriteShapePoint
{
    public Float2 Position;
}

/// <summary>Edge/corner sprites used when a spline tangent falls in <see cref="Start"/>..<see cref="End"/> degrees.</summary>
public struct SpriteShapeAngleRange
{
    public float Start;
    public float End;
    public string EdgeSpriteId;
    public string CornerSpriteId;
}

/// <summary>One CPU edge strip: sprite id plus a width-offset quad along the segment.</summary>
public struct SpriteShapeEdgeStrip
{
    public string SpriteId;
    public Float2 Start;
    public Float2 End;
    public Float2[] Quad;
}

/// <summary>Corner sprite at a spline control point.</summary>
public struct SpriteShapeCorner
{
    public string SpriteId;
    public Float2 Position;
}
