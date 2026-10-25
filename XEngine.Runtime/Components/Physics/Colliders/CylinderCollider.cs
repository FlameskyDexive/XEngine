// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Jitter2.Collision.Shapes;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

[AddComponentMenu("Physics/Colliders/Cylinder Collider")]
[ComponentIcon("\uf46b")] // Capsules (closest shape)
public sealed class CylinderCollider : Collider
{
    [SerializeField] private float radius = 0.5f;
    [SerializeField] private float height = 2;

    /// <summary>
    /// Gets or sets the radius of the cylinder, raised to <see cref="Collider.MinExtent"/> if smaller.
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
    /// Gets or sets the height of the cylinder along its local Y axis, raised to
    /// <see cref="Collider.MinExtent"/> if smaller.
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

    // CylinderShape takes height first. Passing radius first built a shape with the two swapped, so a
    // tall thin cylinder simulated as a short wide one while the gizmo drew the shape that was asked for.
    public override RigidBodyShape[] CreateShapes()
        => [new CylinderShape(height: ClampExtent(height), radius: ClampExtent(radius))];

    public override void DrawGizmos()
    {
        Float4x4 matrix = Float4x4.CreateTRS(Transform.Position, Transform.Rotation * Quaternion.FromEuler(Rotation), ClampScale(Transform.LossyScale));
        Debug.PushMatrix(matrix);
        Debug.DrawWireCylinder(Center, Quaternion.Identity, ClampExtent(radius), ClampExtent(height), GizmoColor);
        Debug.PopMatrix();
    }
}
