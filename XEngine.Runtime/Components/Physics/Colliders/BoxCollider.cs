// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Jitter2.Collision.Shapes;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

[AddComponentMenu("Physics/Colliders/Box Collider")]
public sealed class BoxCollider : Collider
{
    [SerializeField] private Float3 size = new(1, 1, 1);

    /// <summary>
    /// Gets or sets the dimensions of the box. Each component is raised to
    /// <see cref="Collider.MinExtent"/> if smaller.
    /// </summary>
    public Float3 Size
    {
        get => size;
        set
        {
            size = ClampExtent(value);
            OnValidate();
        }
    }

    public override RigidBodyShape[] CreateShapes()
    {
        Float3 extents = ClampExtent(size);
        return [new BoxShape(extents.X, extents.Y, extents.Z)];
    }

    public override void DrawGizmos()
    {
        Float4x4 matrix = Float4x4.CreateTRS(Transform.Position, Transform.Rotation * Quaternion.FromEuler(Rotation), ClampScale(Transform.LossyScale));
        Debug.PushMatrix(matrix);
        Debug.DrawWireCube(Center, ClampExtent(size) * 0.5f, GizmoColor);
        Debug.PopMatrix();
    }
}
