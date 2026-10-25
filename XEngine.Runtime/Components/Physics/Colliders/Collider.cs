// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Jitter2.Collision.Shapes;
using Jitter2.Dynamics;
using Jitter2.LinearMath;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

[ComponentIcon("\uf1b2")] // Cube subclasses override with their specific shape
public abstract class Collider : MonoBehaviour
{
    /// <summary>
    /// Smallest dimension a collider is allowed to have.
    /// </summary>
    /// <remarks>
    /// The kernel rejects zero and negative extents outright, so something has to stand between a value
    /// typed into the inspector and a thrown exception. Clamping is that something, and it happens on the
    /// serialized field rather than only where the shape is built, so the number shown, the gizmo drawn
    /// and the shape simulated are the same size.
    /// </remarks>
    protected const float MinExtent = 0.01f;

    /// <summary>Raises a dimension to <see cref="MinExtent"/> if it is smaller.</summary>
    protected static float ClampExtent(float value) => value < MinExtent ? MinExtent : value;

    /// <summary>Raises each component of a dimension to <see cref="MinExtent"/>.</summary>
    protected static Float3 ClampExtent(Float3 value)
        => new(ClampExtent(value.X), ClampExtent(value.Y), ClampExtent(value.Z));

    /// <summary>Smallest magnitude a transform-scale axis may contribute to a collider shape.</summary>
    protected const float MinScale = 1e-4f;

    /// <summary>
    /// Clamps the magnitude of one transform-scale axis rather than its value, so a mirrored (negative)
    /// axis stays mirrored instead of flipping to a tiny positive one, and a zero axis degrades to the
    /// minimum instead of producing a degenerate shape (upstream 0e9dac4a — negative-scale colliders).
    /// </summary>
    protected static float ClampScale(float value)
        => value <= -MinScale || value >= MinScale ? value : value < 0.0f ? -MinScale : MinScale;

    /// <summary>Applies <see cref="ClampScale"/> to each component of the transform scale.</summary>
    protected static Float3 ClampScale(Float3 scale)
        => new(ClampScale(scale.X), ClampScale(scale.Y), ClampScale(scale.Z));

    public Float3 Center;
    public Float3 Rotation;

    /// <summary>
    /// Scene-view wire colour: cyan for triggers, green for solid colliders. Matches
    /// <see cref="Collider2D"/>.
    /// </summary>
    protected internal Color GizmoColor => IsTrigger ? Color.Cyan : Color.Green;

    /// <summary>
    /// When true the collider reports overlaps but generates no contacts, so nothing collides with
    /// it physically. Matches Unity's <c>Collider.isTrigger</c>.
    /// </summary>
    [SerializeField] private bool isTrigger;

    /// <summary>
    /// When true the collider reports overlaps but generates no contacts, so nothing collides with
    /// it physically. Matches Unity's <c>Collider.isTrigger</c>.
    /// </summary>
    public bool IsTrigger
    {
        get => isTrigger;
        set
        {
            if (isTrigger == value)
                return;
            isTrigger = value;
            SyncBindings();
        }
    }

    /// <summary>
    /// Friction and bounciness for this collider's surface. Matches Unity's
    /// <c>Collider.sharedMaterial</c>.
    /// </summary>
    [SerializeField] private AssetRef<PhysicsMaterial> sharedMaterial;

    /// <summary>
    /// Friction and bounciness for this collider's surface, or null to inherit.
    /// </summary>
    /// <remarks>
    /// With no material the collider falls back to the project's default material, and failing that to
    /// its <see cref="Rigidbody3D"/>'s own friction and restitution. That layering is what lets a body
    /// set one surface for all its colliders and have an individual collider override it — a metal
    /// crate with one rubber face.
    /// <para>
    /// Assigning while the collider is already touching something takes effect on the existing contacts
    /// rather than waiting for them to separate; see <c>World.RefreshMaterial</c>.
    /// </para>
    /// </remarks>
    public PhysicsMaterial SharedMaterial
    {
        get => sharedMaterial.Res;
        set
        {
            sharedMaterial = new AssetRef<PhysicsMaterial>(value);
            SyncMaterial();
        }
    }

