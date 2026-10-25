// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Jitter2;
using Jitter2.Dynamics;
using Jitter2.Dynamics.Constraints;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Base class for all physics constraints that connect two rigidbodies.
/// </summary>
[ComponentIcon("\uf0c1")] // Link inherited by all joints/constraints
public abstract class PhysicsConstraint : MonoBehaviour
{
    [SerializeField] protected Rigidbody3D connectedBody;
    [SerializeField] protected bool enabledOnStart = true;
    [SerializeField] protected bool enableCollision;

    private bool _constraintCollisionRegistered;

    /// <summary>
    /// The rigidbody connected by this constraint. If null, the constraint connects to the world.
    /// </summary>
    public Rigidbody3D ConnectedBody
    {
        get => connectedBody;
        set
        {
            if (connectedBody != value)
            {
                connectedBody = value;
                RecreateConstraint();
            }
        }
    }

    /// <summary>
    /// When true, the two connected rigidbodies still generate contacts with each other.
    /// Matches Unity's <c>Joint.enableCollision</c>. Default false: a jointed pair does not collide.
    /// </summary>
    public bool EnableCollision
    {
        get => enableCollision;
        set
        {
            if (enableCollision == value) return;
            enableCollision = value;
            UpdateConstraintCollisionPair();
        }
    }

    /// <summary>
    /// The first rigidbody (owner of this component).
    /// </summary>
    protected Rigidbody3D Body1 => GetComponentInParent<Rigidbody3D>();

    /// <summary>
    /// Gets or sets whether this constraint is active.
    /// </summary>
    public bool Active
    {
        get => GetConstraint()?.IsEnabled ?? false;
        set
        {
            Constraint constraint = GetConstraint();
            if (constraint != null) constraint.IsEnabled = value;
        }
    }

    public override void OnEnable()
    {
        RecreateConstraint();
    }

    public override void OnDisable()
    {
        UnregisterConstraintCollisionPair();
        DestroyConstraint();
    }

    public override void OnValidate()
    {
        if (GameObject?.Scene?.IsNotValid() ?? true) return;
        RecreateConstraint();
    }

    public override void DrawGizmos()
    {
        // TODO DrawGizmos
    }

    /// <summary>
    /// Gets the underlying Jitter2 constraint.
    /// </summary>
    protected abstract Constraint GetConstraint();

    /// <summary>
    /// Creates the constraint in the physics world.
    /// </summary>
    protected abstract void CreateConstraint(World world, RigidBody body1, RigidBody body2);

    /// <summary>
    /// Destroys the constraint.
    /// </summary>
    protected abstract void DestroyConstraint();

    /// <summary>
    /// Recreates the constraint with current settings.
    /// </summary>
    protected void RecreateConstraint()
    {
        DestroyConstraint();
        UnregisterConstraintCollisionPair();

        Rigidbody3D body1 = Body1;
        if (body1.IsNotValid() || body1._body == null || body1._body.Handle.IsZero)
            return;

        World world = GameObject.Scene.Physics.World;
        if (world == null) return;

        // If no connected body is specified, create a static body at the world origin
        RigidBody body2;
        if (connectedBody.IsNotValid() || connectedBody._body == null || connectedBody._body.Handle.IsZero)
        {
            // Create a temporary static body for world-space constraints
            body2 = world.CreateRigidBody();
            body2.MotionType = MotionType.Static;
        }
        else
        {
            body2 = connectedBody._body;
        }

        CreateConstraint(world, body1._body, body2);

        // Set initial enabled state
        Constraint constraint = GetConstraint();
        if (constraint != null)
        {
            constraint.IsEnabled = enabledOnStart;
        }

        UpdateConstraintCollisionPair();
    }

    private void UpdateConstraintCollisionPair()
    {
        UnregisterConstraintCollisionPair();
        if (!enableCollision) return;
        Rigidbody3D body1 = Body1;
        if (body1.IsNotValid() || connectedBody.IsNotValid()) return;
        body1.Scene.Physics.AddConstraintCollision(body1, connectedBody);
        _constraintCollisionRegistered = true;
    }

    private void UnregisterConstraintCollisionPair()
    {
        if (!_constraintCollisionRegistered) return;
        Body1?.Scene?.Physics?.RemoveConstraintCollision(Body1, connectedBody);
        _constraintCollisionRegistered = false;
    }

    /// <summary>
    /// Applies <paramref name="spring"/> to a constraint that exposes Softness/Bias, using the
    /// pair's reduced mass and the fixed timestep. No-op when the spring is rigid.
    /// </summary>
    protected static void ApplySpring(Constraint constraint, SpringSettings spring, RigidBody body1, RigidBody body2)
    {
        if (constraint == null || spring.IsRigid) return;
        float dt = Time.FixedDeltaTime;
        if (!spring.Compute(SpringSettings.EffectiveMass(body1, body2), dt, out float softness, out float bias))
            return;

        switch (constraint)
        {
            case BallSocket ball: ball.Softness = softness; ball.Bias = bias; break;
            case DistanceLimit distance: distance.Softness = softness; distance.Bias = bias; break;
            case FixedAngle angle: angle.Softness = softness; angle.Bias = bias; break;
            case ConeLimit cone: cone.Softness = softness; cone.Bias = bias; break;
            case TwistAngle twist: twist.Softness = softness; twist.Bias = bias; break;
        }
    }

    /// <summary>
    /// Converts a local position to world space.
    /// </summary>
    protected Jitter2.LinearMath.JVector LocalToWorld(Float3 localPos, Transform transform)
    {
        Float3 worldPos = transform.TransformPoint(localPos);
        return new Jitter2.LinearMath.JVector(worldPos.X, worldPos.Y, worldPos.Z);
    }

    /// <summary>
    /// Converts a local direction to world space.
    /// </summary>
    protected Jitter2.LinearMath.JVector LocalDirToWorld(Float3 localDir, Transform transform)
    {
        Float3 worldDir = transform.TransformDirection(localDir);
        return new Jitter2.LinearMath.JVector(worldDir.X, worldDir.Y, worldDir.Z);
    }

    // --- Gizmos (prowl-sync M2.5, ported from upstream 2875c929/a053ec5d/07a6d13a/cd1067d4) ---

    /// <summary>Reference size for joint gizmo markers, derived from the owning body's scale.</summary>
    protected float GizmoScale
    {
        get
        {
            Transform t = Transform;
            return t != null ? MathF.Max(0.25f, Float3.Length(t.LossyScale) * 0.25f) : 1f;
        }
    }

    /// <summary>Draws a point marker for a joint anchor: a wire sphere with a small inner core,
    /// so a lone anchor still reads as a point when the camera is far away.</summary>
    protected void DrawAnchorMarker(Float3 worldPos, float scale, Color color)
    {
        Debug.DrawWireSphere(worldPos, 0.12f * scale, color);
        Debug.DrawWireSphere(worldPos, 0.04f * scale, color);
    }

    /// <summary>Draws the span between the two anchors of a joint, with a marker on each end.</summary>
    protected void DrawAnchorPair(Float3 a, Float3 b, Color color)
    {
        Debug.DrawLine(a, b, color);
        DrawAnchorMarker(a, GizmoScale, color);
        DrawAnchorMarker(b, GizmoScale, color);
    }

    /// <summary>World position of the connected body's origin, or <paramref name="fallback"/> when
    /// no connected body is set (world-anchored constraint).</summary>
    protected Float3 ConnectedBodyPosition(Float3 fallback)
        => connectedBody != null && !connectedBody.IsNotValid() && connectedBody.Transform != null
            ? connectedBody.Transform.Position
            : fallback;
}
