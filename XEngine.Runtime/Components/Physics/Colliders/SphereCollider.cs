// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Jitter2.Collision.Shapes;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

[AddComponentMenu("Physics/Colliders/Sphere Collider")]
[ComponentIcon("\uf111")] // Circle
public sealed class SphereCollider : Collider
{
    [SerializeField] private float radius = 0.5f;

    /// <summary>
    /// Gets or sets the radius, raised to <see cref="Collider.MinExtent"/> if smaller.
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

    public override RigidBodyShape[] CreateShapes() => [new SphereShape(ClampExtent(radius))];

    public override void DrawGizmos()
    {
        Float4x4 matrix = Float4x4.CreateTRS(Transform.Position, Transform.Rotation * Quaternion.FromEuler(Rotation), ClampScale(Transform.LossyScale));
        Debug.PushMatrix(matrix);
        Debug.DrawWireSphere(Center, ClampExtent(radius), GizmoColor);
        Debug.PopMatrix();
    }
}