    /// <summary>
    /// Relative approach speed above which a pair involving this collider gets a swept test, or 0 to use
    /// the world's <see cref="PhysicsWorld.SweepVelocityThreshold"/>.
    /// </summary>
    [SerializeField] private float sweepVelocityThreshold;

    /// <summary>
    /// Relative approach speed above which a pair involving this collider gets a swept test, or 0 to use
    /// the world's <see cref="PhysicsWorld.SweepVelocityThreshold"/>.
    /// </summary>
    /// <remarks>
    /// Only consulted when the rigidbody's <see cref="Rigidbody3D.CollisionDetectionMode"/> asks for
    /// sweeping at all. Set this on the collider that is easy to tunnel through — a thin wall, a
    /// floorboard — rather than lowering the world value, which would make every other pair in the scene
    /// pay for sweeps it does not need. A pair uses the lower of the two colliders' thresholds.
    /// </remarks>
    public float SweepVelocityThreshold
    {
        get => sweepVelocityThreshold;
        set
        {
            sweepVelocityThreshold = value < 0f ? 0f : value;
            ApplyToShapes();
        }
    }

    /// <summary>Pushes the collider's own per-shape settings onto the shapes it has registered.</summary>
    private void ApplyToShapes()
    {
        if (_attachedShapes == null) return;

        foreach (RigidBodyShape shape in _attachedShapes)
            ApplySweepThreshold(shape);
    }

    private void ApplySweepThreshold(RigidBodyShape shape)
        => shape.SweepVelocityThreshold = sweepVelocityThreshold > 0f ? sweepVelocityThreshold : null;

    /// <summary>
    /// Reverse link stored on every shape this collider registers, so collision results can name the
    /// collider they came from. Shared by all of this collider's shapes.
    /// </summary>
    private ColliderBinding _binding;

    /// <summary>
    /// The Jitter2 rigidbody this collider is currently attached to.
    /// This could be either a Rigidbody3D's body or the PhysicsWorld's static rigidbody.
    /// </summary>
    private Jitter2.Dynamics.RigidBody _attachedBody;

    /// <summary>
    /// The Rigidbody3D component this collider is attached to, if any.
    /// Null if attached to the static rigidbody.
    /// </summary>
    private Rigidbody3D _attachedRigidbody3D;

    /// <summary>
    /// The shapes created by this collider and added to the rigidbody.
    /// </summary>
    private RigidBodyShape[] _attachedShapes;

    /// <summary>
    /// Shapes currently registered with the physics world. Empty while the collider is disabled.
    /// </summary>
    internal RigidBodyShape[] Shapes => _attachedShapes ?? [];

    /// <summary>
    /// Transform version tracking for static colliders.
    /// Used to detect when the transform has moved and shapes need updating.
    /// </summary>
    private uint _lastTransformVersion;

    /// <summary>
    /// Layer tracking for static colliders.
    /// Used to detect when the layer has changed and we need to move to a different static rigidbody.
    /// </summary>
    private int _lastLayer;

    protected Rigidbody3D RigidBody => GetComponentInParent<Rigidbody3D>();

    /// <summary>
    /// The <see cref="Rigidbody3D"/> this collider belongs to, or null when it is static geometry.
    /// Matches Unity's <c>Collider.attachedRigidbody</c>.
    /// </summary>
    public Rigidbody3D AttachedRigidbody => _attachedRigidbody3D;

