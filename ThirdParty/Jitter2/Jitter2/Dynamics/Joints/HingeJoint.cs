/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using System;
using Jitter2.LinearMath;

namespace Jitter2.Dynamics.Constraints;

/// <summary>
/// Constructs a hinge joint utilizing a <see cref="HingeAngle"/>, a <see cref="BallSocket"/>, and an optional <see cref="AngularMotor"/>.
/// </summary>
public class HingeJoint : Joint
{
    public RigidBody Body1 { get; private set; }
    public RigidBody Body2 { get; private set; }

    public HingeAngle HingeAngle { get; }
    public BallSocket BallSocket { get; }
    public AngularMotor? Motor { get; }

    /// <summary>
    /// Initializes a new hinge joint.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="hingeCenter"/> contains a non-finite value, when
    /// <paramref name="hingeAxis"/> is zero or contains a non-finite value, when either body does not belong
    /// to <paramref name="world"/>, or when both body references are the same.
    /// </exception>
    public HingeJoint(World world, RigidBody body1, RigidBody body2, JVector hingeCenter, JVector hingeAxis,
        AngularLimit angle, bool hasMotor = false)
    {
        Body1 = body1;
        Body2 = body2;

        ArgumentCheck.Finite(hingeCenter, nameof(hingeCenter));
        ArgumentCheck.NonZero(hingeAxis, nameof(hingeAxis));

        JVector.NormalizeInPlace(ref hingeAxis);

        HingeAngle = world.CreateConstraint<HingeAngle>(body1, body2);
        HingeAngle.Initialize(hingeAxis, angle);
        Register(HingeAngle);

        BallSocket = world.CreateConstraint<BallSocket>(body1, body2);
        BallSocket.Initialize(hingeCenter);
        Register(BallSocket);

        if (hasMotor)
        {
            Motor = world.CreateConstraint<AngularMotor>(body1, body2);
            Motor.Initialize(hingeAxis);
            Register(Motor);
        }
    }

    /// <inheritdoc cref="HingeJoint(World, RigidBody, RigidBody, JVector, JVector, AngularLimit, bool)"/>
    public HingeJoint(World world, RigidBody body1, RigidBody body2, JVector hingeCenter, JVector hingeAxis,
        bool hasMotor = false) :
        this(world, body1, body2, hingeCenter, hingeAxis, AngularLimit.Full, hasMotor)
    {
    }
}
