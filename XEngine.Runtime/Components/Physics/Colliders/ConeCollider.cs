// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Jitter2.Collision.Shapes;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

[AddComponentMenu("Physics/Colliders/Cone Collider")]
[ComponentIcon("\ue4dc")] // Burst
public sealed class ConeCollider : Collider
{
    [SerializeField] private float radius = 0.5f;
    [SerializeField] private float height = 2;

    /// <summary>
    /// Gets or sets the radius of the cone's base, raised to <see cref="Collider.MinExtent"/> if smaller.
    /// </summary>
    public float Radius
    {
        get => radius;
        set
        {
            radius = ClampExtent(value);
            OnValidate();
        }
    }

    /// <summary>
    /// Gets or sets the height of the cone, raised to <see cref="Collider.MinExtent"/> if smaller.
    /// </summary>
    public float Height
    {
        get => height;
        set
        {
            height = ClampExtent(value);
            OnValidate();
        }
    }

    public override RigidBodyShape[] CreateShapes() => [new ConeShape(ClampExtent(radius), ClampExtent(height))];

    public override void DrawGizmos()
    {
        Float4x4 matrix = Float4x4.CreateTRS(Transform.Position, Transform.Rotation * Quaternion.FromEuler(Rotation), ClampScale(Transform.LossyScale));
        Debug.PushMatrix(matrix);
        float h = ClampExtent(height);
        // The shape's origin is its centre of mass, a quarter of the way up from the base, not the
        // midpoint. Drawing the base at -h/2 put the gizmo a quarter of a height below the shape.
        Debug.DrawWireCone(Center + new Float3(0, -h * 0.25f, 0), new Float3(0, h, 0), ClampExtent(radius), GizmoColor);
        Debug.PopMatrix();
    }
}