    /// <summary>
    /// The world-space axis-aligned bounding box enclosing this collider, as the broad phase sees it.
    /// Matches Unity's <c>Collider.bounds</c>.
    /// </summary>
    /// <remarks>
    /// Empty (a degenerate box at the origin) while the collider is not registered with a physics
    /// world, since it has no shapes to bound until then.
    /// </remarks>
    public AABB Bounds
    {
        get
        {
            if (_attachedShapes == null || _attachedShapes.Length == 0)
                return new AABB(Float3.Zero, Float3.Zero);

            JBoundingBox box = _attachedShapes[0].WorldBoundingBox;
            for (int i = 1; i < _attachedShapes.Length; i++)
                JBoundingBox.CreateMerged(box, _attachedShapes[i].WorldBoundingBox, out box);

            return new AABB(
                new Float3(box.Min.X, box.Min.Y, box.Min.Z),
                new Float3(box.Max.X, box.Max.Y, box.Max.Z));
        }
    }

    /// <summary>
    /// Whether the dispatcher is currently counting this collider's body as one to scan for collision
    /// events. Tracked so attach/detach stays balanced.
    /// </summary>
    private bool _collisionScanRegistered;

    /// <summary>
    /// Returns true if this collider is already attached to a rigidbody.
    /// Used to prevent multiple rigidbodies from claiming the same collider.
    /// </summary>
    public bool IsClaimed => _attachedBody != null;

    /// <summary>
    /// Attempts to attach this collider to a Rigidbody3D.
    /// Returns false if the collider is already claimed by another rigidbody.
    /// </summary>
    internal bool TryAttachTo(Rigidbody3D rigidbody)
    {
        if (IsClaimed && _attachedRigidbody3D != rigidbody)
            return false; // Already claimed by a different rigidbody

        // Detach from current rigidbody if any
        Detach();

        // Attach to the new rigidbody
        _attachedRigidbody3D = rigidbody;
        _attachedBody = rigidbody._body;
        RegisterShapes();
        return true;
    }

    /// <summary>
    /// Attaches this collider to the static rigidbody in the physics world.
    /// Uses the GameObject's layer to determine which static rigidbody to attach to.
    /// </summary>
    private void AttachToStatic()
    {
        if (GameObject == null || GameObject.Scene == null || GameObject.Scene.Physics == null)
            return;

        _attachedRigidbody3D = null;
        // Get or create the static rigidbody for this GameObject's layer
        int layer = GameObject.LayerIndex;
        _attachedBody = GameObject.Scene.Physics.GetOrCreateStaticRigidBody(layer);
        RegisterShapes();
        _lastTransformVersion = ComputeWorldTransformVersion();
        _lastLayer = layer;
    }

    /// <summary>
    /// A version that changes whenever this transform OR any ancestor changes, so static colliders
    /// follow their parents. Transform.Version alone only tracks local edits, so moving a parent would
    /// not re-register a child collider's world-space shapes.
    /// </summary>
    private uint ComputeWorldTransformVersion()
    {
        uint v = 17;
        Transform t = Transform;
        while (t != null)
        {
            v = v * 31 + t.Version;
            t = t.Parent;
        }
        return v;
    }

    /// <summary>
    /// Detaches this collider from its current rigidbody.
    /// </summary>
    internal void Detach()
    {
        RegisterCollisionScan(PhysicsCallbacks.None);

        // Keep Owner so the next step's End/Exit callbacks can still name this collider.
        // ReportsPairEvents is cleared so a shape that somehow remains in the tree is not tracked.
        if (_attachedShapes != null)
        {
            foreach (RigidBodyShape shape in _attachedShapes)
                shape.ReportsPairEvents = false;
        }

        if (_attachedBody != null && _attachedShapes != null && !_attachedBody.Handle.IsZero)
        {
            // Only try to remove shapes if the body is still registered with the physics world
            // (If the rigidbody was already removed, the shapes are already gone)
            foreach (RigidBodyShape shape in _attachedShapes)
            {
                try
                {
                    // Use Preserve: Update mode calls SetMassInertia() after each removal, which
                    // iterates remaining shapes this throws NotSupportedException for TriangleShape.
                    // Mass/inertia is recalculated in full by RegisterShapes after re-attachment.
                    _attachedBody.RemoveShape(shape, Jitter2.Dynamics.MassInertiaUpdateMode.Preserve);
                }
                catch (ArgumentException)
                {
                    // Shape was already removed from this body (e.g., UpdateShapes pre-cleared the
                    // body with RemoveShapes before calling Detach). Safe to ignore.
                }
                catch (InvalidOperationException)
                {
                    // Body was removed from the physics world; its shapes are already gone.
                }
            }
        }

        _attachedBody = null;
        _attachedRigidbody3D = null;
        _attachedShapes = null;
    }

