// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Jitter2.Collision.Shapes;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

[AddComponentMenu("Physics/Colliders/Capsule Collider")]
[ComponentIcon("\uf46b")] // Capsules
public sealed class CapsuleCollider : Collider
{
    [SerializeField] private float radius = 0.5f;
    [SerializeField] private float height = 2;

    /// <summary>
    /// Gets or sets the radius of the capsule, raised to <see cref="Collider.MinExtent"/> if smaller.
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
    /// Gets or sets the total height of the capsule, hemispherical caps included.
    /// </summary>
    /// <remarks>
    /// Unity's meaning, which is not the kernel's: <c>CapsuleShape</c> is built from the length of the
    /// cylindrical section alone, so <see cref="CylinderLength"/> is what actually reaches it. A height at
    /// or below twice the radius leaves no cylinder, and the capsule is then a sphere — which is also what
    /// Unity does rather than refusing the value.
    /// </remarks>
    public float Height
    {
        get => height;
        set
        {
            height = ClampExtent(value);
            OnValidate();
        }
    }

    /// <summary>
    /// The cylindrical section between the two caps, which is what the shape is built from.
    /// </summary>
    private float CylinderLength
    {
        get
        {
            float length = ClampExtent(height) - 2f * ClampExtent(radius);
            return length > 0f ? length : 0f;
        }
    }

    public override RigidBodyShape[] CreateShapes() => [new CapsuleShape(ClampExtent(radius), CylinderLength)];

    public override void DrawGizmos()
    {
        Float4x4 matrix = Float4x4.CreateTRS(Transform.Position, Transform.Rotation * Quaternion.FromEuler(Rotation), ClampScale(Transform.LossyScale));
        Debug.PushMatrix(matrix);
        float halfLength = CylinderLength * 0.5f;
        Debug.DrawWireCapsule(Center + new Float3(0, -halfLength, 0), Center + new Float3(0, halfLength, 0), ClampExtent(radius), GizmoColor);
        Debug.PopMatrix();
    }
}
