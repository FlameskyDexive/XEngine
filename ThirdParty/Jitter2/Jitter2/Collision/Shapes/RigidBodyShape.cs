/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using Jitter2.Dynamics;
using Jitter2.LinearMath;

namespace Jitter2.Collision.Shapes;

/// <summary>
/// Represents the abstract base class for shapes that can be attached to a rigid body.
/// </summary>
public abstract class RigidBodyShape : Shape
{
    /// <summary>
    /// The instance of <see cref="RigidBody"/> to which this shape is attached.
    /// </summary>
    public RigidBody RigidBody { get; internal set; } = null!;

    /// <summary>
    /// Friction coefficient for this shape, or <see langword="null"/> to use
    /// <see cref="RigidBody.Friction"/>.
    /// </summary>
    /// <remarks>
    /// XENGINE: a body may carry several shapes with different surfaces — an ice patch welded to a
    /// rubber floor — which the body-level coefficient cannot express. Resolution happens once per
    /// arbiter, in <see cref="Dynamics.ContactData.Init(Dynamics.RigidBody, Dynamics.RigidBody, RigidBodyShape, RigidBodyShape)"/>.
    /// <para>
    /// <see langword="null"/> rather than a default number, so that a shape which was never given a
    /// material does not silently outvote <see cref="RigidBody.Friction"/>. With both shapes unset and
    /// both modes left at <see cref="MaterialCombine.Maximum"/>, resolution reduces exactly to the
    /// upstream <c>max(body1.Friction, body2.Friction)</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="System.ArgumentOutOfRangeException">Thrown if the value is negative.</exception>
    public Real? Friction
    {
        get => friction;
        set => friction = value.HasValue
            ? ArgumentCheck.NonNegative(value.Value, nameof(value))
            : null;
    }

    /// <summary>
    /// Restitution coefficient for this shape, or <see langword="null"/> to use
    /// <see cref="RigidBody.Restitution"/>.
    /// </summary>
    /// <remarks>XENGINE: see <see cref="Friction"/>.</remarks>
    /// <exception cref="System.ArgumentOutOfRangeException">Thrown if the value is outside [0, 1].</exception>
    public Real? Restitution
    {
        get => restitution;
        set => restitution = value.HasValue
            ? ArgumentCheck.InRange(value.Value, (Real)0.0, (Real)1.0, nameof(value))
            : null;
    }

    /// <summary>
    /// How this shape's <see cref="Friction"/> combines with the other shape's in a pair, or
    /// <see langword="null"/> to let the other shape decide.
    /// </summary>
    /// <remarks>
    /// XENGINE: <see langword="null"/> by default, so a shape with no material abstains rather than
    /// voting. With both sides abstaining the pair combines with <see cref="MaterialCombine.Maximum"/>,
    /// which is upstream's <c>max</c>; with one side abstaining the other's mode is used unopposed. See
    /// <see cref="MaterialCombineExtensions.Reconcile"/> for why abstaining beats any concrete default.
    /// </remarks>
    public MaterialCombine? FrictionCombine { get; set; }

    /// <summary>
    /// How this shape's <see cref="Restitution"/> combines with the other shape's in a pair, or
    /// <see langword="null"/> to let the other shape decide.
    /// </summary>
    /// <remarks>XENGINE: see <see cref="FrictionCombine"/>.</remarks>
    public MaterialCombine? RestitutionCombine { get; set; }

    /// <summary>
    /// Relative approach speed above which this shape is worth a swept test, or <see langword="null"/>
    /// to use <see cref="World.SpeculativeVelocityThreshold"/>.
    /// </summary>
    /// <remarks>
    /// XENGINE: the threshold that avoids tunnelling is about <c>thickness / dt</c>, so it is really a
    /// property of the shape and only looks global. One world-wide value has to be set for the
    /// thinnest shape in the scene, which makes every larger shape pay for sweeps it cannot need. A
    /// pair sweeps at the lower of the two shapes' thresholds, so the more tunnel-prone shape decides.
    /// </remarks>
    /// <exception cref="System.ArgumentOutOfRangeException">Thrown if the value is negative.</exception>
    public Real? SweepVelocityThreshold
    {
        get => sweepVelocityThreshold;
        set => sweepVelocityThreshold = value.HasValue
            ? ArgumentCheck.NonNegative(value.Value, nameof(value))
            : null;
    }

    private Real? friction;
    private Real? restitution;
    private Real? sweepVelocityThreshold;

    /// <summary>
    /// The friction coefficient this shape contributes to a pair.
    /// </summary>
    internal Real EffectiveFriction => friction ?? RigidBody.Friction;

    /// <summary>
    /// The restitution coefficient this shape contributes to a pair.
    /// </summary>
    internal Real EffectiveRestitution => restitution ?? RigidBody.Restitution;

    public sealed override JVector Velocity => RigidBody?.Velocity ?? JVector.Zero;

