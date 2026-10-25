// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>Headless SpriteShape spline fill (LibTessDotNet), profile ranges, and collider bake.</summary>
public sealed class SpriteShapeTests : RuntimeTestBase
{
    [Fact]
    public void Bake_ClosedSquare_FillsTwoTriangles()
    {
        var shape = CreateClosedSquare();

        shape.Bake();

        Assert.True(shape.FillVertices.Length >= 3);
        Assert.Equal(6, shape.FillIndices.Length);
    }

    [Fact]
    public void Bake_UnchangedSpline_ReusesCachedFill()
    {
        var shape = CreateClosedSquare();
        shape.Bake();
        Float2[] firstVerts = shape.FillVertices;
        int[] firstIndices = shape.FillIndices;
        ulong hash = shape.ContentHash;

        shape.Bake();

        Assert.Equal(hash, shape.ContentHash);
        Assert.Same(firstVerts, shape.FillVertices);
        Assert.Same(firstIndices, shape.FillIndices);
    }

    [Fact]
    public void Bake_MovedPoint_RebuildsFill()
    {
        var shape = CreateClosedSquare();
        shape.Bake();
        Float2[] firstVerts = shape.FillVertices;
        ulong hash = shape.ContentHash;

        shape.Spline[2] = new SpriteShapePoint { Position = new Float2(2f, 2f) };
        shape.Bake();

        Assert.NotEqual(hash, shape.ContentHash);
        Assert.NotSame(firstVerts, shape.FillVertices);
    }

    [Fact]
    public void Bake_OpenSpline_HasNoFill()
    {
        var shape = CreateClosedSquare();
        shape.Closed = false;

        shape.Bake();

        Assert.Empty(shape.FillIndices);
    }

    [Fact]
    public void Profile_ResolveEdge_PicksAngleRange()
    {
        var profile = new SpriteShapeProfile();
        profile.FillSpriteId = "fill";
        profile.AngleRanges.Add(new SpriteShapeAngleRange
        {
            Start = -45f,
            End = 45f,
            EdgeSpriteId = "ground",
        });
        profile.AngleRanges.Add(new SpriteShapeAngleRange
        {
            Start = 45f,
            End = 135f,
            EdgeSpriteId = "wall",
        });

        Assert.Equal("ground", profile.ResolveEdge(0f));
        Assert.Equal("wall", profile.ResolveEdge(90f));
        Assert.Null(profile.ResolveEdge(180f));
    }

    [Fact]
    public void BakeCollider_CopiesClosedSplineOntoPolygonCollider2D()
    {
        GameObject go = CreateGameObject("Shape");
        var shape = go.AddComponent<SpriteShapeController>();
        shape.Closed = true;
        shape.Spline.Add(new SpriteShapePoint { Position = new Float2(0f, 0f) });
        shape.Spline.Add(new SpriteShapePoint { Position = new Float2(1f, 0f) });
        shape.Spline.Add(new SpriteShapePoint { Position = new Float2(1f, 1f) });
        shape.Spline.Add(new SpriteShapePoint { Position = new Float2(0f, 1f) });

        PolygonCollider2D collider = shape.BakeCollider();

        Assert.Same(go.GetComponent<PolygonCollider2D>(), collider);
        Assert.Equal(4, collider.Points.Length);
        Assert.Equal(0f, collider.Points[0].X);
        Assert.Equal(1f, collider.Points[2].Y);
    }

    [Fact]
    public void Bake_WithProfile_EmitsEdgeStripsByTangent()
    {
        var shape = CreateClosedSquare();
        shape.EdgeWidth = 0.2f;
        shape.Profile = GroundAndWallProfile();

        shape.Bake();

        Assert.Contains(shape.Edges, e => e.SpriteId == "ground" && e.Start.X == 0f && e.End.X == 1f && e.Start.Y == 0f);
        Assert.Contains(shape.Edges, e => e.SpriteId == "wall" && e.Start.X == 1f && e.End.Y == 1f);
    }

    [Fact]
    public void Bake_EdgeStrip_IsQuadOffsetFromSegment()
    {
        GameObject go = CreateGameObject("Shape");
        var shape = go.AddComponent<SpriteShapeController>();
        shape.Closed = false;
        shape.EdgeWidth = 2f;
        shape.Spline.Add(new SpriteShapePoint { Position = new Float2(0f, 0f) });
        shape.Spline.Add(new SpriteShapePoint { Position = new Float2(4f, 0f) });
        shape.Profile = GroundAndWallProfile();

        shape.Bake();

        SpriteShapeEdgeStrip edge = Assert.Single(shape.Edges);
        Assert.Equal("ground", edge.SpriteId);
        Assert.Equal(4, edge.Quad.Length);
        Assert.Equal(1f, edge.Quad[0].Y, 3);
        Assert.Equal(-1f, edge.Quad[1].Y, 3);
    }

    [Fact]
    public void Bake_OpenSpline_EmitsEdgesWithoutFill()
    {
        GameObject go = CreateGameObject("Shape");
        var shape = go.AddComponent<SpriteShapeController>();
        shape.Closed = false;
        shape.Spline.Add(new SpriteShapePoint { Position = new Float2(0f, 0f) });
        shape.Spline.Add(new SpriteShapePoint { Position = new Float2(1f, 0f) });
        shape.Spline.Add(new SpriteShapePoint { Position = new Float2(1f, 1f) });
        shape.Profile = GroundAndWallProfile();

        shape.Bake();

        Assert.Empty(shape.FillIndices);
        Assert.Equal(2, shape.Edges.Count);
    }

    [Fact]
    public void Bake_UnchangedSpline_ReusesCachedEdges()
    {
        var shape = CreateClosedSquare();
        shape.Profile = GroundAndWallProfile();
        shape.Bake();
        IReadOnlyList<SpriteShapeEdgeStrip> first = shape.Edges;

        shape.Bake();

        Assert.Same(first, shape.Edges);
    }

    [Fact]
    public void Bake_EmitsCornerWhenRangeHasCornerId()
    {
        var shape = CreateClosedSquare();
        var profile = GroundAndWallProfile();
        profile.AngleRanges[0] = new SpriteShapeAngleRange
        {
            Start = -45f,
            End = 45f,
            EdgeSpriteId = "ground",
            CornerSpriteId = "ground-corner",
        };
        shape.Profile = profile;

        shape.Bake();

        Assert.Contains(shape.Corners, c => c.SpriteId == "ground-corner" && c.Position.X == 0f && c.Position.Y == 0f);
    }

    private static SpriteShapeProfile GroundAndWallProfile()
    {
        var profile = new SpriteShapeProfile();
        profile.AngleRanges.Add(new SpriteShapeAngleRange
        {
            Start = -45f,
            End = 45f,
            EdgeSpriteId = "ground",
        });
        profile.AngleRanges.Add(new SpriteShapeAngleRange
        {
            Start = 45f,
            End = 135f,
            EdgeSpriteId = "wall",
        });
        return profile;
    }

    private SpriteShapeController CreateClosedSquare()
    {
        GameObject go = CreateGameObject("Shape");
        var shape = go.AddComponent<SpriteShapeController>();
        shape.Closed = true;
        shape.Spline.Add(new SpriteShapePoint { Position = new Float2(0f, 0f) });
        shape.Spline.Add(new SpriteShapePoint { Position = new Float2(1f, 0f) });
        shape.Spline.Add(new SpriteShapePoint { Position = new Float2(1f, 1f) });
        shape.Spline.Add(new SpriteShapePoint { Position = new Float2(0f, 1f) });
        return shape;
    }
}
