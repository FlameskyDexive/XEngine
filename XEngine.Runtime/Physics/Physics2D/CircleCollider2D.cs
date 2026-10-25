// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Box2D.NET;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

[AddComponentMenu("Physics 2D/Circle Collider 2D")]
[ComponentIcon("\uf111")]
public sealed class CircleCollider2D : Collider2D
{
    [SerializeField] private float radius = 0.5f;

    public float Radius
    {
        get => radius;
        set
        {
            radius = value;
            OnValidate();
        }
    }

    protected override float ComputeDensity()
    {
        Rigidbody2D? rb = AttachedBody ?? GetComponent<Rigidbody2D>();
        float mass = rb != null ? rb.Mass : 1f;
        Float3 scale = Transform != null ? Transform.LossyScale : Float3.One;
        float r = ScaledRadius(scale);
        float area = Maths.PI * r * r;
        return mass / Maths.Max(area, 0.0001f);
    }

    protected override B2ShapeId CreateShape(B2BodyId bodyId, ref B2ShapeDef def)
    {
        Float3 scale = Transform != null ? Transform.LossyScale : Float3.One;
        var circle = new B2Circle(new B2Vec2(Offset.X, Offset.Y), ScaledRadius(scale));
        return B2Shapes.b2CreateCircleShape(bodyId, ref def, ref circle);
    }

    public override void DrawGizmos()
    {
        if (!TryPushColliderGizmoMatrix())
            return;
        Debug.DrawWireCircle(new Float3(Offset.X, Offset.Y, 0f), Float3.UnitZ, radius, GizmoColor);
        Debug.PopMatrix();
    }

    private float ScaledRadius(Float3 scale)
    {
        float s = Maths.Max(Maths.Abs(scale.X), Maths.Abs(scale.Y));
        return Maths.Max(radius * s, 0.01f);
    }
}
