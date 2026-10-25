/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using Jitter2.DataStructures;
using Jitter2.Dynamics;
using Jitter2.LinearMath;

namespace Jitter2.Collision.Shapes;

/// <summary>
/// The main entity of the collision system. Implements <see cref="ISupportMappable"/> for
/// narrow-phase and <see cref="IDynamicTreeProxy"/> for broad-phase collision detection.
/// The shape itself does not have a position or orientation. Shapes can be associated with
/// instances of <see cref="RigidBody"/>.
/// </summary>
public abstract class Shape : IDynamicTreeProxy, IUpdatableBoundingBox, ISupportMappable, IRayCastable, ISweepTestable, IDistanceTestable
{
    int IPartitionedSetIndex.SetIndex { get; set; } = -1;

    /// <summary>
    /// A 64-bit integer representing the shape ID. This is used by algorithms that require
    /// arranging shapes in a well-defined order.
    /// </summary>
    public readonly ulong ShapeId = World.RequestId();

    /// <summary>
    /// The bounding box of the shape in world space. It is automatically updated when the position or
    /// orientation of the corresponding instance of <see cref="RigidBody"/> changes.
    /// </summary>
    public JBoundingBox WorldBoundingBox { get; protected set; }

    /// <summary>
    /// User data associated with this shape. The engine never reads or writes it.
    /// </summary>
    /// <remarks>
    /// XENGINE: the counterpart of <see cref="RigidBody.Tag"/> at shape granularity. A body may own
    /// several shapes, so results that identify a shape (collision callbacks, ray and sweep hits)
    /// cannot resolve back to the owning game-side object through the body alone. Storing the owner
    /// here turns that reverse lookup into a field read instead of a side table keyed by
    /// <see cref="ShapeId"/>, which would have to be concurrency-safe because the narrow phase runs
    /// on several threads.
    /// </remarks>
    public object? Owner { get; set; }

    /// <summary>
    /// Behavioural flags for this shape.
    /// </summary>
    /// <remarks>
    /// XENGINE: a single field the narrow phase can test to decide whether a shape needs any
    /// special handling, so shapes that need none cost one read and one branch. Prefer the named
    /// properties over the raw flags.
    /// </remarks>
    public ShapeFlags Flags { get; set; }

    /// <summary>
    /// Gets or sets whether this shape detects overlaps without generating contacts.
    /// </summary>
    /// <remarks>
    /// XENGINE: a sensor participates in broad and narrow phase, so overlaps are reported, but no
    /// contact is registered for the pair. It therefore applies no forces, is invisible to the
    /// solver, and costs neither an arbiter nor solver iterations.
    /// </remarks>
    public bool IsSensor
    {
        get => (Flags & ShapeFlags.Sensor) != 0;
        set => Flags = value ? Flags | ShapeFlags.Sensor : Flags & ~ShapeFlags.Sensor;
    }

    /// <summary>
    /// Gets or sets whether collisions involving this shape are reported to the world's
    /// pair-event stream.
    /// </summary>
    /// <remarks>
    /// XENGINE: off by default, so a world whose shapes have no listeners does not pay for event
    /// bookkeeping.
    /// </remarks>
    public bool ReportsPairEvents
    {
        get => (Flags & ShapeFlags.ReportsPairEvents) != 0;
        set => Flags = value ? Flags | ShapeFlags.ReportsPairEvents : Flags & ~ShapeFlags.ReportsPairEvents;
    }

    int IDynamicTreeProxy.NodePtr { get; set; }

    protected void SweptExpandBoundingBox(Real dt)
    {
        Real swept = dt * Velocity.Length();
        JBoundingBox box = WorldBoundingBox;
        box.Min -= new JVector(swept);
        box.Max += new JVector(swept);
        WorldBoundingBox = box;
    }

    internal bool IsRegistered => (this as IPartitionedSetIndex).SetIndex != -1;

    [ReferenceFrame(ReferenceFrame.World)]
    public abstract JVector Velocity { get; }

    [ReferenceFrame(ReferenceFrame.World)]
    public abstract void UpdateWorldBoundingBox(Real dt = (Real)0.0);

    [ReferenceFrame(ReferenceFrame.World)]
    public abstract bool RayCast(in JVector origin, in JVector direction, out JVector normal, out Real lambda);

    [ReferenceFrame(ReferenceFrame.World)]
    public abstract bool Sweep<T>(in T support, in JQuaternion orientation, in JVector position, in JVector sweep,
        out JVector pointA, out JVector pointB, out JVector normal, out Real lambda)
        where T : ISupportMappable;

    [ReferenceFrame(ReferenceFrame.World)]
    public abstract bool Distance<T>(in T support, in JQuaternion orientation, in JVector position,
        out JVector pointA, out JVector pointB, out JVector normal, out Real distance)
        where T : ISupportMappable;

    /// <inheritdoc/>
    [ReferenceFrame(ReferenceFrame.Local)]
    public abstract void SupportMap(in JVector direction, out JVector result);

    /// <inheritdoc/>
    [ReferenceFrame(ReferenceFrame.Local)]
    public abstract void GetCenter(out JVector point);
}
