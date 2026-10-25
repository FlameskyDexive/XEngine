// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Box2D.NET;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

[AddComponentMenu("Physics 2D/Polygon Collider 2D")]
[ComponentIcon("\uf0c8")]
public sealed class PolygonCollider2D : Collider2D
{
    [SerializeField] private Float2[] points =
    [
        new Float2(-0.5f, -0.5f),
        new Float2(0.5f, -0.5f),
        new Float2(0.5f, 0.5f),
        new Float2(-0.5f, 0.5f)
    ];

    /// <summary>
    /// Local-space vertices. Box2D keeps a convex hull of at most 8 points.
    /// </summary>
    public Float2[] Points
    {
        get => points;
        set
        {
            points = value ?? [];
            OnValidate();
        }
    }

    protected override float ComputeDensity()
    {
        Rigidbody2D? rb = AttachedBody ?? GetComponent<Rigidbody2D>();
        float mass = rb != null ? rb.Mass : 1f;
        float area = ComputeArea();
        return mass / Maths.Max(area, 0.0001f);
    }

    protected override B2ShapeId CreateShape(B2BodyId bodyId, ref B2ShapeDef def)
    {
        int count = points != null ? Maths.Min(points.Length, 8) : 0;
        if (count < 3)
            return default;

        Span<B2Vec2> verts = stackalloc B2Vec2[count];
        Float3 scale = Transform != null ? Transform.LossyScale : Float3.One;
        for (int i = 0; i < count; i++)
        {
            verts[i] = new B2Vec2(
                points[i].X * scale.X + Offset.X,
                points[i].Y * scale.Y + Offset.Y);
        }

        B2Hull hull = B2Hulls.b2ComputeHull(verts, count);
        if (hull.count < 3)
            return default;

        B2Polygon polygon = B2Geometries.b2MakePolygon(ref hull, 0f);
        return B2Shapes.b2CreatePolygonShape(bodyId, ref def, ref polygon);
    }

    public override void DrawGizmos()
    {
        if (points == null || points.Length < 2)
            return;

        if (!TryPushColliderGizmoMatrix())
            return;
        Color color = GizmoColor;
        int n = points.Length;
        for (int i = 0; i < n; i++)
        {
            Float2 a = points[i] + Offset;
            Float2 b = points[(i + 1) % n] + Offset;
            Debug.DrawLine(new Float3(a.X, a.Y, 0f), new Float3(b.X, b.Y, 0f), color);
        }
        Debug.PopMatrix();
    }

    private float ComputeArea()
    {
        if (points == null || points.Length < 3)
            return 0f;

        Float3 scale = Transform != null ? Transform.LossyScale : Float3.One;
        float area = 0f;
        int n = Maths.Min(points.Length, 8);
        for (int i = 0; i < n; i++)
        {
            Float2 a = points[i];
            Float2 b = points[(i + 1) % n];
            area += (a.X * scale.X) * (b.Y * scale.Y) - (b.X * scale.X) * (a.Y * scale.Y);
        }
        return Maths.Abs(area) * 0.5f;
    }
}
