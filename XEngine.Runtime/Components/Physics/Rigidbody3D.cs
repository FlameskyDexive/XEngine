// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Jitter2;
using Jitter2.Dynamics;
using Jitter2.LinearMath;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

[AddComponentMenu("Physics/Rigidbody")]
[ComponentIcon("\uf1b2")] // Cube
public sealed class Rigidbody3D : MonoBehaviour
{
    public class RigidBodyUserData
    {
        public Rigidbody3D Rigidbody { get; set; }
        public int InstanceID { get; set; }
        public int Layer { get; set; }
        //public bool HasTransformConstraints { get; set; }
        //public JVector RotationConstraint { get; set; }
        //public JVector TranslationConstraint { get; set; }
    }

    [SerializeField] private MotionType motionType = MotionType.Dynamic;

    // The boolean this grew out of. Kept only so a scene authored before CollisionDetectionMode existed
    // still gets its setting: MigrateSpeculativeFlag folds it into collisionDetection and clears it.
    [SerializeField] private bool isSpeculative;
    [SerializeField] private CollisionDetectionMode collisionDetection = CollisionDetectionMode.Discrete;
    [SerializeField] private bool useGravity = true;
    [SerializeField] private bool enableGyroscopicForces = false;
    [SerializeField] private float mass = 1;
    [SerializeField] private float linearDamping = 0.0f;
    [SerializeField] private float angularDamping = 0.0f;
    [SerializeField] private float friction = 0.2f;
    [SerializeField] private float restitution = 0;
    [SerializeField] private float deactivationTime = 1.0f;
    [SerializeField] private float linearSleepThreshold = 0.1f;
    [SerializeField] private float angularSleepThreshold = 0.1f;
    [SerializeField] private RigidbodyConstraints constraints = RigidbodyConstraints.None;
    [SerializeField] private RigidbodyInterpolation interpolation = RigidbodyInterpolation.None;

    // The simulated pose after the previous step and after the latest one. Interpolation presents a
    // point between them, which is why an interpolated Transform trails the simulation by up to one
    // fixed step. Only maintained while the mode asks for it.
    private JVector _previousPosition;
    private JVector _currentPosition;
    private JQuaternion _previousOrientation;
    private JQuaternion _currentOrientation;
    private bool _hasPoseHistory;

    // Transform.Version last pushed into the physics body, so SyncTransformToBody only pushes when
    // the user actually edited the Transform (not when the physics readback wrote it).
    private uint _lastSyncedTransformVersion;

    /// <summary>
    /// How this body participates in the simulation: <see cref="MotionType.Dynamic"/>,
    /// <see cref="MotionType.Kinematic"/>, or <see cref="MotionType.Static"/>.
    /// </summary>
    public MotionType MotionType
    {
        get => motionType;
        set
        {
            motionType = value;
            if (_body != null) _body.MotionType = value;
        }
    }