    /// <summary>
    /// Registers this collider's shapes with its attached rigidbody.
    /// </summary>
    private void RegisterShapes()
    {
        if (_attachedBody == null || _attachedBody.Handle.IsZero)
            return;

        // Create shapes based on whether we're attached to a Rigidbody3D or static
        if (_attachedRigidbody3D != null)
        {
            // Use transformed shapes for Rigidbody3D (existing behavior)
            _attachedShapes = CreateTransformedShapes();
        }
        else
        {
            // For static rigidbody, we need world-space transformed shapes
            _attachedShapes = CreateWorldTransformedShapes();
        }

        if (_attachedShapes != null)
        {
            _binding ??= new ColliderBinding(this);
            RefreshBinding();

            PhysicsCallbacks listeners = FindListeners();
            RegisterCollisionScan(listeners);

            foreach (RigidBodyShape shape in _attachedShapes)
            {
                shape.Owner = _binding;
                shape.IsSensor = isTrigger;
                shape.ReportsPairEvents = (listeners & PhysicsCallbacks.AnyTrigger) != 0;
                ApplyMaterial(shape);
                ApplySweepThreshold(shape);

                // Always use Preserve: per-shape Update mode calls SetMassInertia() after every
                // addition, which iterates ALL attached shapes. Any TriangleShape in that set
                // throws NotSupportedException. We set mass once below, after all shapes are added.
                _attachedBody.AddShape(shape, Jitter2.Dynamics.MassInertiaUpdateMode.Preserve);
            }
        }

        if (_attachedRigidbody3D != null)
        {
            // SetMassInertia(mass) sums inertia from all shapes, then scales to the requested mass.
            // TriangleShape has no volume so it throws NotSupportedException; fall back to treating the
            // body as a solid box sized to the shapes' combined bounds so it still rotates plausibly.
            try
            {
                _attachedBody.SetMassInertia(_attachedRigidbody3D.Mass);
            }
            catch (NotSupportedException)
            {
                JMatrix inertia = ApproximateBoxInertia(_attachedShapes, _attachedRigidbody3D.Mass);
                _attachedBody.SetMassInertia(inertia, _attachedRigidbody3D.Mass);
            }
        }
        // Static bodies don't need mass or inertia.
    }

    /// <summary>
    /// Copies the collider's current layer, trigger state and rigidbody into the shared binding.
    /// </summary>
    private void RefreshBinding()
    {
        if (_binding == null)
            return;

        _binding.Rigidbody = _attachedRigidbody3D;
        _binding.Layer = GameObject != null ? GameObject.LayerIndex : 0;
        _binding.IsTrigger = isTrigger;
    }

    /// <summary>
    /// Pushes changes made through this collider's properties out to the binding and the registered
    /// shapes, without rebuilding them.
    /// </summary>
    internal void SyncBindings()
    {
        RefreshBinding();

        if (_attachedShapes == null)
            return;

        PhysicsCallbacks listeners = FindListeners();
        RegisterCollisionScan(listeners);

        foreach (RigidBodyShape shape in _attachedShapes)
        {
            shape.IsSensor = isTrigger;
            shape.ReportsPairEvents = (listeners & PhysicsCallbacks.AnyTrigger) != 0;
        }
    }

