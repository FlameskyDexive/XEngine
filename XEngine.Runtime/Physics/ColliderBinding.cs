// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Jitter2.Collision;
using Jitter2.Collision.Shapes;

namespace XEngine.Runtime;

/// <summary>
/// Links a Jitter2 shape back to the <see cref="Collider"/> that created it, and caches the values
/// the physics pipeline needs per shape.
/// </summary>
/// <remarks>
/// A body may own shapes from several colliders, so a body-level tag cannot answer "which collider
/// was hit". The binding is stored in <see cref="Shape.Owner"/>, making the reverse lookup a field
/// read on a shape that is already in cache, instead of a dictionary keyed by shape id that would
/// have to be safe for the concurrent narrow phase.
/// <para>
/// <see cref="Layer"/> is cached rather than read through <c>Collider.GameObject.LayerIndex</c>
/// because broad-phase filtering and layer-masked queries read it once per visited shape.
/// <see cref="Collider.SyncBindings"/> refreshes it.
/// </para>
/// </remarks>
internal sealed class ColliderBinding
{
    /// <summary>The collider that created the shape. Never null.</summary>
    public readonly Collider Collider;

    /// <summary>
    /// The rigidbody the collider is attached to, or null when it is attached to a static body.
    /// </summary>
    public Rigidbody3D? Rigidbody;

    /// <summary>Cached <c>GameObject.LayerIndex</c> of the collider.</summary>
    public int Layer;

    /// <summary>Cached <see cref="Collider.IsTrigger"/>.</summary>
    public bool IsTrigger;

    public ColliderBinding(Collider collider)
    {
        Collider = collider;
    }

    /// <summary>
    /// Gets the binding for a proxy, or null when the proxy is not a shape created by a collider
    /// (character controller internals and terrain shapes register without one).
    /// </summary>
    public static ColliderBinding? Of(IDynamicTreeProxy? proxy)
        => (proxy as Shape)?.Owner as ColliderBinding;

    /// <summary>
    /// Gets the binding for a shape, or null when the shape was not created by a collider.
    /// </summary>
    public static ColliderBinding? Of(Shape? shape)
        => shape?.Owner as ColliderBinding;

    /// <summary>
    /// Resolves the collision layer of a shape, preferring the per-shape binding and falling back to
    /// the owning body's tag for shapes that have no binding.
    /// </summary>
    /// <returns><c>false</c> when neither source can supply a layer, in which case the caller should
    /// not filter the shape out on layer grounds.</returns>
    public static bool TryGetLayer(RigidBodyShape? shape, out int layer)
    {
        if (shape?.Owner is ColliderBinding binding)
        {
            layer = binding.Layer;
            return true;
        }

        if (shape?.RigidBody?.Tag is Rigidbody3D.RigidBodyUserData userData)
        {
            layer = userData.Layer;
            return true;
        }

        layer = 0;
        return false;
    }

    /// <summary>
    /// Resolves the <see cref="Collider"/> a shape belongs to, or null when it has no binding.
    /// </summary>
    public static Collider? GetCollider(Shape? shape)
        => (shape?.Owner as ColliderBinding)?.Collider;

    /// <summary>
    /// Resolves the <see cref="Rigidbody3D"/> a shape belongs to, preferring the per-shape binding
    /// and falling back to the owning body's tag.
    /// </summary>
    /// <returns>Null for static geometry and for shapes registered without either.</returns>
    public static Rigidbody3D? GetRigidbody(RigidBodyShape? shape)
        => (shape?.Owner as ColliderBinding)?.Rigidbody
           ?? (shape?.RigidBody?.Tag as Rigidbody3D.RigidBodyUserData)?.Rigidbody;
}
