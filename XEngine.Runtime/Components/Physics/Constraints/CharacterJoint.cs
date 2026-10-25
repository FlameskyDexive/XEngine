// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Jitter2;
using Jitter2.Dynamics;
using Jitter2.Dynamics.Constraints;
using Jitter2.LinearMath;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Ragdoll-style joint: ball socket plus swing (cone) and twist limits.
/// Matches Unity's <c>CharacterJoint</c>. Constraint-based characters stay a documented
/// alternative; gameplay characters use <see cref="CharacterController"/>.
/// </summary>
[AddComponentMenu("Physics/Joints/Character Joint")]
public class CharacterJoint : PhysicsJoint
{
    [SerializeField] private Float3 anchor = Float3.Zero;
    [SerializeField] private Float3 axis = Float3.UnitY;
    [SerializeField] private float swingLimit = 20f;
    [SerializeField] private float twistLimit = 20f;
    [SerializeField] private bool enableSwingLimit = true;
    [SerializeField] private bool enableTwistLimit = true;

    private BallSocket ballSocket;
    private ConeLimit coneLimit;
    private TwistAngle twistAngle;

    public Float3 Anchor
    {
        get => anchor;
        set { anchor = value; RecreateConstraint(); }
    }

    public Float3 Axis
    {
        get => axis;
        set { axis = value; RecreateConstraint(); }
    }

    public float SwingLimit
    {
        get => swingLimit;
        set { swingLimit = value; RecreateConstraint(); }
    }

    public float TwistLimit
    {
        get => twistLimit;
        set { twistLimit = value; RecreateConstraint(); }
    }

    public bool EnableSwingLimit
    {
        get => enableSwingLimit;
        set { enableSwingLimit = value; RecreateConstraint(); }
    }

    public bool EnableTwistLimit
    {
        get => enableTwistLimit;
        set { enableTwistLimit = value; RecreateConstraint(); }
    }

    protected override void CreateConstraint(World world, RigidBody body1, RigidBody body2)
    {
        JVector worldAnchor = LocalToWorld(anchor, Body1.Transform);
        JVector worldAxis = LocalDirToWorld(axis, Body1.Transform);

        ballSocket = world.CreateConstraint<BallSocket>(body1, body2);
        ballSocket.Initialize(worldAnchor);
        AddPart(ballSocket);

        if (enableSwingLimit)
        {
            coneLimit = world.CreateConstraint<ConeLimit>(body1, body2);
            coneLimit.Initialize(worldAxis, AngularLimit.FromDegree(-swingLimit, swingLimit));
            AddPart(coneLimit);
        }

        if (enableTwistLimit)
        {
            twistAngle = world.CreateConstraint<TwistAngle>(body1, body2);
            twistAngle.Initialize(worldAxis, worldAxis, AngularLimit.FromDegree(-twistLimit, twistLimit));
            AddPart(twistAngle);
        }
    }

    protected override void DestroyConstraint()
    {
        ballSocket = null;
        coneLimit = null;
        twistAngle = null;
        base.DestroyConstraint();
    }
}