    /// <summary>
    /// Pushes this collider's material onto its registered shapes and re-resolves any contacts they are
    /// already part of.
    /// </summary>
    internal void SyncMaterial()
    {
        if (_attachedShapes == null)
            return;

        Jitter2.World world = _attachedBody?.World;

        foreach (RigidBodyShape shape in _attachedShapes)
        {
            ApplyMaterial(shape);
            world?.RefreshMaterial(shape);
        }
    }

    /// <summary>
    /// Copies the effective material onto one shape.
    /// </summary>
    /// <remarks>
    /// With no material anywhere, the shape's coefficients are left unset so the kernel falls back to the
    /// body's own friction and restitution — which is both the pre-material behaviour and what
    /// <see cref="Rigidbody3D"/> exposes directly.
    /// </remarks>
    private void ApplyMaterial(RigidBodyShape shape)
    {
        PhysicsMaterial material;
        if (sharedMaterial.Res.IsValid())
            material = sharedMaterial.Res;
        else if (GameObject != null && GameObject.Scene != null && GameObject.Scene.Physics != null)
            material = GameObject.Scene.Physics.DefaultMaterial;
        else
            material = null;

        if (material == null)
        {
            shape.Friction = null;
            shape.Restitution = null;
            shape.FrictionCombine = null;
            shape.RestitutionCombine = null;
            return;
        }

        shape.Friction = material.Friction;
        shape.Restitution = material.Bounciness;
        shape.FrictionCombine = (MaterialCombine)material.FrictionCombine;
        shape.RestitutionCombine = (MaterialCombine)material.BounceCombine;
    }

    /// <summary>
    /// Which physics callbacks are implemented by components that would receive this collider's events:
    /// those on its own GameObject, plus those on its rigidbody's GameObject when that differs.
    /// </summary>
    /// <remarks>
    /// Drives whether the kernel tracks overlaps for these shapes at all, so a trigger nobody listens
    /// to costs no bookkeeping.
    /// </remarks>
    private PhysicsCallbacks FindListeners()
    {
        PhysicsCallbacks listeners = CollectListeners(GameObject);

        GameObject rigidbodyOwner = _attachedRigidbody3D != null ? _attachedRigidbody3D.GameObject : null;
        if (rigidbodyOwner != null && rigidbodyOwner != GameObject)
            listeners |= CollectListeners(rigidbodyOwner);

        return listeners;
    }

    private static PhysicsCallbacks CollectListeners(GameObject target)
    {
        if (target == null)
            return PhysicsCallbacks.None;

        var listeners = PhysicsCallbacks.None;
        System.Collections.Generic.List<MonoBehaviour> components = target._components;
        for (int i = 0; i < components.Count; i++)
        {
            MonoBehaviour component = components[i];
            if (component == null || component.IsDisposed) continue;
            listeners |= SceneComponentRegistry.GetPhysicsCallbacks(component);
        }

        return listeners;
    }

    /// <summary>
    /// Adds or removes this collider's body from the dispatcher's collision scan, so only bodies with
    /// an actual <c>OnCollision*</c> listener are walked each step.
    /// </summary>
    private void RegisterCollisionScan(PhysicsCallbacks listeners)
    {
        ContactEventDispatcher dispatcher = (GameObject != null && GameObject.Scene != null && GameObject.Scene.Physics != null ? GameObject.Scene.Physics.ContactEvents : null);
        bool wanted = dispatcher != null && _attachedBody != null
                      && (listeners & PhysicsCallbacks.AnyCollision) != 0;

        if (wanted == _collisionScanRegistered)
            return;

        if (wanted) dispatcher.AddBody(_attachedBody);
        else dispatcher?.RemoveBody(_attachedBody);

        _collisionScanRegistered = wanted;
    }

