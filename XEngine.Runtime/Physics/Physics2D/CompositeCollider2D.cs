// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Box2D.NET;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Merges <see cref="Collider2D.UsedByComposite"/> box and polygon colliders in this hierarchy
/// into shapes on the parent <see cref="Rigidbody2D"/>. Tilemap chunks attach here later.
/// </summary>
[AddComponentMenu("Physics 2D/Composite Collider 2D")]
[ComponentIcon("\uf009")]
public sealed class CompositeCollider2D : Collider2D
{
    private B2ShapeId[] _shapes = new B2ShapeId[4];
    private int _shapeCount;

    public int ShapeCount => _shapeCount;

    public void GenerateGeometry()
    {
        if (AttachedBody != null && AttachedBody.HasBody)
            RecreateShape(AttachedBody.BodyId);
        else
            OnValidate();
    }

    protected override float ComputeDensity()
    {
        Rigidbody2D? rb = AttachedBody ?? GetComponent<Rigidbody2D>();
        float mass = rb != null ? rb.Mass : 1f;
        return mass / Maths.Max(SumChildArea(), 0.0001f);
    }

    protected override B2ShapeId CreateShape(B2BodyId bodyId, ref B2ShapeDef def)
    {
        _shapeCount = 0;
        GameObject? root = GameObject;
        Transform? bodyTransform = Transform;
        if (root == null || bodyTransform == null)
            return default;

        foreach (Collider2D collider in root.GetComponentsInChildren<Collider2D>())
        {
            if (collider == this || !collider.UsedByComposite)
                continue;

            B2ShapeId id = CreateChildShape(collider, bodyId, bodyTransform, ref def);
            if (!B2Worlds.b2Shape_IsValid(id))
                continue;

            B2Shapes.b2Shape_EnableSensorEvents(id, true);
            B2Shapes.b2Shape_EnableContactEvents(id, !IsTrigger);
            AddShape(id);
        }

        return _shapeCount > 0 ? _shapes[0] : default;
    }

    protected override void DestroyCreatedShapes()
    {
        for (int i = 0; i < _shapeCount; i++)
        {
            if (B2Worlds.b2Shape_IsValid(_shapes[i]))
                B2Shapes.b2DestroyShape(_shapes[i], updateBodyMass: true);
            _shapes[i] = default;
        }

        _shapeCount = 0;
        ClearShapeId();
    }

    private void AddShape(B2ShapeId id)
    {
        if (_shapeCount >= _shapes.Length)
            Array.Resize(ref _shapes, _shapes.Length * 2);
        _shapes[_shapeCount++] = id;
    }

    private static B2ShapeId CreateChildShape(Collider2D collider, B2BodyId bodyId, Transform bodyTransform, ref B2ShapeDef def)
    {
        return collider switch
        {
            BoxCollider2D box => CreateBox(box, bodyId, bodyTransform, ref def),
            PolygonCollider2D polygon => CreatePolygon(polygon, bodyId, bodyTransform, ref def),
            _ => default
        };
    }

    private static B2ShapeId CreateBox(BoxCollider2D box, B2BodyId bodyId, Transform bodyTransform, ref B2ShapeDef def)
    {
        Transform? child = box.Transform;
        if (child == null)
            return default;

        Float3 scale = child.LossyScale;
        float hx = 0.5f * Maths.Max(box.Size.X * Maths.Abs(scale.X), 0.01f);
        float hy = 0.5f * Maths.Max(box.Size.Y * Maths.Abs(scale.Y), 0.01f);
        Float2 center = ToBodyLocal(bodyTransform, child, box.Offset);
        float rel = Maths.Deg2Rad * (child.EulerAngles.Z - bodyTransform.EulerAngles.Z);
        B2Rot rot = B2MathFunction.b2MakeRot(rel);
        B2Polygon polygon = B2Geometries.b2MakeOffsetBox(hx, hy, new B2Vec2(center.X, center.Y), rot);
        return B2Shapes.b2CreatePolygonShape(bodyId, ref def, ref polygon);
    }

    private static B2ShapeId CreatePolygon(PolygonCollider2D polygon, B2BodyId bodyId, Transform bodyTransform, ref B2ShapeDef def)
    {
        Transform? child = polygon.Transform;
        Float2[]? points = polygon.Points;
        if (child == null || points == null)
            return default;

        int count = Maths.Min(points.Length, 8);
        if (count < 3)
            return default;

        Span<B2Vec2> verts = stackalloc B2Vec2[count];
        for (int i = 0; i < count; i++)
        {
            Float2 local = ToBodyLocal(bodyTransform, child, points[i] + polygon.Offset);
            verts[i] = new B2Vec2(local.X, local.Y);
        }

        B2Hull hull = B2Hulls.b2ComputeHull(verts, count);
        if (hull.count < 3)
            return default;

        B2Polygon shape = B2Geometries.b2MakePolygon(ref hull, 0f);
        return B2Shapes.b2CreatePolygonShape(bodyId, ref def, ref shape);
    }

    private static Float2 ToBodyLocal(Transform body, Transform child, Float2 localPoint)
    {
        Float3 world = child.TransformPoint(new Float3(localPoint.X, localPoint.Y, 0f));
        Float3 bodyLocal = body.InverseTransformPoint(world);
        return new Float2(bodyLocal.X, bodyLocal.Y);
    }

    private float SumChildArea()
    {
        GameObject? root = GameObject;
        if (root == null)
            return 0f;

        float area = 0f;
        foreach (Collider2D collider in root.GetComponentsInChildren<Collider2D>())
        {
            if (collider == this || !collider.UsedByComposite)
                continue;
            area += ChildArea(collider);
        }

        return area;
    }

    private static float ChildArea(Collider2D collider)
    {
        Float3 scale = collider.Transform != null ? collider.Transform.LossyScale : Float3.One;
        if (collider is BoxCollider2D box)
        {
            float w = Maths.Max(box.Size.X * Maths.Abs(scale.X), 0.01f);
            float h = Maths.Max(box.Size.Y * Maths.Abs(scale.Y), 0.01f);
            return w * h;
        }

        if (collider is PolygonCollider2D polygon)
        {
            Float2[]? points = polygon.Points;
            if (points == null || points.Length < 3)
                return 0f;

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

        return 0f;
    }
}
