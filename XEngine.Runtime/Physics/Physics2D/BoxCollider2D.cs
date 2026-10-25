// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Box2D.NET;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

[AddComponentMenu("Physics 2D/Box Collider 2D")]
[ComponentIcon("\uf1b2")]
public sealed class BoxCollider2D : Collider2D
{
    [SerializeField] private Float2 size = new(1f, 1f);

    /// <summary>Full width and height in engine units (meters). Box2D receives half-extents.</summary>
    public Float2 Size
    {
        get => size;
        set
        {
            size = value;
            OnValidate();
        }
    }

    protected override float ComputeDensity()
    {
        Rigidbody2D? rb = AttachedBody ?? GetComponent<Rigidbody2D>();
        float mass = rb != null ? rb.Mass : 1f;
        Float3 scale = Transform != null ? Transform.LossyScale : Float3.One;
        float w = Maths.Max(size.X * Maths.Abs(scale.X), 0.01f);
        float h = Maths.Max(size.Y * Maths.Abs(scale.Y), 0.01f);
        float area = w * h;
        return mass / area;
    }

    protected override B2ShapeId CreateShape(B2BodyId bodyId, ref B2ShapeDef def)
    {
        Float3 scale = Transform != null ? Transform.LossyScale : Float3.One;
        float hx = 0.5f * Maths.Max(size.X * Maths.Abs(scale.X), 0.01f);
        float hy = 0.5f * Maths.Max(size.Y * Maths.Abs(scale.Y), 0.01f);

        B2Polygon polygon;
        if (Offset.X != 0f || Offset.Y != 0f)
        {
            B2Rot rot = B2MathFunction.b2MakeRot(0f);
            polygon = B2Geometries.b2MakeOffsetBox(hx, hy, new B2Vec2(Offset.X, Offset.Y), rot);
        }
        else
        {
            polygon = B2Geometries.b2MakeBox(hx, hy);
        }

        return B2Shapes.b2CreatePolygonShape(bodyId, ref def, ref polygon);
    }

    public override void DrawGizmos()
    {
        if (!TryPushColliderGizmoMatrix())
            return;
        Debug.DrawWireCube(new Float3(Offset.X, Offset.Y, 0f), new Float3(size.X * 0.5f, size.Y * 0.5f, 0.01f), GizmoColor);
        Debug.PopMatrix();
    }
}
