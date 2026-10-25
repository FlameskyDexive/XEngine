/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using System;
using Jitter2.LinearMath;

namespace Jitter2.Dynamics.Constraints;

/// <summary>
/// Creates a universal joint utilizing a <see cref="TwistAngle"/>, <see cref="BallSocket"/>, and an optional <see cref="AngularMotor"/>
/// constraint.
/// </summary>
public class UniversalJoint : Joint
{
    public RigidBody Body1 { get; private set; }
    public RigidBody Body2 { get; private set; }

    public TwistAngle TwistAngle { get; }
    public BallSocket BallSocket { get; }
    public AngularMotor? Motor { get; }

    /// <summary>
    /// Initializes a new universal joint.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="center"/> contains a non-finite value, when either rotation axis is zero
    /// or contains a non-finite value, when either body does not belong to <paramref name="world"/>,
    /// or when both body references are the same.
    /// </exception>
    public UniversalJoint(World world, RigidBody body1, RigidBody body2, JVector center, JVector rotateAxis1, JVector rotateAxis2, bool hasMotor = false)
    {
        Body1 = body1;
        Body2 = body2;

        ArgumentCheck.Finite(center, nameof(center));
        ArgumentCheck.NonZero(rotateAxis1, nameof(rotateAxis1));
        ArgumentCheck.NonZero(rotateAxis2, nameof(rotateAxis2));

        JVector.NormalizeInPlace(ref rotateAxis1);
        JVector.NormalizeInPlace(ref rotateAxis2);

        TwistAngle = world.CreateConstraint<TwistAngle>(body1, body2);
        TwistAngle.Initialize(rotateAxis1, rotateAxis2);
        Register(TwistAngle);

        BallSocket = world.CreateConstraint<BallSocket>(body1, body2);
        BallSocket.Initialize(center);
        Register(BallSocket);

        if (hasMotor)
        {
            Motor = world.CreateConstraint<AngularMotor>(body1, body2);
            Motor.Initialize(rotateAxis1, rotateAxis2);
            Register(Motor);
        }
    }
}