    public sealed override void UpdateWorldBoundingBox(Real dt = (Real)0.0)
    {
        JBoundingBox box;

        if (RigidBody == null)
        {
            CalculateBoundingBox(JQuaternion.Identity, JVector.Zero, out box);
            WorldBoundingBox = box;
            return;
        }

        ref var data = ref RigidBody.Data;
        CalculateBoundingBox(data.Orientation, data.Position, out box);
        WorldBoundingBox = box;
        // A pair the broad phase never offers cannot be swept, so any continuous mode has to reserve
        // the space the body is about to travel through.
        if (RigidBody.ContinuousDetection != ContinuousDetectionMode.Discrete) SweptExpandBoundingBox(dt);
    }

    public virtual void CalculateBoundingBox(in JQuaternion orientation, in JVector position, out JBoundingBox box)
    {
        ShapeHelper.CalculateBoundingBox(this, orientation, position, out box);
    }

    /// <summary>
    /// Calculates the mass and inertia of the shape. Can be overridden by child classes to improve
    /// performance or accuracy. The default implementation relies on an approximation of the shape
    /// constructed using the support map function.
    /// </summary>
    /// <remarks>
    /// The inertia tensor is computed relative to the coordinate system origin (0,0,0),
    /// not the center of mass.
    /// </remarks>
    [ReferenceFrame(ReferenceFrame.Local)]
    public virtual void CalculateMassInertia(out JMatrix inertia, out JVector com, out Real mass)
    {
        ShapeHelper.CalculateMassInertia(this, out inertia, out com, out mass);
    }

    /// <summary>
    /// Performs a local ray cast against the shape, checking if a ray originating from a specified point
    /// and traveling in a specified direction intersects with the object. It does not take into account the
    /// transformation of the associated rigid body.
    /// </summary>
    /// <param name="origin">The starting point of the ray.</param>
    /// <param name="direction">
    /// The direction of the ray. This vector does not need to be normalized.
    /// </param>
    /// <param name="normal">
    /// The surface normal at the point of intersection, if an intersection occurs.
    /// </param>
    /// <param name="lambda">
    /// The scalar value representing the distance along the ray's direction vector
    /// from the <paramref name="origin"/> to the intersection point. The hit point can be calculated as:
    /// <c>origin + lambda * direction</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if the ray intersects with the object; otherwise, <c>false</c>.
    /// </returns>
    [ReferenceFrame(ReferenceFrame.Local)]
    public virtual bool LocalRayCast(in JVector origin, in JVector direction, out JVector normal, out Real lambda)
    {
        return NarrowPhase.RayCast(this, origin, direction, out lambda, out normal);
    }

    [ReferenceFrame(ReferenceFrame.World)]
    public sealed override bool RayCast(in JVector origin, in JVector direction, out JVector normal, out Real lambda)
    {
        if (RigidBody == null)
        {
            return LocalRayCast(origin, direction, out normal, out lambda);
        }

        ref var data = ref RigidBody.Data;

        // rotate the ray into the reference frame of the body...
        JVector transformedDir = JVector.ConjugatedTransform(direction, data.Orientation);
        JVector transformedOrigin = JVector.ConjugatedTransform(origin - data.Position, data.Orientation);

        bool result = LocalRayCast(transformedOrigin, transformedDir, out normal, out lambda);

        // ...rotate back.
        JVector.Transform(normal, data.Orientation, out normal);

        return result;
    }

    [ReferenceFrame(ReferenceFrame.World)]
    public sealed override bool Sweep<T>(in T support, in JQuaternion orientation, in JVector position, in JVector sweep,
        out JVector pointA, out JVector pointB, out JVector normal, out Real lambda)
    {
        if (RigidBody == null)
        {
            bool hit = NarrowPhase.Sweep(this, support,
                orientation, position, sweep,
                out pointB, out pointA, out normal, out lambda);

            JVector.NegateInPlace(ref normal);
            return hit;
        }

        ref var data = ref RigidBody.Data;

        return NarrowPhase.Sweep(support, this,
            orientation, data.Orientation,
            position, data.Position,
            sweep, JVector.Zero,
            out pointA, out pointB, out normal, out lambda);
    }

    [ReferenceFrame(ReferenceFrame.World)]
    public sealed override bool Distance<T>(in T support, in JQuaternion orientation, in JVector position,
        out JVector pointA, out JVector pointB, out JVector normal, out Real distance)
    {
        if (RigidBody == null)
        {
            return NarrowPhase.Distance(support, this,
                orientation, JQuaternion.Identity,
                position, JVector.Zero,
                out pointA, out pointB, out normal, out distance);
        }

        ref var data = ref RigidBody.Data;

        return NarrowPhase.Distance(support, this,
            orientation, data.Orientation,
            position, data.Position,
            out pointA, out pointB, out normal, out distance);
    }
}
