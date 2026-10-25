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
/// A springy distance joint. Matches Unity's <c>SpringJoint</c>.
/// Softness/bias remain settable; <see cref="Spring"/> is the frequency/damping parameterization.
/// </summary>
[AddComponentMenu("Physics/Joints/Spring Joint")]
public class SpringJoint : PhysicsConstraint
{
    [SerializeField] private Float3 anchor = Float3.Zero;
    [SerializeField] private Float3 connectedAnchor = Float3.Zero;
    [SerializeField] private float minDistance;
    [SerializeField] private float maxDistance;
    [SerializeField] private float tolerance = 0.025f;
    [SerializeField] private SpringSettings spring;
    [SerializeField] private float softness = 0.001f;
    [SerializeField] private float biasFactor = 0.2f;

    private DistanceLimit constraint;

    public Float3 Anchor
    {
        get => anchor;
        set { anchor = value; RecreateConstraint(); }
    }

    public Float3 ConnectedAnchor
    {
        get => connectedAnchor;
        set { connectedAnchor = value; RecreateConstraint(); }
    }

    /// <summary>Minimum distance. The rest length is <c>minDistance</c> when max equals min.</summary>
    public float MinDistance
    {
        get => minDistance;
        set { minDistance = value; RecreateConstraint(); }
    }

    /// <summary>Maximum distance. 0 means "use minDistance" (a rest-length spring).</summary>
    public float MaxDistance
    {
        get => maxDistance;
        set { maxDistance = value; RecreateConstraint(); }
    }

    /// <summary>Slack added to the limit, matching Unity's <c>SpringJoint.tolerance</c>.</summary>
    public float Tolerance
    {
        get => tolerance;
        set { tolerance = value; RecreateConstraint(); }
    }

    /// <summary>Frequency (Hz) and damping ratio. Frequency 0 falls back to Softness/Bias.</summary>
    public SpringSettings Spring
    {
        get => spring;
        set
        {
            spring = value;
            ApplySpringOrSoftness();
        }
    }

    public float Softness
    {
        get => softness;
        set
        {
            softness = value;
            if (constraint != null && spring.IsRigid) constraint.Softness = value;
        }
    }

    public float BiasFactor
    {
        get => biasFactor;
        set
        {
            biasFactor = value;
            if (constraint != null && spring.IsRigid) constraint.Bias = value;
        }
    }

    public float CurrentDistance => constraint?.Distance ?? 0f;

    protected override Constraint GetConstraint() => constraint;

    protected override void CreateConstraint(World world, RigidBody body1, RigidBody body2)
    {
        JVector worldAnchor1 = LocalToWorld(anchor, Body1.Transform);
        JVector worldAnchor2 = connectedBody.IsValid()
            ? LocalToWorld(connectedAnchor, connectedBody.Transform)
            : new JVector(connectedAnchor.X, connectedAnchor.Y, connectedAnchor.Z);

        float rest = Float3.Length(new Float3(worldAnchor2.X, worldAnchor2.Y, worldAnchor2.Z)
            - new Float3(worldAnchor1.X, worldAnchor1.Y, worldAnchor1.Z));
        float min = minDistance;
        float max = maxDistance > minDistance ? maxDistance : minDistance;
        if (max <= 0f && min <= 0f)
        {
            min = rest;
            max = rest;
        }

        constraint = world.CreateConstraint<DistanceLimit>(body1, body2);
        constraint.Initialize(worldAnchor1, worldAnchor2, new LinearLimit(min, max + tolerance));
        constraint.TargetDistance = min;
        ApplySpringOrSoftness();
    }

    protected override void DestroyConstraint()
    {
        if (constraint != null && !constraint.Handle.IsZero)
        {
            Body1._body.World.Remove(constraint);
            constraint = null;
        }
    }

    private void ApplySpringOrSoftness()
    {
        if (constraint == null) return;
        if (!spring.IsRigid)
        {
            ApplySpring(constraint, spring, constraint.Body1, constraint.Body2);
            return;
        }

        constraint.Softness = softness;
        constraint.Bias = biasFactor;
    }

    /// <summary>
    /// The spring is the gap between its two anchors: draw both ends, the span, and where the
    /// min/max limits sit along it.
    /// </summary>
    public override void DrawGizmos()
    {
        if (Transform == null) return;
        Float3 a = Transform.TransformPoint(anchor);
        Float3 b = ConnectedBodyPosition(a);
        if (connectedBody != null && !connectedBody.IsNotValid() && connectedBody.Transform != null)
            b = connectedBody.Transform.TransformPoint(connectedAnchor);

        Debug.DrawLine(a, b, Color.Cyan);
        DrawAnchorMarker(a, GizmoScale, Color.Yellow);
        DrawAnchorMarker(b, GizmoScale, Color.Orange);

        Float3 span = b - a;
        float length = Float3.Length(span);
        if (length <= 1e-5f) return;
        Float3 dir = span / length;
        if (float.IsFinite(minDistance))
            Debug.DrawLine(a + dir * minDistance, a + dir * (minDistance + 0.25f * GizmoScale), Color.Red);
        if (float.IsFinite(maxDistance))
            Debug.DrawLine(a + dir * maxDistance, a + dir * (maxDistance + 0.25f * GizmoScale), Color.Red);
    }
}
