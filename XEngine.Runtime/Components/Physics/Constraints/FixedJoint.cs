// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Jitter2;
using Jitter2.Dynamics;
using Jitter2.Dynamics.Constraints;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Welds two rigidbodies together, locking both position and rotation.
/// Matches Unity's <c>FixedJoint</c>. Built from Jitter2's <see cref="WeldJoint"/>.
/// </summary>
[AddComponentMenu("Physics/Joints/Fixed Joint")]
public class FixedJoint : PhysicsJoint
{
    [SerializeField] private Float3 anchor = Float3.Zero;
    [SerializeField] private SpringSettings spring;

    private WeldJoint weld;

    /// <summary>Anchor in local space of this rigidbody.</summary>
    public Float3 Anchor
    {
        get => anchor;
        set
        {
            anchor = value;
            RecreateConstraint();
        }
    }

    /// <summary>
    /// Optional spring on the weld. Frequency 0 (default) keeps the joint rigid.
    /// Softness/bias on the underlying constraints remain available through the weld.
    /// </summary>
    public SpringSettings Spring
    {
        get => spring;
        set
        {
            spring = value;
            ApplySpringSettings();
        }
    }

    protected override void CreateConstraint(World world, RigidBody body1, RigidBody body2)
    {
        Jitter2.LinearMath.JVector worldAnchor = LocalToWorld(anchor, Body1.Transform);
        weld = new WeldJoint(world, body1, body2, worldAnchor);
        joint = weld;
        ApplySpringSettings();
    }

    protected override void DestroyConstraint()
    {
        weld = null;
        base.DestroyConstraint();
    }

    private void ApplySpringSettings()
    {
        if (weld == null) return;
        ApplySpring(weld.BallSocket, spring, weld.Body1, weld.Body2);
        ApplySpring(weld.FixedAngle, spring, weld.Body1, weld.Body2);
    }

    /// <summary>A weld has no motion to show: mark the welded anchor point.</summary>
    public override void DrawGizmos()
    {
        if (Transform == null) return;
        DrawAnchorMarker(Transform.TransformPoint(anchor), GizmoScale, Color.Yellow);
    }
}