    /// <summary>
    /// Builds an inertia tensor for a body whose shapes have no usable volume (e.g. concave
    /// TriangleShapes). The shapes are approximated as a single solid box sized to their combined
    /// local-space bounds, using the same formula as <c>BoxShape</c>. The tensor is taken about the box
    /// centre rather than the body's centre of mass, which is a close enough approximation for the
    /// fallback case (the alternative is a meaningless identity tensor).
    /// </summary>
    private static JMatrix ApproximateBoxInertia(RigidBodyShape[] shapes, float mass)
    {
        if (shapes == null || shapes.Length == 0)
            return JMatrix.Identity;

        JVector min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        JVector max = new(float.MinValue, float.MinValue, float.MinValue);
        foreach (RigidBodyShape shape in shapes)
        {
            shape.CalculateBoundingBox(JQuaternion.Identity, JVector.Zero, out JBoundingBox box);
            min = JVector.Min(min, box.Min);
            max = JVector.Max(max, box.Max);
        }

        // Clamp to a small positive size so the tensor stays positive-definite (invertible) even for
        // perfectly flat or degenerate meshes.
        float sx = Maths.Max(max.X - min.X, 1e-3f);
        float sy = Maths.Max(max.Y - min.Y, 1e-3f);
        float sz = Maths.Max(max.Z - min.Z, 1e-3f);

        JMatrix inertia = JMatrix.Identity;
        inertia.M11 = (1.0f / 12.0f) * mass * (sy * sy + sz * sz);
        inertia.M22 = (1.0f / 12.0f) * mass * (sx * sx + sz * sz);
        inertia.M33 = (1.0f / 12.0f) * mass * (sx * sx + sy * sy);
        return inertia;
    }

    /// <summary>
    /// Create the Jitter Physics RigidBodyShape
    /// </summary>
    public abstract RigidBodyShape[] CreateShapes();

    /// <summary>
    /// Create the Transformed Jitter Physics RigidBodyShape
    /// </summary>
    public RigidBodyShape[] CreateTransformedShapes()
    {
        // Create the base shape
        RigidBodyShape[] shapes = CreateShapes();
        if (shapes == null)
            return null;
        Rigidbody3D rb = RigidBody;
        if (rb.IsNotValid()) return shapes;

        // Get the cumulative scale from this object up to (but not including) the rigidbody
        Float3 cumulativeScale = Float3.One;
        Transform current = Transform;
        Transform rbTransform = rb.Transform;

        while (current != null)
        {
            cumulativeScale *= current.LocalScale;
            current = current.Parent;
        }

        cumulativeScale = Maths.Max(cumulativeScale, Float3.One * 0.05f);

        // Get the local rotation and position in world space
        Quaternion localRotation = Quaternion.FromEuler(Rotation);
        Float3 scaledCenter = Center * cumulativeScale;

        // Transform local position and rotation to world space
        Float3 worldCenter = Transform.TransformPoint(scaledCenter);
        Quaternion worldRotation = Transform.Rotation * localRotation;

        // Transform from world space to rigid body's local space
        Float3 rbLocalCenter = rb.Transform.InverseTransformPoint(worldCenter);
        Quaternion rbLocalRotation = Quaternion.Inverse(rb.Transform.Rotation) * worldRotation;

        // Create a scale transform matrix that includes both rotation and scale
        Float4x4 scaleMatrix = Float4x4.CreateTRS(Float3.Zero, rbLocalRotation, cumulativeScale);

        // If there's no transformation needed, return the original shape
        if (rbLocalCenter.Equals(Float3.Zero) &&
            cumulativeScale.Equals(Float3.One) &&
            rbLocalRotation == Quaternion.Identity)
            return shapes;

        // Convert to Jitter types
        var translation = new Jitter2.LinearMath.JVector(
            rbLocalCenter.X,
            rbLocalCenter.Y,
            rbLocalCenter.Z
        );

        // Convert combined rotation and scale matrix to JMatrix
        var orientation = new Jitter2.LinearMath.JMatrix(
            scaleMatrix[0, 0], scaleMatrix[0, 1], scaleMatrix[0, 2],
            scaleMatrix[1, 0], scaleMatrix[1, 1], scaleMatrix[1, 2],
            scaleMatrix[2, 0], scaleMatrix[2, 1], scaleMatrix[2, 2]
        );

        //return new TransformedShape(shape, translation, orientation);
        TransformedShape[] transformedShapes = new TransformedShape[shapes.Length];
        for (int i = 0; i < shapes.Length; i++)
            transformedShapes[i] = new TransformedShape(shapes[i], translation, orientation);

        return transformedShapes;
    }

