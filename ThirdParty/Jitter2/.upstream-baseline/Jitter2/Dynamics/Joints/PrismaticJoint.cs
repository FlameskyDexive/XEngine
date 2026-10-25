/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using System;
using Jitter2.LinearMath;

namespace Jitter2.Dynamics.Constraints;

/// <summary>
/// Constructs a prismatic joint utilizing a <see cref="PointOnLine"/> constraint in conjunction with
/// <see cref="FixedAngle"/>, <see cref="HingeAngle"/>, and <see cref="LinearMotor"/> constraints.
/// </summary>
public class PrismaticJoint : Joint
{
    public RigidBody Body1 { get; private set; }
    public RigidBody Body2 { get; private set; }

    public PointOnLine Slider { get; }

    public FixedAngle? FixedAngle { get; }
    public HingeAngle? HingeAngle { get; }
    public LinearMotor? Motor { get; }

    /// <inheritdoc cref="PrismaticJoint(World, RigidBody, RigidBody, JVector, JVector, LinearLimit, bool, bool)"/>
    public PrismaticJoint(World world, RigidBody body1, RigidBody body2, JVector center, JVector axis,
        bool pinned = true, bool hasMotor = false) :
        this(world, body1, body2, center, axis, LinearLimit.Full, pinned, hasMotor)
    {
    }

    /// <summary>
    /// Initializes a new prismatic joint.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="center"/> contains a non-finite value, when <paramref name="axis"/> is zero
    /// or contains a non-finite value, when either limit value is NaN, when either body does not belong to
    /// <paramref name="world"/>, or when both body references are the same.
    /// </exception>
    public PrismaticJoint(World world, RigidBody body1, RigidBody body2, JVector center, JVector axis, LinearLimit limit,
        bool pinned = true, bool hasMotor = false)
    {
        Body1 = body1;
        Body2 = body2;

        ArgumentCheck.Finite(center, nameof(center));
        ArgumentCheck.NonZero(axis, nameof(axis));
        ArgumentCheck.NotNaN(limit.From, nameof(limit.From));
        ArgumentCheck.NotNaN(limit.To, nameof(limit.To));

        JVector.NormalizeInPlace(ref axis);

        Slider = world.CreateConstraint<PointOnLine>(body1, body2);
        Slider.Initialize(axis, center, center, limit);
        Register(Slider);

        if (pinned)
        {
            FixedAngle = world.CreateConstraint<FixedAngle>(body1, body2);
            FixedAngle.Initialize();
            Register(FixedAngle);
        }
        else
        {
            HingeAngle = world.CreateConstraint<HingeAngle>(body1, body2);
            HingeAngle.Initialize(axis, AngularLimit.Full);
            Register(HingeAngle);
        }

        if (hasMotor)
        {
            Motor = world.CreateConstraint<LinearMotor>(body1, body2);
            Motor.Initialize(axis, axis);
            Register(Motor);
        }
    }
}
