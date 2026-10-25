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
/// Configurable joint built from Jitter2 atomic constraints.
/// Linear axes that are Locked use a ball socket; Limited uses a distance limit;
/// angular Locked uses a fixed angle, Limited uses a cone limit.
/// Per-axis linear lock (X locked / Y free) is not modelled — treat the three linear
/// axes as one group, same for the three angular axes.
/// </summary>
[AddComponentMenu("Physics/Joints/Configurable Joint")]
public class ConfigurableJoint : PhysicsJoint
{
    [SerializeField] private Float3 anchor = Float3.Zero;
    [SerializeField] private ConfigurableJointMotion xMotion = ConfigurableJointMotion.Locked;
    [SerializeField] private ConfigurableJointMotion yMotion = ConfigurableJointMotion.Locked;
    [SerializeField] private ConfigurableJointMotion zMotion = ConfigurableJointMotion.Locked;
    [SerializeField] private ConfigurableJointMotion angularXMotion = ConfigurableJointMotion.Locked;
    [SerializeField] private ConfigurableJointMotion angularYMotion = ConfigurableJointMotion.Locked;
    [SerializeField] private ConfigurableJointMotion angularZMotion = ConfigurableJointMotion.Locked;
    [SerializeField] private float linearLimit = 0.1f;
    [SerializeField] private float angularLimit = 45f;
    [SerializeField] private SpringSettings slerpDrive;

    private BallSocket ballSocket;
    private DistanceLimit distanceLimit;
    private FixedAngle fixedAngle;
    private ConeLimit coneLimit;

    public Float3 Anchor
    {
        get => anchor;
        set { anchor = value; RecreateConstraint(); }
    }

    public ConfigurableJointMotion XMotion { get => xMotion; set { xMotion = value; RecreateConstraint(); } }
    public ConfigurableJointMotion YMotion { get => yMotion; set { yMotion = value; RecreateConstraint(); } }
    public ConfigurableJointMotion ZMotion { get => zMotion; set { zMotion = value; RecreateConstraint(); } }
    public ConfigurableJointMotion AngularXMotion { get => angularXMotion; set { angularXMotion = value; RecreateConstraint(); } }
    public ConfigurableJointMotion AngularYMotion { get => angularYMotion; set { angularYMotion = value; RecreateConstraint(); } }
    public ConfigurableJointMotion AngularZMotion { get => angularZMotion; set { angularZMotion = value; RecreateConstraint(); } }

    public float LinearLimit { get => linearLimit; set { linearLimit = value; RecreateConstraint(); } }
    public float AngularLimit { get => angularLimit; set { angularLimit = value; RecreateConstraint(); } }

    public SpringSettings SlerpDrive
    {
        get => slerpDrive;
        set
        {
            slerpDrive = value;
            ApplyDrive();
        }
    }

    protected override void CreateConstraint(World world, RigidBody body1, RigidBody body2)
    {
        JVector worldAnchor = LocalToWorld(anchor, Body1.Transform);
        ConfigurableJointMotion linear = Strictest(xMotion, yMotion, zMotion);
        ConfigurableJointMotion angular = Strictest(angularXMotion, angularYMotion, angularZMotion);

        if (linear == ConfigurableJointMotion.Locked)
        {
            ballSocket = world.CreateConstraint<BallSocket>(body1, body2);
            ballSocket.Initialize(worldAnchor);
            AddPart(ballSocket);
        }
        else if (linear == ConfigurableJointMotion.Limited)
        {
            distanceLimit = world.CreateConstraint<DistanceLimit>(body1, body2);
            distanceLimit.Initialize(worldAnchor, worldAnchor, new LinearLimit(0f, linearLimit));
            AddPart(distanceLimit);
        }

        if (angular == ConfigurableJointMotion.Locked)
        {
            fixedAngle = world.CreateConstraint<FixedAngle>(body1, body2);
            fixedAngle.Initialize();
            AddPart(fixedAngle);
        }
        else if (angular == ConfigurableJointMotion.Limited)
        {
            coneLimit = world.CreateConstraint<ConeLimit>(body1, body2);
            coneLimit.Initialize(JVector.UnitY, Jitter2.Dynamics.Constraints.AngularLimit.FromDegree(-angularLimit, angularLimit));
            AddPart(coneLimit);
        }

        ApplyDrive();
    }

    protected override void DestroyConstraint()
    {
        ballSocket = null;
        distanceLimit = null;
        fixedAngle = null;
        coneLimit = null;
        base.DestroyConstraint();
    }

    private void ApplyDrive()
    {
        RigidBody b1 = Body1?._body;
        RigidBody b2 = connectedBody?._body;
        if (b1 == null) return;
        if (fixedAngle != null) ApplySpring(fixedAngle, slerpDrive, b1, b2 ?? b1);
        if (coneLimit != null) ApplySpring(coneLimit, slerpDrive, b1, b2 ?? b1);
        if (ballSocket != null) ApplySpring(ballSocket, slerpDrive, b1, b2 ?? b1);
        if (distanceLimit != null) ApplySpring(distanceLimit, slerpDrive, b1, b2 ?? b1);
    }

    private static ConfigurableJointMotion Strictest(ConfigurableJointMotion a, ConfigurableJointMotion b, ConfigurableJointMotion c)
    {
        if (a == ConfigurableJointMotion.Locked || b == ConfigurableJointMotion.Locked || c == ConfigurableJointMotion.Locked)
            return ConfigurableJointMotion.Locked;
        if (a == ConfigurableJointMotion.Limited || b == ConfigurableJointMotion.Limited || c == ConfigurableJointMotion.Limited)
            return ConfigurableJointMotion.Limited;
        return ConfigurableJointMotion.Free;
    }
}