    /// <summary>
    /// Create shapes transformed into world space for the static rigidbody.
    /// </summary>
    private RigidBodyShape[] CreateWorldTransformedShapes()
    {
        // Create the base shape
        RigidBodyShape[] shapes = CreateShapes();
        if (shapes == null)
            return null;

        // Get the cumulative scale
        Float3 cumulativeScale = Float3.One;
        Transform current = Transform;

        while (current != null)
        {
            cumulativeScale *= current.LocalScale;
            current = current.Parent;
        }

        cumulativeScale = Maths.Max(cumulativeScale, Float3.One * 0.05f);

        // Get the local rotation and position
        Quaternion localRotation = Quaternion.FromEuler(Rotation);
        Float3 scaledCenter = Center * cumulativeScale;

        // Transform to world space
        Float3 worldCenter = Transform.TransformPoint(scaledCenter);
        Quaternion worldRotation = Transform.Rotation * localRotation;

        // Create a scale transform matrix that includes both rotation and scale
        Float4x4 scaleMatrix = Float4x4.CreateTRS(Float3.Zero, worldRotation, cumulativeScale);

        // Convert to Jitter types
        var translation = new Jitter2.LinearMath.JVector(
            worldCenter.X,
            worldCenter.Y,
            worldCenter.Z
        );

        // Convert combined rotation and scale matrix to JMatrix
        var orientation = new Jitter2.LinearMath.JMatrix(
            scaleMatrix[0, 0], scaleMatrix[0, 1], scaleMatrix[0, 2],
            scaleMatrix[1, 0], scaleMatrix[1, 1], scaleMatrix[1, 2],
            scaleMatrix[2, 0], scaleMatrix[2, 1], scaleMatrix[2, 2]
        );

        // If there's no transformation needed, return the original shape
        if (worldCenter.Equals(Float3.Zero) &&
            cumulativeScale.Equals(Float3.One) &&
            worldRotation == Quaternion.Identity)
            return shapes;

        TransformedShape[] transformedShapes = new TransformedShape[shapes.Length];
        for (int i = 0; i < shapes.Length; i++)
            transformedShapes[i] = new TransformedShape(shapes[i], translation, orientation);

        return transformedShapes;
    }

    public override void OnEnable()
    {
        // First check if there's a Rigidbody3D on this GameObject or any parent
        Rigidbody3D rb = GetComponentInParent<Rigidbody3D>();

        if (rb.IsValid())
        {
            // Attach to the Rigidbody3D
            TryAttachTo(rb);
        }
        else
        {
            // No Rigidbody3D found, attach to the static rigidbody
            AttachToStatic();
        }
    }

    public override void OnDisable()
    {
        // Detach from whatever rigidbody we're attached to
        Detach();
    }

    public override void Update()
    {
        // Only track transform and layer changes if we're attached to the static rigidbody
        if (_attachedRigidbody3D == null && _attachedBody != null)
        {
            bool transformChanged = ComputeWorldTransformVersion() != _lastTransformVersion;
            bool layerChanged = GameObject.LayerIndex != _lastLayer;

            // Check if the transform or layer has changed
            if (transformChanged || layerChanged)
            {
                // Transform has moved or layer changed, update the shapes
                Detach();
                AttachToStatic();
            }
        }
    }

    public override void OnValidate()
    {
        // If we're attached to a Rigidbody3D, refresh it
        if (_attachedRigidbody3D != null)
        {
            _attachedRigidbody3D.OnValidate();
        }
        else if (_attachedBody != null)
        {
            // We're attached to the static rigidbody, just re-register
            Detach();
            AttachToStatic();
        }
    }
}