    /// <summary>
    /// Gets or sets how hard this body works to avoid passing through obstacles within one step.
    /// </summary>
    public CollisionDetectionMode CollisionDetectionMode
    {
        get => collisionDetection;
        set
        {
            collisionDetection = value;
            if (_body != null) _body.ContinuousDetection = (ContinuousDetectionMode)value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether speculative contacts are enabled.
    /// </summary>
    /// <remarks>
    /// A view over <see cref="CollisionDetectionMode"/>, which this switch grew into. Setting it selects
    /// <see cref="CollisionDetectionMode.ContinuousSpeculative"/> — sweep against every partner — which
    /// is what the boolean has always meant.
    /// </remarks>
    public bool EnableSpeculativeContacts
    {
        get => collisionDetection != CollisionDetectionMode.Discrete;
        set => CollisionDetectionMode = value
            ? CollisionDetectionMode.ContinuousSpeculative
            : CollisionDetectionMode.Discrete;
    }

    /// <summary>
    /// Folds the pre-<see cref="CollisionDetectionMode"/> boolean into the mode, once.
    /// </summary>
    private void MigrateSpeculativeFlag()
    {
        if (!isSpeculative) return;

        // Only when the mode is still at its default, so a scene that set both does not have its
        // explicit mode overwritten by the legacy flag.
        if (collisionDetection == CollisionDetectionMode.Discrete)
            collisionDetection = CollisionDetectionMode.ContinuousSpeculative;

        isSpeculative = false;
    }

    /// <summary>
    /// Gets or sets a value indicating whether this Rigidbody3D is affected by gravity.
    /// </summary>
    public bool AffectedByGravity
    {
        get => useGravity;
        set
        {
            useGravity = value;
            if (_body != null) _body.AffectedByGravity = value;
        }
    }

    /// <inheritdoc cref="AffectedByGravity"/>
    /// <remarks>Unity's name for <see cref="AffectedByGravity"/>.</remarks>
    public bool UseGravity
    {
        get => AffectedByGravity;
        set => AffectedByGravity = value;
    }

    /// <summary>
    /// Gets or sets whether this body is moved by scripts rather than by the simulation.
    /// </summary>
    /// <remarks>
    /// Unity's spelling of <see cref="MotionType"/>, which has a third state this boolean cannot
    /// express. Clearing it returns the body to <see cref="MotionType.Dynamic"/>, so it will not turn a
    /// <see cref="MotionType.Static"/> body kinematic and back again — read <see cref="MotionType"/>
    /// directly if the distinction matters.
    /// </remarks>
    public bool IsKinematic
    {
        get => motionType == MotionType.Kinematic;
        set => MotionType = value ? MotionType.Kinematic : MotionType.Dynamic;
    }

    /// <summary>
    /// Gets or sets the mass of this Rigidbody3D.
    /// </summary>
    public float Mass
    {
        get => mass;
        set
        {
            if (value <= 0.0)
                throw new ArgumentException("Mass can not be zero or negative.", nameof(value));

            mass = value;
            if (_body != null) _body.SetMassInertia(value);
        }
    }

    /// <summary>
    /// Gets or sets the friction of this Rigidbody3D.
    /// </summary>
    public float Friction
    {
        get => friction;
        set
        {
            if (value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(nameof(value), "Restitution must be between 0 and 1.");

            friction = value;
            if (_body != null) _body.Friction = value;
        }
    }

    /// <summary>
    /// Gets or sets the restitution of this Rigidbody3D.
    /// </summary>
    public float Restitution
    {
        get => restitution;
        set
        {
            if (value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(nameof(value), "Restitution must be between 0 and 1.");

            restitution = value;
            if (_body != null) _body.Restitution = value;
        }
    }

    /// <summary>
    /// Gets or sets the linear damping of this Rigidbody3D.
    /// Higher values slow down linear movement faster. Range: 0 to 1.
    /// </summary>
    public float LinearDamping
    {
        get => linearDamping;
        set
        {
            if (value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(nameof(value), "Linear damping must be between 0 and 1.");

            linearDamping = value;
            if (_body != null) _body.Damping = (linearDamping, _body.Damping.angular);
        }
    }

    /// <summary>
    /// Gets or sets the angular damping of this Rigidbody3D.
    /// Higher values slow down rotation faster. Range: 0 to 1.
    /// </summary>
    public float AngularDamping
    {
        get => angularDamping;
        set
        {
            if (value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(nameof(value), "Angular damping must be between 0 and 1.");

            angularDamping = value;
            if (_body != null) _body.Damping = (_body.Damping.linear, angularDamping);
        }
    }

    /// <inheritdoc cref="LinearDamping"/>
    /// <remarks>Unity's name for <see cref="LinearDamping"/>.</remarks>
    public float Drag
    {
        get => LinearDamping;
        set => LinearDamping = value;
    }

    /// <inheritdoc cref="AngularDamping"/>
    /// <remarks>Unity's name for <see cref="AngularDamping"/>.</remarks>
    public float AngularDrag
    {
        get => AngularDamping;
        set => AngularDamping = value;
    }

    /// <summary>
    /// Gets or sets whether gyroscopic forces are enabled for this Rigidbody3D.
    /// Useful for spinning objects with high inertia anisotropy (like propellers).
    /// </summary>
    public bool EnableGyroscopicForces
    {
        get => enableGyroscopicForces;
        set
        {
            enableGyroscopicForces = value;
            if (_body != null) _body.EnableGyroscopicForces = value;
        }
    }

    /// <summary>
    /// Gets or sets the deactivation time in seconds.
    /// The body sleeps if velocity stays below threshold for this duration.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the value is negative.</exception>
    public float DeactivationTime
    {
        get => deactivationTime;
        set
        {
            // The kernel throws on a negative duration, and would do so from inside the setter below
            // rather than here, where the caller can see which value it objected to.
            if (value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value), "Deactivation time can not be negative.");

            deactivationTime = value;
            if (_body != null) _body.DeactivationTime = TimeSpan.FromSeconds(value);
        }
    }

    /// <summary>
    /// Gets or sets the linear velocity threshold for sleeping.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the value is negative.</exception>
    public float LinearSleepThreshold
    {
        get => linearSleepThreshold;
        set
        {
            if (value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value), "Sleep threshold can not be negative.");

            linearSleepThreshold = value;
            if (_body != null)
                _body.DeactivationThreshold = (angular: _body.DeactivationThreshold.angular, linear: value);
        }
    }

    /// <summary>
    /// Gets or sets the angular velocity threshold for sleeping (radians/second).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the value is negative.</exception>
    public float AngularSleepThreshold
    {
        get => angularSleepThreshold;
        set
        {
            if (value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value), "Sleep threshold can not be negative.");

            angularSleepThreshold = value;
            if (_body != null)
                _body.DeactivationThreshold = (angular: value, linear: _body.DeactivationThreshold.linear);
        }
    }

    /// <summary>
    /// Gets or sets the world-space axes this body may not move along.
    /// </summary>
    /// <remarks>
    /// A frozen axis holds against every source of motion — gravity, forces, contacts, joints, and
    /// velocity assigned directly — rather than being a force that competes with them.
    /// </remarks>
    public RigidbodyConstraints Constraints
    {
        get => constraints;
        set
        {
            constraints = value;
            if (_body != null) _body.LockedAxes = ToAxisLock(value);
        }
    }

    private static AxisLock ToAxisLock(RigidbodyConstraints value)
    {
        AxisLock locked = AxisLock.None;

        if ((value & RigidbodyConstraints.FreezePositionX) != 0) locked |= AxisLock.LinearX;
        if ((value & RigidbodyConstraints.FreezePositionY) != 0) locked |= AxisLock.LinearY;
        if ((value & RigidbodyConstraints.FreezePositionZ) != 0) locked |= AxisLock.LinearZ;

        if ((value & RigidbodyConstraints.FreezeRotationX) != 0) locked |= AxisLock.AngularX;
        if ((value & RigidbodyConstraints.FreezeRotationY) != 0) locked |= AxisLock.AngularY;
        if ((value & RigidbodyConstraints.FreezeRotationZ) != 0) locked |= AxisLock.AngularZ;

        return locked;
    }

    /// <summary>
    /// Gets or sets how the Transform is smoothed between fixed physics steps.
    /// </summary>
    public RigidbodyInterpolation Interpolation
    {
        get => interpolation;
        set
        {
            if (interpolation == value) return;
            interpolation = value;
            // Whatever history was collected belongs to the old mode, and interpolating out of a stale
            // pair would slide the body backwards for one frame.
            _hasPoseHistory = false;
        }
    }

    /// <summary>
    /// Records the simulated pose for interpolation. Called by the physics world after each step.
    /// </summary>
    internal void CapturePose()
    {
        if (interpolation != RigidbodyInterpolation.Interpolate) return;
        if (_body == null || _body.Handle.IsZero) return;

        if (_hasPoseHistory)
        {
            _previousPosition = _currentPosition;
            _previousOrientation = _currentOrientation;
        }
        else
        {
            // First capture: both ends of the segment are the current pose, so the body renders where
            // it is rather than sliding in from wherever the history happened to be uninitialized.
            _previousPosition = _body.Position;
            _previousOrientation = _body.Orientation;
            _hasPoseHistory = true;
        }

        _currentPosition = _body.Position;
        _currentOrientation = _body.Orientation;
    }

    /// <summary>
    /// Gets whether this Rigidbody3D is currently active (not sleeping).
    /// </summary>
    public bool IsActive => _body?.IsActive ?? false;

    /// <summary>
    /// Gets or sets the Linear Velocity of this Rigidbody3D.
    /// </summary>
    public Float3 LinearVelocity
    {
        get => _body == null ? Float3.Zero : new(_body.Velocity.X, _body.Velocity.Y, _body.Velocity.Z);
        set { EnsureBody(); if (_body != null) _body.Velocity = new(value.X, value.Y, value.Z); }
    }

    /// <inheritdoc cref="LinearVelocity"/>
    /// <remarks>Unity's name for <see cref="LinearVelocity"/>.</remarks>
    public Float3 Velocity
    {
        get => LinearVelocity;
        set => LinearVelocity = value;
    }

    /// <summary>
    /// Gets or sets the Angular Velocity of this Rigidbody3D.
    /// </summary>
    public Float3 AngularVelocity
    {
        get => _body == null ? Float3.Zero : new(_body.AngularVelocity.X, _body.AngularVelocity.Y, _body.AngularVelocity.Z);
        set { EnsureBody(); if (_body != null) _body.AngularVelocity = new(value.X, value.Y, value.Z); }
    }

    /// <summary>
    /// Gets or sets the Torque of this Rigidbody3D.
    /// </summary>
    public Float3 Torque
    {
        get => _body == null ? Float3.Zero : new(_body.Torque.X, _body.Torque.Y, _body.Torque.Z);
        set { EnsureBody(); if (_body != null) _body.Torque = new JVector(value.X, value.Y, value.Z); }
    }

    /// <summary>
    /// Event triggered when this rigidbody begins colliding with another rigidbody.
    /// </summary>
    public event Action<Rigidbody3D, ContactInfo> BeginCollide;

    /// <summary>
    /// Event triggered when this rigidbody stops colliding with another rigidbody.
    /// </summary>
    public event Action<Rigidbody3D> EndCollide;

    /// <summary>
    /// Information about a collision contact.
    /// </summary>
    public struct ContactInfo
    {
        public Float3 Point;
        public Float3 Normal;
        public float ImpulseMagnitude;
    }

    [SerializeIgnore]
    internal RigidBody _body;

    /// <summary>
    /// Ensures the underlying Jitter body exists. Body creation normally happens in OnEnable, but
    /// game code can touch a rigidbody (e.g. set velocity or add a force) in the same frame it is
    /// added, before OnEnable runs create it on demand so those calls don't hit a null body.
    /// </summary>
    private void EnsureBody()
    {
        if (_body != null && !_body.Handle.IsZero) return;
        World? world = GameObject?.Scene?.Physics?.World;
        if (world != null) CreateBody(world);
    }

    public RigidBody CreateBody(World world)
    {
        _body = world.CreateRigidBody();
        UpdateProperties(_body);
        UpdateShapes(_body);
        UpdateTransform(_body);
        _lastSyncedTransformVersion = Transform.Version; // initial pose is already in the body
        GameObject?.Scene?.Physics?.RegisterBody(this);
        _body.Tag = new RigidBodyUserData()
        {
            Rigidbody = this,
            InstanceID = this.InstanceID,
            Layer = GameObject.LayerIndex,
            //HasTransformConstraints = rotationConstraints != Vector3Int.one || translationConstraints != Vector3Int.one,
            //RotationConstraint = new JVector(rotationConstraints.x, rotationConstraints.y, rotationConstraints.z),
            //TranslationConstraint = new JVector(translationConstraints.x, translationConstraints.y, translationConstraints.z)
        };

        // Hook up collision events
        _body.BeginCollide += OnJitterBeginCollide;
        _body.EndCollide += OnJitterEndCollide;

        return _body;
    }

    private void OnJitterBeginCollide(Arbiter arbiter)
    {
        // Get the other body in the collision
        var otherBody = arbiter.Body1 == _body ? arbiter.Body2 : arbiter.Body1;

        if (otherBody.Tag is RigidBodyUserData userData && userData.Rigidbody != null)
        {
            // Get contact information from the first contact point if available
            var contact = arbiter.Handle.Data.Contact0;
            var normal = contact.Normal;
            var relPos = contact.RelativePosition2;

            // Calculate world position of contact
            var worldPos = otherBody.Position + relPos;

            var contactInfo = new ContactInfo
            {
                Point = new Float3(worldPos.X, worldPos.Y, worldPos.Z),
                Normal = new Float3(normal.X, normal.Y, normal.Z),
                ImpulseMagnitude = contact.Impulse
            };

            BeginCollide?.Invoke(userData.Rigidbody, contactInfo);
        }
    }

    private void OnJitterEndCollide(Arbiter arbiter)
    {
        // Get the other body in the collision
        var otherBody = arbiter.Body1 == _body ? arbiter.Body2 : arbiter.Body1;

        if (otherBody.Tag is RigidBodyUserData userData && userData.Rigidbody != null)
        {
            EndCollide?.Invoke(userData.Rigidbody);
        }
    }

    public override void OnValidate()
    {
        if (GameObject?.Scene?.IsNotValid() ?? true) return;

        if (_body == null || _body.Handle.IsZero)
            _body = GameObject.Scene.Physics.World.CreateRigidBody();

        UpdateProperties(_body);
        UpdateShapes(_body);
        UpdateTransform(_body);
    }

    public override void Update()
    {
        if (_body == null || _body.Handle.IsZero) return;

        // Dynamic AND kinematic bodies move within the simulation (kinematic via LinearVelocity /
        // MovePosition), so the transform must follow the body. Only static bodies don't move - writing
        // their pose back would clobber a script-set transform. (This body->transform readback is
        // independent of AutoSyncTransforms, which only governs the transform->body direction.)
        if (motionType == MotionType.Static) return;

        ResolvePresentationPose(out JVector position, out JQuaternion orientation);

        Transform.Position = new Float3(position.X, position.Y, position.Z);
        Transform.Rotation = new Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);

        // Remember the version we just wrote so the transform->body sync doesn't treat this
        // physics-driven change as a user edit and push it straight back.
        _lastSyncedTransformVersion = Transform.Version;
    }

    /// <summary>
    /// The pose to show this frame, which is the simulated pose only when interpolation is off.
    /// </summary>
    private void ResolvePresentationPose(out JVector position, out JQuaternion orientation)
    {
        // How far this frame sits past the last step. Bounded to one step by construction, which is the
        // point: driving this off a timer accumulated in Update instead lets it exceed a full step (or
        // grow without bound on frames where no step ran), and the body then races ahead of the
        // simulation and is yanked back.
        float alpha = Time.FixedAlpha;

        switch (interpolation)
        {
            case RigidbodyInterpolation.Interpolate when _hasPoseHistory:
                position = _previousPosition + (_currentPosition - _previousPosition) * alpha;
                orientation = JQuaternion.Slerp(_previousOrientation, _currentOrientation, alpha);
                return;

            case RigidbodyInterpolation.Extrapolate:
                _body.PredictPose(alpha * Time.FixedDeltaTime, out position, out orientation);
                return;

            default:
                position = _body.Position;
                orientation = _body.Orientation;
                return;
        }
    }

    public override void DrawGizmos()
    {
        // TODO DrawGizmos
    }

    public override void OnEnable()
    {
        if (_body == null || _body.Handle.IsZero)
        {
            CreateBody(GameObject.Scene.Physics.World);
        }

        // Claim all child colliders that aren't already claimed
        ClaimChildColliders();
    }

    /// <summary>
    /// Claims all colliders in this GameObject and its children that aren't already claimed by another rigidbody.
    /// </summary>
    private void ClaimChildColliders()
    {
        if (_body == null || _body.Handle.IsZero)
            return;

        // Get all colliders in this GameObject and its children
        var colliders = GetComponentsInChildren<Collider>();

        foreach (var collider in colliders)
        {
            // Try to attach the collider to this rigidbody
            // This will fail if the collider is already claimed by another rigidbody
            collider.TryAttachTo(this);
        }
    }

    public override void OnDisable()
    {
        if (_body != null && !_body.Handle.IsZero)
        {
            // Detach all child colliders - they will attach to the static rigidbody when they re-enable
            var colliders = GetComponentsInChildren<Collider>();
            foreach (var collider in colliders)
            {
                if (collider.IsValid() && collider.Enabled)
                {
                    collider.Detach();
                    // Re-enable the collider so it attaches to the static rigidbody
                    collider.OnEnable();
                }
            }

            // Unhook collision events
            _body.BeginCollide -= OnJitterBeginCollide;
            _body.EndCollide -= OnJitterEndCollide;

            GameObject.Scene.Physics.UnregisterBody(this);
            GameObject.Scene.Physics.World?.Remove(_body);
        }
    }

    internal void UpdateProperties(RigidBody rb)
    {
        MigrateSpeculativeFlag();

        rb.MotionType = motionType;
        rb.ContinuousDetection = (ContinuousDetectionMode)collisionDetection;
        rb.Damping = (linearDamping, angularDamping);
        rb.Friction = friction;
        rb.AffectedByGravity = useGravity;
        rb.Restitution = restitution;
        rb.EnableGyroscopicForces = enableGyroscopicForces;
        rb.DeactivationTime = TimeSpan.FromSeconds(deactivationTime < 0f ? 0f : deactivationTime);
        // Named, because the kernel spells this tuple (angular, linear) while Damping just above is
        // (linear, angular). Positionally these two lines disagree about which is which.
        rb.DeactivationThreshold = (
            angular: angularSleepThreshold < 0f ? 0f : angularSleepThreshold,
            linear: linearSleepThreshold < 0f ? 0f : linearSleepThreshold);
        rb.LockedAxes = ToAxisLock(constraints);
        rb.Tag = new RigidBodyUserData()
        {
            Rigidbody = this,
            InstanceID = this.InstanceID,
            Layer = GameObject.LayerIndex,
        };
        // Mass/inertia is set by RegisterShapes after colliders attach their shapes.
        // Calling rb.SetMassInertia(mass) here would iterate all currently-attached shapes,
        // which throws for TriangleShape (no volume). The fallback in UpdateShapes handles
        // the no-collider case.
    }

    internal void UpdateShapes(RigidBody rb)
    {
        // Remove all shapes from this rigidbody (Preserve mass/inertia, the rebuild below will refresh it)
        rb.RemoveShapes(rb.Shapes, MassInertiaUpdateMode.Preserve);

        // Get all child colliders and have them re-attach
        var colliders = GetComponentsInChildren<Collider>();
        foreach (var collider in colliders)
        {
            // Detach the collider first (in case it's attached to us or static)
            collider.Detach();
            // Then try to attach it to this rigidbody
            collider.TryAttachTo(this);
        }

        // If no colliders provided shapes, RegisterShapes was never called and mass was never set.
        // Safe to call here because the body has no shapes attached.
        if (rb.Shapes.Count == 0)
            rb.SetMassInertia(mass);
    }

    /// <summary>Unconditionally pushes the Transform pose into the body. WHEN this runs is controlled
    /// by the caller (initial creation, the pre-step sync, or an auto-synced query).</summary>
    internal void UpdateTransform(RigidBody rb)
    {
        rb.Position = new JVector(Transform.Position.X, Transform.Position.Y, Transform.Position.Z);
        rb.Orientation = ToUnitJQuaternion(Transform.Rotation);
        // The pose just jumped, so the recorded history describes a segment the body never travelled.
        // Interpolating across it would draw the body sliding from its old place to its new one.
        _hasPoseHistory = false;
    }

    /// <summary>
    /// Pushes the Transform into the physics body, but only if the Transform changed since the last
    /// sync (a genuine user edit) - so it never clobbers the simulated pose. Called by the physics
    /// world before each step and, when AutoSyncTransforms is on, before queries.
    /// </summary>
    internal void SyncTransformToBody()
    {
        if (_body == null || _body.Handle.IsZero) return;
        if (Transform.Version == _lastSyncedTransformVersion) return;
        UpdateTransform(_body);
        _lastSyncedTransformVersion = Transform.Version;
    }

    public void AddForce(Float3 velocity)
    {
        EnsureBody();
        if (_body != null) _body.AddForce(new JVector(velocity.X, velocity.Y, velocity.Z));
    }

    /// <summary>
    /// Applies a linear effect to the body, interpreted according to <paramref name="mode"/>.
    /// </summary>
    /// <remarks>
    /// The instant modes change velocity here and now and wake the body; the continuous modes queue into
    /// the force accumulator, which the simulation turns into a velocity delta at the end of one step and
    /// integrates during the next — so a queued force shows up in <see cref="LinearVelocity"/> two steps
    /// after the call, not one. That is the kernel's pipelining, and gravity arrives the same way.
    /// Ignored for non-dynamic bodies, which the simulation moves rather than pushes.
    /// </remarks>
    public void AddForce(Float3 force, ForceMode mode)
    {
        EnsureBody();
        if (_body == null || _body.Data.InverseMass <= 0f) return;

        var value = new JVector(force.X, force.Y, force.Z);

        switch (mode)
        {
            case ForceMode.Force:
                _body.AddForce(value);
                break;

            case ForceMode.Acceleration:
                _body.AddForce(value * _body.Mass);
                break;

            case ForceMode.Impulse:
                _body.Velocity += value * _body.Data.InverseMass;
                SetActive(true);
                break;

            case ForceMode.VelocityChange:
                _body.Velocity += value;
                SetActive(true);
                break;
        }
    }

    public void AddForceAtPosition(Float3 velocity, Float3 worldPosition)
    {
        EnsureBody();
        if (_body != null) _body.AddForce(new JVector(velocity.X, velocity.Y, velocity.Z), new JVector(worldPosition.X, worldPosition.Y, worldPosition.Z));
    }

    /// <summary>
    /// Applies a force in this body's local space.
    /// </summary>
    public void AddRelativeForce(Float3 force, ForceMode mode = ForceMode.Force)
        => AddForce(Rotation * force, mode);

    public void AddTorque(Float3 torque)
    {
        Torque += torque;
    }

    /// <summary>
    /// Applies an angular effect to the body, interpreted according to <paramref name="mode"/>.
    /// </summary>
    /// <remarks>
    /// The angular counterpart of <see cref="AddForce(Float3, ForceMode)"/>, with the inertia tensor
    /// playing the part mass plays there. The mass-independent modes therefore need the tensor itself
    /// rather than its inverse, and give up if it cannot be inverted — which is the case for a body whose
    /// rotation is fully locked by <see cref="Constraints"/>.
    /// </remarks>
    public void AddTorque(Float3 torque, ForceMode mode)
    {
        EnsureBody();
        if (_body == null || _body.Data.InverseMass <= 0f) return;

        var value = new JVector(torque.X, torque.Y, torque.Z);

        switch (mode)
        {
            case ForceMode.Force:
                _body.Torque += value;
                break;

            case ForceMode.Acceleration:
                if (JMatrix.Inverse(_body.Data.InverseInertiaWorld, out JMatrix inertia))
                    _body.Torque += JVector.Transform(value, inertia);
                break;

            case ForceMode.Impulse:
                _body.AngularVelocity += JVector.Transform(value, _body.Data.InverseInertiaWorld);
                SetActive(true);
                break;

            case ForceMode.VelocityChange:
                _body.AngularVelocity += value;
                SetActive(true);
                break;
        }
    }

    /// <summary>
    /// Applies a torque in this body's local space.
    /// </summary>
    public void AddRelativeTorque(Float3 torque, ForceMode mode = ForceMode.Force)
        => AddTorque(Rotation * torque, mode);

    /// <summary>
    /// Sets the activation state of this rigidbody (awake or sleeping).
    /// </summary>
    /// <remarks>
    /// Takes effect at the start of the next step rather than immediately, because activation runs per
    /// simulation island: a body cannot be woken without waking whatever it is resting on.
    /// </remarks>
    public void SetActive(bool active)
    {
        if (_body != null)
            _body.SetActivationState(active);
    }

    /// <summary>
    /// Asks the simulation to stop tracking this body until something disturbs it.
    /// </summary>
    /// <remarks>Unity's name for <c>SetActive(false)</c>. Deferred to the next step — see
    /// <see cref="SetActive"/>.</remarks>
    public void Sleep() => SetActive(false);

    /// <summary>
    /// Asks the simulation to resume tracking this body.
    /// </summary>
    /// <remarks>Unity's name for <c>SetActive(true)</c>. Deferred to the next step — see
    /// <see cref="SetActive"/>.</remarks>
    public void WakeUp() => SetActive(true);

    /// <summary>
    /// Whether this body is currently asleep.
    /// </summary>
    /// <remarks>
    /// Unity's inverse of <see cref="IsActive"/>. A body with no simulation body yet reads as sleeping,
    /// which is the safer answer of the two for code deciding whether to nudge it.
    /// </remarks>
    public bool IsSleeping() => !IsActive;

    /// <summary>
    /// Gets the velocity at a world space point on this rigidbody.
    /// </summary>
    public Float3 GetPointVelocity(Float3 worldPoint)
    {
        if (_body == null) return Float3.Zero;

        var point = new JVector(worldPoint.X, worldPoint.Y, worldPoint.Z);
        JVector r = point - _body.Position;
        JVector velocity = _body.Velocity + JVector.Cross(_body.AngularVelocity, r);

        return new Float3(velocity.X, velocity.Y, velocity.Z);
    }

    /// <summary>
    /// Gets the center of mass in world space.
    /// </summary>
    public Float3 CenterOfMass
    {
        get
        {
            if (_body == null) return Transform.Position;
            JVector pos = _body.Position;
            return new Float3(pos.X, pos.Y, pos.Z);
        }
    }

    /// <summary>
    /// Gets the inertia tensor (inverse) of this rigidbody.
    /// </summary>
    public Float3 InertiaTensor
    {
        get
        {
            if (_body == null) return Float3.One;
            JMatrix inertia = _body.InverseInertia;
            return new Float3(
                inertia.M11 != 0 ? 1.0f / inertia.M11 : 0,
                inertia.M22 != 0 ? 1.0f / inertia.M22 : 0,
                inertia.M33 != 0 ? 1.0f / inertia.M33 : 0
            );
        }
    }

    /// <summary>
    /// Applies an impulse at a position, immediately affecting velocity.
    /// </summary>
    public void ApplyImpulse(Float3 impulse, Float3 worldPosition)
    {
        if (_body == null) return;

        var jImpulse = new JVector(impulse.X, impulse.Y, impulse.Z);
        var jPosition = new JVector(worldPosition.X, worldPosition.Y, worldPosition.Z);

        JVector r = jPosition - _body.Position;
        _body.Velocity += jImpulse * _body.Data.InverseMass;
        _body.AngularVelocity += JVector.Transform(JVector.Cross(r, jImpulse), _body.Data.InverseInertiaWorld);

        SetActive(true);
    }

    /// <summary>
    /// Applies an impulse to the rigidbody, immediately affecting velocity.
    /// </summary>
    public void ApplyImpulse(Float3 impulse)
    {
        if (_body == null) return;

        var jImpulse = new JVector(impulse.X, impulse.Y, impulse.Z);
        _body.Velocity += jImpulse * _body.Data.InverseMass;

        SetActive(true);
    }

    /// <summary>
    /// Applies an angular impulse to the rigidbody, immediately affecting angular velocity.
    /// </summary>
    public void ApplyAngularImpulse(Float3 angularImpulse)
    {
        if (_body == null) return;

        var jImpulse = new JVector(angularImpulse.X, angularImpulse.Y, angularImpulse.Z);
        _body.AngularVelocity += JVector.Transform(jImpulse, _body.Data.InverseInertiaWorld);

        SetActive(true);
    }

    /// <summary>
    /// Gets or sets the simulated position, bypassing the Transform.
    /// </summary>
    /// <remarks>
    /// Unity's <c>Rigidbody.position</c>. Reading it gives the pose the solver last produced, which is
    /// not the same as <c>Transform.Position</c> while <see cref="Interpolation"/> is smoothing. Writing
    /// it is a teleport, so it discards interpolation history rather than drawing the body sliding along
    /// a path it never took.
    /// </remarks>
    public Float3 Position
    {
        get => _body == null ? Transform.Position : new Float3(_body.Position.X, _body.Position.Y, _body.Position.Z);
        set => MovePosition(value);
    }

    /// <summary>
    /// Gets or sets the simulated rotation, bypassing the Transform.
    /// </summary>
    /// <remarks>Unity's <c>Rigidbody.rotation</c>. See <see cref="Position"/> for why this is not the
    /// same as reading the Transform.</remarks>
    public Quaternion Rotation
    {
        get => _body == null
            ? Transform.Rotation
            : new Quaternion(_body.Orientation.X, _body.Orientation.Y, _body.Orientation.Z, _body.Orientation.W);
        set => MoveRotation(value);
    }

    /// <summary>
    /// Moves the rigidbody to a new position (teleport).
    /// </summary>
    public void MovePosition(Float3 position)
    {
        if (_body != null)
        {
            _body.Position = new JVector(position.X, position.Y, position.Z);
            _hasPoseHistory = false;
        }
    }

    /// <summary>
    /// Rotates the rigidbody to a new rotation (teleport).
    /// </summary>
    public void MoveRotation(Quaternion rotation)
    {
        if (_body != null)
        {
            _body.Orientation = ToUnitJQuaternion(rotation);
            _hasPoseHistory = false;
        }
    }

    /// <summary>
    /// Converts a rotation for the simulation, normalising it on the way.
    /// </summary>
    /// <remarks>
    /// The kernel asserts that an assigned orientation is a unit quaternion, and only in Debug builds —
    /// so a rotation that drifted off unit length by composing many small rotations throws in the editor
    /// and silently skews inertia in a shipped player. Normalising here removes both outcomes. A
    /// degenerate rotation becomes identity rather than NaN.
    /// </remarks>
    private static JQuaternion ToUnitJQuaternion(Quaternion rotation)
    {
        Quaternion unit = Quaternion.NormalizeSafe(rotation);
        return new JQuaternion(unit.X, unit.Y, unit.Z, unit.W);
    }
}
