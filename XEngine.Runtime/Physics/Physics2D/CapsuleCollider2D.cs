// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Box2D.NET;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

[AddComponentMenu("Physics 2D/Capsule Collider 2D")]
[ComponentIcon("\uf46b")]
public sealed class CapsuleCollider2D : Collider2D
{
    [SerializeField] private float radius = 0.5f;
    [SerializeField] private float height = 2f;
    [SerializeField] private CapsuleDirection2D direction = CapsuleDirection2D.Vertical;

    public float Radius
    {
        get => radius;
        set
        {
            radius = value;
            OnValidate();
        }
    }

    /// <summary>Full length including hemispherical caps, in engine units (meters).</summary>
    public float Height
    {
        get => height;
        set
        {
            height = value;
            OnValidate();
        }
    }

    public CapsuleDirection2D Direction
    {
        get => direction;
        set
        {
            direction = value;
            OnValidate();
        }
    }

    protected override float ComputeDensity()
    {
        Rigidbody2D? rb = AttachedBody ?? GetComponent<Rigidbody2D>();
        float mass = rb != null ? rb.Mass : 1f;
        GetWorldCapsule(out B2Vec2 c1, out B2Vec2 c2, out float r);
        float dx = c2.X - c1.X;
        float dy = c2.Y - c1.Y;
        float dist = Maths.Sqrt(dx * dx + dy * dy);
        float area = (Maths.PI * r * r) + (2f * r * dist);
        return mass / Maths.Max(area, 0.0001f);
    }

    protected override B2ShapeId CreateShape(B2BodyId bodyId, ref B2ShapeDef def)
    {
        GetWorldCapsule(out B2Vec2 c1, out B2Vec2 c2, out float r);
        var capsule = new B2Capsule(c1, c2, r);
        return B2Shapes.b2CreateCapsuleShape(bodyId, ref def, ref capsule);
    }

    public override void DrawGizmos()
    {
        bool vertical = direction == CapsuleDirection2D.Vertical;
        float r = Maths.Max(radius, 0.01f);
        float full = Maths.Max(height, r * 2f);
        float halfShaft = 0.5f * (full - 2f * r);
        Float3 a;
        Float3 b;
        if (vertical)
        {
            a = new Float3(Offset.X, Offset.Y - halfShaft, 0f);
            b = new Float3(Offset.X, Offset.Y + halfShaft, 0f);
        }
        else
        {
            a = new Float3(Offset.X - halfShaft, Offset.Y, 0f);
            b = new Float3(Offset.X + halfShaft, Offset.Y, 0f);
        }

        if (!TryPushColliderGizmoMatrix())
            return;
        Debug.DrawWireCapsule(a, b, r, GizmoColor);
        Debug.PopMatrix();
    }

    private void GetWorldCapsule(out B2Vec2 center1, out B2Vec2 center2, out float worldRadius)
    {
        Float3 scale = Transform != null ? Transform.LossyScale : Float3.One;
        float sx = Maths.Abs(scale.X);
        float sy = Maths.Abs(scale.Y);

        bool vertical = direction == CapsuleDirection2D.Vertical;
        float along = vertical ? sy : sx;
        float across = vertical ? sx : sy;

        worldRadius = Maths.Max(radius * across, 0.01f);
        float full = Maths.Max(height * along, worldRadius * 2f);
        float halfShaft = 0.5f * (full - 2f * worldRadius);

        if (vertical)
        {
            center1 = new B2Vec2(Offset.X, Offset.Y - halfShaft);
            center2 = new B2Vec2(Offset.X, Offset.Y + halfShaft);
        }
        else
        {
            center1 = new B2Vec2(Offset.X - halfShaft, Offset.Y);
            center2 = new B2Vec2(Offset.X + halfShaft, Offset.Y);
        }
    }
}
