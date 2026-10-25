// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Jitter2.Collision;
using Jitter2.Collision.Shapes;

using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Contains information about a raycast hit.
/// </summary>
public struct RaycastHit
{
    /// <summary>
    /// If the ray hit something.
    /// </summary>
    public bool Hit;

    /// <summary>
    /// The distance from the ray's origin to the impact point.
    /// </summary>
    public float Distance;

    /// <summary>
    /// The normal of the surface the ray hit.
    /// </summary>
    public Float3 Normal;

    /// <summary>
    /// The point in world space where the ray hit the collider.
    /// </summary>
    public Float3 Point;

    /// <summary>
    /// The Rigidbody3D of the collider that was hit.
    /// </summary>
    public Rigidbody3D Rigidbody;

    /// <summary>
    /// The Shape that was hit.
    /// </summary>
    public RigidBodyShape Shape;

    /// <summary>
    /// The collider that was hit. Unlike <see cref="Rigidbody"/> this is also set for static geometry,
    /// so it is the reliable way to identify what a ray hit.
    /// </summary>
    public Collider Collider;

    /// <summary>
    /// The Transform of the rigidbody that was hit.
    /// </summary>
    public Transform Transform;

    internal void SetFromJitterResult(DynamicTree.RayCastResult result, Float3 origin, Float3 direction)
    {
        Shape = result.Entity as RigidBodyShape;
        // Terrain and other non-shape proxies still count as a hit; they just have no collider.
        Rigidbody = ColliderBinding.GetRigidbody(Shape);
        Collider = ColliderBinding.GetCollider(Shape);

        Hit = true;
        Transform = Collider?.GameObject?.Transform ?? Rigidbody?.GameObject?.Transform;
        Normal = new Float3(result.Normal.X, result.Normal.Y, result.Normal.Z);
        Distance = result.Lambda;
        Point = origin + (direction * Distance);
    }
}
