// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Quill.External.LibTessDotNet;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Spline-driven 2D shape. Fill is tessellated with the vendored LibTessDotNet and cached by
/// spline/profile content hash. Edge strips are CPU quads keyed by profile angle ranges.
/// </summary>
[AddComponentMenu("2D/Sprite Shape Controller")]
[ComponentIcon("\uf1fc")]
public sealed class SpriteShapeController : MonoBehaviour
{
    private static readonly Float2[] EmptyVertices = [];
    private static readonly int[] EmptyIndices = [];
    private static readonly List<SpriteShapeEdgeStrip> EmptyEdges = [];
    private static readonly List<SpriteShapeCorner> EmptyCorners = [];

    [SerializeField] private bool closed = true;
    [SerializeField] private float edgeWidth = 0.1f;
    [SerializeField] private List<SpriteShapePoint> spline = [];
    [SerializeField] private SpriteShapeProfile? profile;

    private Float2[] _fillVertices = EmptyVertices;
    private int[] _fillIndices = EmptyIndices;
    private List<SpriteShapeEdgeStrip> _edges = EmptyEdges;
    private List<SpriteShapeCorner> _corners = EmptyCorners;
    private ulong _contentHash;
    private bool _baked;

    public bool Closed
    {
        get => closed;
        set => closed = value;
    }

    public float EdgeWidth
    {
        get => edgeWidth;
        set => edgeWidth = value < 0f ? 0f : value;
    }

    public List<SpriteShapePoint> Spline => spline;

    public SpriteShapeProfile? Profile
    {
        get => profile;
        set => profile = value;
    }

    public Float2[] FillVertices => _fillVertices;
    public int[] FillIndices => _fillIndices;
    public IReadOnlyList<SpriteShapeEdgeStrip> Edges => _edges;
    public IReadOnlyList<SpriteShapeCorner> Corners => _corners;
    public ulong ContentHash => _contentHash;

    public void Bake()
    {
        ulong hash = ComputeHash();
        if (_baked && hash == _contentHash)
            return;

        _contentHash = hash;
        _baked = true;

        if (closed && spline.Count >= 3)
            TessellateFill();
        else
        {
            _fillVertices = EmptyVertices;
            _fillIndices = EmptyIndices;
        }

        BakeEdges();
    }

    public PolygonCollider2D BakeCollider()
    {
        PolygonCollider2D collider = GetComponent<PolygonCollider2D>() ?? AddComponent<PolygonCollider2D>();
        var points = new Float2[spline.Count];
        for (int i = 0; i < spline.Count; i++)
            points[i] = spline[i].Position;
        collider.Points = points;
        return collider;
    }

    private void TessellateFill()
    {
        var tess = new Tess();
        var contour = new ContourVertex[spline.Count];
        for (int i = 0; i < spline.Count; i++)
        {
            Float2 p = spline[i].Position;
            contour[i] = new ContourVertex { Position = new Vec3 { X = p.X, Y = p.Y, Z = 0f } };
        }

        tess.AddContour(contour, ContourOrientation.Original);
        tess.Tessellate(WindingRule.NonZero, ElementType.Polygons, 3);

        ContourVertex[] vertices = tess.Vertices ?? [];
        int[] elements = tess.Elements ?? [];
        var fillVerts = new Float2[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            fillVerts[i] = new Float2(vertices[i].Position.X, vertices[i].Position.Y);

        _fillVertices = fillVerts;
        _fillIndices = elements.Length == 0 ? EmptyIndices : elements;
    }

    private void BakeEdges()
    {
        if (profile == null || spline.Count < 2)
        {
            _edges = EmptyEdges;
            _corners = EmptyCorners;
            return;
        }

        int segmentCount = closed ? spline.Count : spline.Count - 1;
        var edges = new List<SpriteShapeEdgeStrip>(segmentCount);
        var corners = new List<SpriteShapeCorner>(segmentCount);
        float half = edgeWidth * 0.5f;

        for (int i = 0; i < segmentCount; i++)
        {
            Float2 start = spline[i].Position;
            Float2 end = spline[(i + 1) % spline.Count].Position;
            Float2 delta = end - start;
            float lengthSq = delta.X * delta.X + delta.Y * delta.Y;
            if (lengthSq <= 1e-12f)
                continue;

            float angle = MathF.Atan2(delta.Y, delta.X) * (180f / MathF.PI);
            string? spriteId = profile.ResolveEdge(angle);
            if (!string.IsNullOrEmpty(spriteId))
            {
                float invLen = 1f / MathF.Sqrt(lengthSq);
                Float2 normal = new Float2(-delta.Y * invLen, delta.X * invLen) * half;
                edges.Add(new SpriteShapeEdgeStrip
                {
                    SpriteId = spriteId,
                    Start = start,
                    End = end,
                    Quad =
                    [
                        start + normal,
                        start - normal,
                        end - normal,
                        end + normal,
                    ],
                });
            }

            string? cornerId = profile.ResolveCorner(angle);
            if (!string.IsNullOrEmpty(cornerId))
                corners.Add(new SpriteShapeCorner { SpriteId = cornerId, Position = start });
        }

        _edges = edges;
        _corners = corners;
    }

    private ulong ComputeHash()
    {
        var hc = new HashCode();
        hc.Add(closed);
        hc.Add(edgeWidth);
        hc.Add(spline.Count);
        for (int i = 0; i < spline.Count; i++)
        {
            Float2 p = spline[i].Position;
            hc.Add(p.X);
            hc.Add(p.Y);
        }

        if (profile != null)
        {
            hc.Add(profile.FillSpriteId);
            hc.Add(profile.AngleRanges.Count);
            for (int i = 0; i < profile.AngleRanges.Count; i++)
            {
                SpriteShapeAngleRange range = profile.AngleRanges[i];
                hc.Add(range.Start);
                hc.Add(range.End);
                hc.Add(range.EdgeSpriteId);
                hc.Add(range.CornerSpriteId);
            }
        }

        return (ulong)(uint)hc.ToHashCode();
    }
}
