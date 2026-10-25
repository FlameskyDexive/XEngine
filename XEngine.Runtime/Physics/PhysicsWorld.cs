// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Jitter2;
using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.Dynamics;
using Jitter2.LinearMath;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Persistent keeps them alive between steps for lower latency at idle CPU cost.
/// </summary>
public enum PhysicsThreadModel
{
    Regular,
    Persistent
}

public partial class PhysicsWorld
{
    /// <summary>Resolves the physics world a rigidbody lives in, or null when detached.</summary>
    private static PhysicsWorld? WorldOf(Rigidbody3D? body)
    {
        if (body == null || body.IsNotValid()) return null;
        var go = body.GameObject;
        if (go == null || go.IsNotValid()) return null;
        var scene = go.Scene;
        if (scene == null || scene.IsNotValid()) return null;
        return scene.Physics;
    }

    /// <summary>
    /// Marks a pair of bodies as non-colliding. The ignore pair is scoped to the bodies' physics
    /// world (upstream 8e8ff9c59) — it no longer leaks across scenes.
    /// </summary>
    public static void IgnoreCollisionBetween(Rigidbody3D bodyA, Rigidbody3D bodyB)
    {
        var world = WorldOf(bodyA) ?? WorldOf(bodyB);
        world?.IgnoreCollisionPair(bodyA, bodyB);
    }

    /// <summary>Re-enables collision for a pair previously ignored via <see cref="IgnoreCollisionBetween"/>.</summary>
    public static void EnableCollisionBetween(Rigidbody3D bodyA, Rigidbody3D bodyB)
    {
        var world = WorldOf(bodyA) ?? WorldOf(bodyB);
        world?.EnableCollisionPair(bodyA, bodyB);
    }

    internal void IgnoreCollisionPair(Rigidbody3D bodyA, Rigidbody3D bodyB) => _layerFilter.IgnoreCollisionBetween(bodyA, bodyB);

    internal void EnableCollisionPair(Rigidbody3D bodyA, Rigidbody3D bodyB) => _layerFilter.EnableCollisionBetween(bodyA, bodyB);

    internal void AddConstraintCollision(Rigidbody3D bodyA, Rigidbody3D bodyB) => _layerFilter.AddConstraintCollision(bodyA, bodyB);

    internal void RemoveConstraintCollision(Rigidbody3D bodyA, Rigidbody3D bodyB) => _layerFilter.RemoveConstraintCollision(bodyA, bodyB);

    /// <summary>
    /// Bake (or fetch the cached) physics representation of a mesh. The bake is stored directly on
    /// the <see cref="Mesh"/> instance (<see cref="Mesh.BakedPhysics"/>), so its lifetime is tied to
    /// the mesh rather than a separate global cache. The result is shared by every collider that uses
    /// the mesh, so the bake happens once instead of per MeshCollider instance, and is rebuilt
    /// automatically when the mesh's <see cref="Mesh.Version"/> changes.
    /// <para/>
    /// Thread-safe. The bake is pure CPU work, so this may be called from worker threads to pre-bake
    /// meshes off the main thread (independent meshes bake in parallel; each mesh locks on itself).
    /// </summary>
    public static BakedPhysicsMesh BakeMesh(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        lock (mesh)
        {
            var cached = mesh.BakedPhysics;
            if (cached != null && cached.Version == mesh.Version)
                return cached;
        }

        // Bake outside the lock so independent meshes bake concurrently. A rare double-bake of the
        // same mesh is harmless (the bake is idempotent; last write wins).
        BakedPhysicsMesh baked = BakedPhysicsMesh.Build(mesh);

        lock (mesh)
        {
            var cached = mesh.BakedPhysics;
            if (cached != null && cached.Version == mesh.Version)
                return cached; // another thread baked the current version meanwhile
            mesh.BakedPhysics = baked;
            return baked;
        }
    }

    public World World { get; private set; }

    /// <summary>
    /// Turns the step's overlap and contact state into <c>OnTrigger*</c> / <c>OnCollision*</c>
    /// callbacks.
    /// </summary>
    internal ContactEventDispatcher ContactEvents { get; } = new();

    /// <summary>
    /// Static rigidbodies indexed by layer. Each layer has its own static rigidbody to ensure collision filtering works correctly.
    /// Orphan colliders (colliders without a Rigidbody3D component) will attach to the static rigidbody for their layer.
    /// </summary>
    private Dictionary<int, Jitter2.Dynamics.RigidBody> _staticRigidbodiesByLayer = new();

    /// <summary>
    /// Composite filter that chains multiple broad phase filters together.
    /// </summary>
    private CompositeBroadPhaseFilter _compositeBroadPhaseFilter;
    private LayerFilter _layerFilter;

    // Registered terrain data for shape casts
    internal struct TerrainInfo
    {
        public ITerrainHeightProvider HeightProvider;
        public JVector Origin;
        public float CellSize;
    }
    internal readonly Dictionary<TerrainHeightmapProxy, TerrainInfo> _terrainProxies = [];

    private readonly List<IDynamicTreeProxy> _queryProxies = [];
    private readonly List<RaycastHit> _scratchRayHits = [];
    private readonly List<ShapeCastHit> _scratchCastHits = [];

    // Scratch state for queries, shared rather than allocated per call: a character controller issues
    // about ten casts a frame, each of which would otherwise allocate lists and reuse cast shapes.
    //
    // INVARIANT: a query must never run inside another query. Nothing on a query path calls into user
    // code, and collision and trigger callbacks are raised between queries rather than during one. If
    // that ever changes, these buffers and the cached shapes are shared mutable state and both queries
    // would return nonsense; _queryDepth is what makes that say so instead of corrupting quietly.
    private int _queryDepth;

    /// <summary>
    /// Marks a query as running, reporting the two ways the shared scratch state can be violated: a
    /// query off the engine thread, and a second query overlapping this one. Both are tripwires rather
    /// than recoveries, since the buffers are already committed by the time we could tell. Always pair
    /// with <see cref="ExitQuery"/> (via try/finally), including on the early-return paths.
    /// </summary>
    private void EnterQuery(string query)
    {
        // Jitter's own traversal is thread-safe; this layer is not, so the rule is ours.
        Debug.EnsureMainThread(query);

        // Interlocked so the counter stays balanced even when the rule above is already being broken;
        // a torn count would strand this permanently above zero and report every later query.
        if (System.Threading.Interlocked.Increment(ref _queryDepth) > 1)
        {
            Debug.LogErrorOnce("Physics.OverlappingQuery",
                $"[Physics] {query} ran while another physics query was still in progress, either nested or from another thread. Queries share scratch buffers and cast shapes, so both results are unreliable.");
        }
    }

    private void ExitQuery() => System.Threading.Interlocked.Decrement(ref _queryDepth);

    public Float3 Gravity = new(0, -9.81f, 0);
    public int SolverIterations = 8;
    public int RelaxIterations = 4;
    public int Substep = 2;
    public bool AllowSleep = true;
    public bool UseMultithreading = true;
    /// <summary>
    /// Whether Transform edits are pushed into the physics system immediately (before physics queries),
    /// or only batched right before the FixedUpdate step. Matches Unity's Physics.autoSyncTransforms:
    /// when true, a query right after moving a Transform sees the new pose; when false, call
    /// <see cref="SyncTransforms"/> manually (the pre-step sync still happens every FixedUpdate either way).
    /// This only concerns the transform -> body direction; the body -> transform readback after a step
    /// is separate and always runs for dynamic/kinematic bodies.
    /// </summary>
    public bool AutoSyncTransforms = true;

    // Rigidbodies that may need their Transform pushed into the physics world (transform -> body).
    private readonly HashSet<Rigidbody3D> _syncBodies = [];

    internal void RegisterBody(Rigidbody3D body) => _syncBodies.Add(body);
    internal void UnregisterBody(Rigidbody3D body) => _syncBodies.Remove(body);

    /// <summary>
    /// Pushes any changed Transforms into their physics bodies (transform -> body). Runs automatically
    /// before each simulation step and, when <see cref="AutoSyncTransforms"/> is on, before queries.
    /// Call it manually to make Transform edits visible to queries when auto-sync is off. Only bodies
    /// whose Transform actually changed since the last sync are updated, so it is cheap when idle.
    /// </summary>
    public void SyncTransforms()
    {
        foreach (var body in _syncBodies)
            body?.SyncTransformToBody();
    }

    /// <summary>
    /// When true, uses Jitter2's deterministic island-based solver instead of the regular parallel solver.
    /// Slower but produces identical results across runs required for networked physics, replays, or lockstep simulation.
    /// </summary>
    public bool EnhancedDeterminism = false;

    /// <summary>
    /// Thread model for the physics step. Persistent keeps worker threads alive between steps
    /// (lower latency, higher idle CPU). Regular spins them up per step (higher latency, lower idle CPU).
    /// </summary>
    public PhysicsThreadModel ThreadModel = PhysicsThreadModel.Regular;

    /// <summary>
    /// Generates extra contact points along the perimeter of contact patches for improved stability
    /// on flat surfaces. Default true. Disable for slight perf gain at the cost of stability.
    /// </summary>
    public bool EnableAuxiliaryContactPoints = true;

    /// <summary>
    /// Persists contact manifolds across frames (warm-starts the solver). Default true. Disable for
    /// slight memory savings; significantly hurts stack stability if turned off.
    /// </summary>
    public bool PersistentContactManifold = true;

    /// <summary>
    /// Damping factor (0..1) applied to speculative contact correction. Lower = softer prediction.
    /// Default 0.9.
    /// </summary>
    public float SpeculativeRelaxationFactor = 0.9f;

    /// <summary>
    /// Relative approach speed above which a continuous pair gets a swept test. Default 10.
    /// </summary>
    /// <remarks>
    /// The speed that risks tunnelling is roughly the thinner shape's thickness divided by the fixed
    /// timestep, so this world-wide value has to suit the thinnest geometry in the scene. Rather than
    /// lowering it and making every pair pay, set
    /// <see cref="Collider.SweepVelocityThreshold"/> on the geometry that needs it.
    /// </remarks>
    public float SweepVelocityThreshold = 10f;

    /// <summary>
    /// Approach speed a contact must exceed before bounciness is applied at all. Matches Unity's
    /// <c>Physics.bounceThreshold</c>. Default 1.
    /// </summary>
    /// <remarks>
    /// A resting contact still reports a small approach velocity every step; bouncing on it would mean a
    /// bouncy body never settles. Raise this to suppress small bounces, lower it to let light taps bounce.
    /// Applies to contact pairs formed after the change, not to ones already being solved.
    /// </remarks>
    public float BounceThreshold = 1f;

    /// <summary>
    /// The material used by colliders with no <see cref="Collider.SharedMaterial"/> of their own.
    /// Matches Unity's <c>Physics.defaultPhysicsMaterial</c>.
    /// </summary>
    /// <remarks>
    /// Null by default, in which case a collider without a material falls back to the friction and
    /// restitution of its own <see cref="Rigidbody3D"/> — the behaviour before materials existed.
    /// </remarks>
    public PhysicsMaterial DefaultMaterial;

    /// <summary>
    /// Whether queries report trigger colliders. Matches Unity's <c>Physics.queriesHitTriggers</c>.
    /// Default true.
    /// </summary>
    /// <remarks>
    /// Applies to raycasts, shape casts and overlap tests unless a call passes
    /// <see cref="QueryTriggerInteraction"/> other than <see cref="QueryTriggerInteraction.UseGlobal"/>.
    /// </remarks>
    public bool QueriesHitTriggers = true;

    /// <summary>
    /// Event triggered before each physics step.
    /// </summary>
    public event Action<float> PreStep;

    /// <summary>
    /// Event triggered after each physics step.
    /// </summary>
    public event Action<float> PostStep;

    /// <summary>
    /// Event triggered before each physics substep, with the substep duration (FixedDeltaTime / Substep).
    /// Use this for sub-stepped force/impulse models (e.g. vehicle tyres) that need the body's
    /// re-integrated velocity each substep.
    /// </summary>
    public event Action<float> PreSubStep;

    public PhysicsWorld()
    {
        World = new World();

        World.DynamicTree.Filter = World.DefaultDynamicTreeFilter;

        // Set up composite broad phase filter
        _compositeBroadPhaseFilter = new CompositeBroadPhaseFilter();
        _layerFilter = new LayerFilter();
        _compositeBroadPhaseFilter.AddFilter(_layerFilter);
        World.BroadPhaseFilter = _compositeBroadPhaseFilter;

        World.NarrowPhaseFilter = new TriangleEdgeCollisionFilter();

        // Hook up physics step events
        World.PreStep += OnPreStep;
        World.PostStep += OnPostStep;
        World.PreSubStep += OnPreSubStep;
    }

    /// <summary>
    /// Gets or creates a static rigidbody for the specified layer.
    /// Each layer has its own static rigidbody to ensure collision filtering works correctly.
    /// </summary>
    public Jitter2.Dynamics.RigidBody GetOrCreateStaticRigidBody(int layer)
    {
        if (_staticRigidbodiesByLayer.TryGetValue(layer, out var staticBody))
        {
            return staticBody;
        }

        // Create a new static rigidbody for this layer
        staticBody = World.CreateRigidBody();
        staticBody.MotionType = MotionType.Static;
        staticBody.Tag = new Rigidbody3D.RigidBodyUserData()
        {
            Rigidbody = null, // No Rigidbody3D component associated with this
            InstanceID = layer, // This is just used to sort for collision filtering, it just needs to be a consistent value
            Layer = layer
        };

        _staticRigidbodiesByLayer[layer] = staticBody;
        return staticBody;
    }

    private void OnPreStep(float deltaTime) => InvokeStepEvent(PreStep, deltaTime, nameof(PreStep));
    private void OnPreSubStep(float deltaTime) => InvokeStepEvent(PreSubStep, deltaTime, nameof(PreSubStep));
    private void OnPostStep(float deltaTime) => InvokeStepEvent(PostStep, deltaTime, nameof(PostStep));

    // Step callbacks fire inside Jitter's World.Step. A throwing subscriber (e.g. a wheel raycasting
    // against a disposed body) must not unwind through the solver and crash. Isolate each subscriber
    // so one failure neither aborts the step nor blocks the other subscribers.
    private static void InvokeStepEvent(Action<float> evt, float deltaTime, string stage)
    {
        if (evt == null) return;
        foreach (Delegate d in evt.GetInvocationList())
        {
            try { ((Action<float>)d)(deltaTime); }
            catch (Exception ex)
            {
                Debug.LogError($"[Physics] {stage} subscriber {d.Method.DeclaringType?.Name}.{d.Method.Name} threw and was skipped: {ex.Message}");
            }
        }
    }

    public void Clear()
    {
        World?.Clear();
        ContactEvents.Clear();

        // Clear the static rigidbodies dictionary - they will be recreated as needed
        _staticRigidbodiesByLayer.Clear();
    }

    public void Update()
    {
        // Configure world settings
        World.AllowDeactivation = AllowSleep;

        World.SubstepCount = Substep;
        World.SolverIterations = (SolverIterations, RelaxIterations);

        World.Gravity = new JVector(Gravity.X, Gravity.Y, Gravity.Z);

        World.SolveMode = EnhancedDeterminism ? SolveMode.Deterministic : SolveMode.Regular;
        World.ThreadModel = ThreadModel == PhysicsThreadModel.Persistent
            ? World.ThreadModelType.Persistent
            : World.ThreadModelType.Regular;

        World.EnableAuxiliaryContactPoints = EnableAuxiliaryContactPoints;
        World.PersistentContactManifold = PersistentContactManifold;
        World.SpeculativeRelaxationFactor = SpeculativeRelaxationFactor;
        World.SpeculativeVelocityThreshold = SweepVelocityThreshold;
        World.RestitutionThreshold = BounceThreshold;

        // Push any user Transform edits into the bodies before stepping (always - this is the
        // "sync prior to the physics step" that happens regardless of AutoSyncTransforms).
        SyncTransforms();

        World.Step(Time.FixedDeltaTime, UseMultithreading);

        // Record the new pose against the previous one, so interpolating bodies have a segment to
        // present between. Has to happen here rather than in a component FixedUpdate: several steps can
        // run before a frame is drawn, and only the last pair of poses is the one to interpolate over.
        foreach (Rigidbody3D body in _syncBodies)
            body?.CapturePose();

        // After the step, so contact impulses are the solved ones and gameplay code that reacts to a
        // callback sees the post-step state.
        ContactEvents.Dispatch(World);
    }

    /// <summary>
    /// Casts a ray against all colliders in this physics world.
    /// </summary>
    public bool Raycast(Float3 origin, Float3 direction)
        => RaycastInternal(origin, direction, float.MaxValue, LayerMask.Everything,
            QueryTriggerInteraction.UseGlobal, allowNonShape: true, out _);

    /// <summary>
    /// Casts a ray against all colliders and returns detailed information about the hit.
    /// </summary>
    public bool Raycast(Float3 origin, Float3 direction, out RaycastHit hitInfo)
        => RaycastInternal(origin, direction, float.MaxValue, LayerMask.Everything,
            QueryTriggerInteraction.UseGlobal, allowNonShape: true, out hitInfo);

    /// <summary>
    /// Casts a ray within a maximum distance.
    /// </summary>
    public bool Raycast(Float3 origin, Float3 direction, float maxDistance)
        => RaycastInternal(origin, direction, maxDistance, LayerMask.Everything,
            QueryTriggerInteraction.UseGlobal, allowNonShape: true, out _);

    /// <summary>
    /// Casts a ray within a maximum distance and returns detailed information.
    /// </summary>
    public bool Raycast(Float3 origin, Float3 direction, float maxDistance, out RaycastHit hitInfo)
        => RaycastInternal(origin, direction, maxDistance, LayerMask.Everything,
            QueryTriggerInteraction.UseGlobal, allowNonShape: true, out hitInfo);

    /// <summary>
    /// Casts a ray with layer mask filtering.
    /// </summary>
    public bool Raycast(Float3 origin, Float3 direction, float maxDistance, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
        => RaycastInternal(origin, direction, maxDistance, layerMask, queryTriggerInteraction,
            allowNonShape: false, out _);

    /// <summary>
    /// Casts a ray with layer mask filtering and returns detailed information.
    /// </summary>
    public bool Raycast(Float3 origin, Float3 direction, out RaycastHit hitInfo, float maxDistance, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
        => RaycastInternal(origin, direction, maxDistance, layerMask, queryTriggerInteraction,
            allowNonShape: false, out hitInfo);

    #region Shape Casting

    /// <summary>
    /// Generic shape cast that returns all hits along the sweep path.
    /// </summary>
    /// <param name="shape">The shape to cast.</param>
    /// <param name="orientation">The orientation of the casting shape.</param>
    /// <param name="origin">Starting position of the shape.</param>
    /// <param name="direction">Direction to cast the shape.</param>
    /// <param name="maxDistance">Maximum distance to cast.</param>
    /// <param name="hits">List to populate with all hits found.</param>
    /// <param name="layerMask">Layer mask for filtering.</param>
    /// <returns>Number of hits found.</returns>
    public int ShapeCastAll(RigidBodyShape shape, Quaternion orientation, Float3 origin, Float3 direction, float maxDistance, List<ShapeCastHit> hits, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
    {
        ArgumentNullException.ThrowIfNull(hits);
        hits.Clear();
        if (maxDistance < 0f) return 0;
        if (!ValidateQuery(origin, direction, maxDistance, nameof(ShapeCastAll))) return 0;
        if (AutoSyncTransforms) SyncTransforms();

        EnterQuery(nameof(ShapeCastAll));
        try
        {
            direction = Float3.Normalize(direction);
            var jOrigin = new JVector(origin.X, origin.Y, origin.Z);
            var jSweep = new JVector(direction.X, direction.Y, direction.Z) * maxDistance;
            var jOrientation = new JQuaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);

            var handler = new CollectSweepListHandler
            {
                LayerMask = layerMask,
                HitTriggers = HitsTriggers(queryTriggerInteraction),
                QueryShape = shape,
                SweepLength = maxDistance,
                Hits = hits,
            };
            float maxLambda = 1f;
            World.DynamicTree.SweepCast(shape, jOrientation, jOrigin, jSweep, ref maxLambda, ref handler);
            SweepTerrains(shape, jOrientation, jOrigin, jSweep, hits);
            return hits.Count;
        }
        finally
        {
            ExitQuery();
        }
    }

    /// <summary>
    /// Sweep every registered terrain heightmap. Terrain is <see cref="IRayCastable"/> but not
    /// <see cref="ISweepTestable"/>, so the generic tree sweep cannot see it.
    /// </summary>
    private void SweepTerrains(RigidBodyShape shape, JQuaternion jOrientation,
        JVector jOrigin, JVector sweep, List<ShapeCastHit> hits)
    {
        if (_terrainProxies.Count == 0) return;

        shape.CalculateBoundingBox(jOrientation, jOrigin, out JBoundingBox startBox);
        shape.CalculateBoundingBox(jOrientation, jOrigin + sweep, out JBoundingBox endBox);
        var sweepBox = new JBoundingBox
        {
            Min = JVector.Min(startBox.Min, endBox.Min),
            Max = JVector.Max(startBox.Max, endBox.Max),
        };

        foreach (KeyValuePair<TerrainHeightmapProxy, TerrainInfo> entry in _terrainProxies)
            SweepAgainstTerrain(shape, jOrientation, jOrigin, sweep, entry.Key, sweepBox, hits);
    }

    /// <summary>
    /// Sweep a shape against terrain heightmap triangles within the sweep bounding box.
    /// </summary>
    private void SweepAgainstTerrain(RigidBodyShape shape, JQuaternion jOrientation,
        JVector jOrigin, JVector sweep, TerrainHeightmapProxy terrainProxy,
        JBoundingBox sweepBox, List<ShapeCastHit> hits)
    {
        if (!_terrainProxies.TryGetValue(terrainProxy, out var info))
            return;

        var hp = info.HeightProvider;
        var origin = info.Origin;
        float cs = info.CellSize;

        // Convert sweep AABB to grid coordinates
        int minX = Maths.Max(0, (int)Maths.Floor((sweepBox.Min.X - origin.X) / cs));
        int minZ = Maths.Max(0, (int)Maths.Floor((sweepBox.Min.Z - origin.Z) / cs));
        int maxX = Maths.Min(hp.Width - 1, (int)Maths.Ceiling((sweepBox.Max.X - origin.X) / cs));
        int maxZ = Maths.Min(hp.Height - 1, (int)Maths.Ceiling((sweepBox.Max.Z - origin.Z) / cs));

        float bestLambda = float.MaxValue;
        JVector bestNormal = JVector.Zero;
        JVector bestPointA = JVector.Zero;
        JVector bestPointB = JVector.Zero;

            for (int x = minX; x < maxX; x++)
            {
                for (int z = minZ; z < maxZ; z++)
                {
                    if (!hp.IsValidCell(x, z)) continue;
                    if (hp.IsCellHole(x, z)) continue; // holes never collide (upstream 95b44c405)
                    if (!hp.TryGetHeight(x, z, out float h00) ||
                    !hp.TryGetHeight(x + 1, z, out float h10) ||
                    !hp.TryGetHeight(x + 1, z + 1, out float h11) ||
                    !hp.TryGetHeight(x, z + 1, out float h01))
                    continue;

                // Two triangles per cell
                for (int tri = 0; tri < 2; tri++)
                {
                    CollisionTriangle triangle;
                    if (tri == 0)
                    {
                        triangle.A = new JVector(x * cs + origin.X, h00, z * cs + origin.Z);
                        triangle.B = new JVector((x + 1) * cs + origin.X, h11, (z + 1) * cs + origin.Z);
                        triangle.C = new JVector((x + 1) * cs + origin.X, h10, z * cs + origin.Z);
                    }
                    else
                    {
                        triangle.A = new JVector(x * cs + origin.X, h00, z * cs + origin.Z);
                        triangle.B = new JVector(x * cs + origin.X, h01, (z + 1) * cs + origin.Z);
                        triangle.C = new JVector((x + 1) * cs + origin.X, h11, (z + 1) * cs + origin.Z);
                    }

                    bool hit = NarrowPhase.Sweep(
                        shape, triangle,
                        jOrientation, JQuaternion.Identity,
                        jOrigin, JVector.Zero,
                        sweep, JVector.Zero,
                        out JVector pA, out JVector pB, out JVector n, out float lambda);

                    if (hit && lambda >= 0 && lambda <= 1.0f && lambda < bestLambda)
                    {
                        if (n.LengthSquared() <= 0)
                            n = JVector.Normalize((triangle.B - triangle.A) % (triangle.C - triangle.A));

                        bestLambda = lambda;
                        bestNormal = n;
                        bestPointA = pA;
                        bestPointB = pB;
                    }
                }
            }
        }

        if (bestLambda < float.MaxValue)
        {
            hits.Add(new ShapeCastHit
            {
                Hit = true,
                Fraction = bestLambda,
                Distance = bestLambda * sweep.Length(),
                Normal = -(new Float3(bestNormal.X, bestNormal.Y, bestNormal.Z)),
                Point = new Float3(bestPointA.X, bestPointA.Y, bestPointA.Z),
                HitPoint = new Float3(bestPointB.X, bestPointB.Y, bestPointB.Z),
                Rigidbody = null,
                Shape = null,
                Transform = null,
            });
        }
    }

    /// <summary>
    /// Generic shape cast that returns all hits with default layer mask.
    /// </summary>
    public int ShapeCastAll(RigidBodyShape shape, Quaternion orientation, Float3 origin, Float3 direction, float maxDistance, List<ShapeCastHit> hits)
    {
        return ShapeCastAll(shape, orientation, origin, direction, maxDistance, hits, LayerMask.Everything);
    }

    /// <summary>
    /// Generic shape cast that returns only the closest hit.
    /// </summary>
    /// <param name="shape">The shape to cast.</param>
    /// <param name="orientation">The orientation of the casting shape.</param>
    /// <param name="origin">Starting position of the shape.</param>
    /// <param name="direction">Direction to cast the shape.</param>
    /// <param name="maxDistance">Maximum distance to cast.</param>
    /// <param name="hitInfo">Information about the closest hit.</param>
    /// <param name="layerMask">Layer mask for filtering.</param>
    /// <returns>True if the shape hit something.</returns>
    public bool ShapeCast(RigidBodyShape shape, Quaternion orientation, Float3 origin, Float3 direction, float maxDistance, out ShapeCastHit hitInfo, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
    {
        hitInfo = default;
        if (maxDistance < 0f) return false;
        if (!ValidateQuery(origin, direction, maxDistance, nameof(ShapeCast))) return false;
        if (AutoSyncTransforms) SyncTransforms();

        EnterQuery(nameof(ShapeCast));
        try
        {
            direction = Float3.Normalize(direction);
            var jOrigin = new JVector(origin.X, origin.Y, origin.Z);
            var jSweep = new JVector(direction.X, direction.Y, direction.Z) * maxDistance;
            var jOrientation = new JQuaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);

            var handler = new ClosestSweepHandler
            {
                LayerMask = layerMask,
                HitTriggers = HitsTriggers(queryTriggerInteraction),
                QueryShape = shape,
                SweepLength = maxDistance,
            };
            float maxLambda = 1f;
            World.DynamicTree.SweepCast(shape, jOrientation, jOrigin, jSweep, ref maxLambda, ref handler);

            if (_terrainProxies.Count > 0)
            {
                _scratchCastHits.Clear();
                SweepTerrains(shape, jOrientation, jOrigin, jSweep, _scratchCastHits);
                for (int i = 0; i < _scratchCastHits.Count; i++)
                {
                    ShapeCastHit terrainHit = _scratchCastHits[i];
                    if (!handler.Hit.Hit || terrainHit.Fraction < handler.Hit.Fraction)
                        handler.Hit = terrainHit;
                }
            }

            hitInfo = handler.Hit;
            return hitInfo.Hit;
        }
        finally
        {
            ExitQuery();
        }
    }

    /// <summary>
    /// Generic shape cast that returns only the closest hit with default orientation and layer mask.
    /// </summary>
    public bool ShapeCast(RigidBodyShape shape, Float3 origin, Float3 direction, float maxDistance, out ShapeCastHit hitInfo)
    {
        return ShapeCast(shape, Quaternion.Identity, origin, direction, maxDistance, out hitInfo, LayerMask.Everything);
    }

    /// <summary>
    /// Casts a sphere along a direction and returns the closest hit.
    /// </summary>
    /// <param name="origin">Starting position of the sphere center.</param>
    /// <param name="radius">Radius of the sphere.</param>
    /// <param name="direction">Direction to cast the sphere.</param>
    /// <param name="maxDistance">Maximum distance to cast.</param>
    /// <param name="hitInfo">Information about what was hit.</param>
    /// <returns>True if the sphere hit something.</returns>
    public bool SphereCast(Float3 origin, float radius, Float3 direction, float maxDistance, out ShapeCastHit hitInfo)
    {
        return SphereCast(origin, radius, direction, maxDistance, out hitInfo, LayerMask.Everything);
    }

    /// <summary>
    /// Casts a sphere along a direction with layer filtering and returns the closest hit.
    /// </summary>
    public bool SphereCast(Float3 origin, float radius, Float3 direction, float maxDistance, out ShapeCastHit hitInfo, LayerMask layerMask)
    {
        var sphere = new SphereShape(radius);
        return ShapeCast(sphere, Quaternion.Identity, origin, direction, maxDistance, out hitInfo, layerMask);
    }

    /// <summary>
    /// Casts a sphere along a direction and returns all hits.
    /// </summary>
    /// <param name="origin">Starting position of the sphere center.</param>
    /// <param name="radius">Radius of the sphere.</param>
    /// <param name="direction">Direction to cast the sphere.</param>
    /// <param name="maxDistance">Maximum distance to cast.</param>
    /// <param name="hits">List to populate with all hits found.</param>
    /// <returns>Number of hits found.</returns>
    public int SphereCastAll(Float3 origin, float radius, Float3 direction, float maxDistance, List<ShapeCastHit> hits)
    {
        return SphereCastAll(origin, radius, direction, maxDistance, hits, LayerMask.Everything);
    }

    /// <summary>
    /// Casts a sphere along a direction with layer filtering and returns all hits.
    /// </summary>
    public int SphereCastAll(Float3 origin, float radius, Float3 direction, float maxDistance, List<ShapeCastHit> hits, LayerMask layerMask)
    {
        var sphere = new SphereShape(radius);
        return ShapeCastAll(sphere, Quaternion.Identity, origin, direction, maxDistance, hits, layerMask);
    }

    /// <summary>
    /// Casts a capsule along a direction and returns the closest hit.
    /// </summary>
    /// <param name="point1">Start point of the capsule's line segment.</param>
    /// <param name="point2">End point of the capsule's line segment.</param>
    /// <param name="radius">Radius of the capsule.</param>
    /// <param name="direction">Direction to cast the capsule.</param>
    /// <param name="maxDistance">Maximum distance to cast.</param>
    /// <param name="hitInfo">Information about what was hit.</param>
    /// <returns>True if the capsule hit something.</returns>
    public bool CapsuleCast(Float3 point1, Float3 point2, float radius, Float3 direction, float maxDistance, out ShapeCastHit hitInfo)
    {
        return CapsuleCast(point1, point2, radius, direction, maxDistance, out hitInfo, LayerMask.Everything);
    }

    /// <summary>
    /// Casts a capsule along a direction with layer filtering and returns the closest hit.
    /// </summary>
    public bool CapsuleCast(Float3 point1, Float3 point2, float radius, Float3 direction, float maxDistance, out ShapeCastHit hitInfo, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
    {
        // Calculate capsule properties
        Float3 capsuleCenter = (point1 + point2) * 0.5f;
        Float3 capsuleAxis = point2 - point1;
        float capsuleLength = Float3.Length(capsuleAxis);

        // Create a capsule shape (aligned along Y-axis)
        var capsule = new CapsuleShape(radius, capsuleLength);

        // Calculate orientation to align capsule with the segment
        Quaternion capsuleOrientation = CalculateCapsuleOrientation(capsuleAxis, capsuleLength);

        return ShapeCast(capsule, capsuleOrientation, capsuleCenter, direction, maxDistance, out hitInfo, layerMask,
            queryTriggerInteraction);
    }

    /// <summary>
    /// Casts a capsule along a direction and returns all hits.
    /// </summary>
    /// <param name="point1">Start point of the capsule's line segment.</param>
    /// <param name="point2">End point of the capsule's line segment.</param>
    /// <param name="radius">Radius of the capsule.</param>
    /// <param name="direction">Direction to cast the capsule.</param>
    /// <param name="maxDistance">Maximum distance to cast.</param>
    /// <param name="hits">List to populate with all hits found.</param>
    /// <returns>Number of hits found.</returns>
    public int CapsuleCastAll(Float3 point1, Float3 point2, float radius, Float3 direction, float maxDistance, List<ShapeCastHit> hits)
    {
        return CapsuleCastAll(point1, point2, radius, direction, maxDistance, hits, LayerMask.Everything);
    }

    /// <summary>
    /// Casts a capsule along a direction with layer filtering and returns all hits.
    /// </summary>
    public int CapsuleCastAll(Float3 point1, Float3 point2, float radius, Float3 direction, float maxDistance, List<ShapeCastHit> hits, LayerMask layerMask)
    {
        // Calculate capsule properties
        Float3 capsuleCenter = (point1 + point2) * 0.5f;
        Float3 capsuleAxis = point2 - point1;
        float capsuleLength = Float3.Length(capsuleAxis);

        // Create a capsule shape (aligned along Y-axis)
        var capsule = new CapsuleShape(radius, capsuleLength);

        // Calculate orientation to align capsule with the segment
        Quaternion capsuleOrientation = CalculateCapsuleOrientation(capsuleAxis, capsuleLength);

        return ShapeCastAll(capsule, capsuleOrientation, capsuleCenter, direction, maxDistance, hits, layerMask);
    }

    /// <summary>
    /// Helper method to calculate the orientation needed to align a capsule (Y-axis aligned) with a given axis.
    /// </summary>
    private static Quaternion CalculateCapsuleOrientation(Float3 capsuleAxis, float capsuleLength)
    {
        if (capsuleLength <= 1e-6)
            return Quaternion.Identity;

        Float3 normalizedAxis = capsuleAxis / capsuleLength;
        Float3 yAxis = new(0, 1, 0);

        // If axis is aligned with Y, no rotation needed
        if (Maths.Abs(Float3.Dot(normalizedAxis, yAxis) - 1.0) < 1e-6)
        {
            return Quaternion.Identity;
        }
        // If axis is opposite to Y, rotate 180 degrees around X
        else if (Maths.Abs(Float3.Dot(normalizedAxis, yAxis) + 1.0) < 1e-6)
        {
            return Quaternion.AxisAngle(new Float3(1, 0, 0), Maths.PI);
        }
        // Calculate rotation from Y-axis to the capsule axis
        else
        {
            Float3 rotAxis = Float3.Cross(yAxis, normalizedAxis);
            rotAxis = Float3.Normalize(rotAxis);
            float angle = Maths.Acos(Float3.Dot(yAxis, normalizedAxis));
            return Quaternion.AxisAngle(new Float3(rotAxis.X, rotAxis.Y, rotAxis.Z), angle);
        }
    }

    /// <summary>
    /// Casts a box along a direction and returns the closest hit.
    /// </summary>
    /// <param name="origin">Starting position of the box center.</param>
    /// <param name="size">Size of the box (width, height, depth).</param>
    /// <param name="orientation">Orientation of the box.</param>
    /// <param name="direction">Direction to cast the box.</param>
    /// <param name="maxDistance">Maximum distance to cast.</param>
    /// <param name="hitInfo">Information about what was hit.</param>
    /// <returns>True if the box hit something.</returns>
    public bool BoxCast(Float3 origin, Float3 size, Quaternion orientation, Float3 direction, float maxDistance, out ShapeCastHit hitInfo)
    {
        return BoxCast(origin, size, orientation, direction, maxDistance, out hitInfo, LayerMask.Everything);
    }

    /// <summary>
    /// Casts a box along a direction with layer filtering and returns the closest hit.
    /// </summary>
    public bool BoxCast(Float3 origin, Float3 size, Quaternion orientation, Float3 direction, float maxDistance, out ShapeCastHit hitInfo, LayerMask layerMask)
    {
        var box = new BoxShape(size.X, size.Y, size.Z);
        return ShapeCast(box, orientation, origin, direction, maxDistance, out hitInfo, layerMask);
    }

    /// <summary>
    /// Casts a box along a direction and returns all hits.
    /// </summary>
    /// <param name="origin">Starting position of the box center.</param>
    /// <param name="size">Size of the box (width, height, depth).</param>
    /// <param name="orientation">Orientation of the box.</param>
    /// <param name="direction">Direction to cast the box.</param>
    /// <param name="maxDistance">Maximum distance to cast.</param>
    /// <param name="hits">List to populate with all hits found.</param>
    /// <returns>Number of hits found.</returns>
    public int BoxCastAll(Float3 origin, Float3 size, Quaternion orientation, Float3 direction, float maxDistance, List<ShapeCastHit> hits)
    {
        return BoxCastAll(origin, size, orientation, direction, maxDistance, hits, LayerMask.Everything);
    }

    /// <summary>
    /// Casts a box along a direction with layer filtering and returns all hits.
    /// </summary>
    public int BoxCastAll(Float3 origin, Float3 size, Quaternion orientation, Float3 direction, float maxDistance, List<ShapeCastHit> hits, LayerMask layerMask)
    {
        var box = new BoxShape(size.X, size.Y, size.Z);
        return ShapeCastAll(box, orientation, origin, direction, maxDistance, hits, layerMask);
    }

    /// <summary>
    /// Casts a cylinder along a direction and returns the closest hit.
    /// </summary>
    /// <param name="origin">Starting position of the cylinder center.</param>
    /// <param name="radius">Radius of the cylinder.</param>
    /// <param name="height">Height of the cylinder.</param>
    /// <param name="orientation">Orientation of the cylinder.</param>
    /// <param name="direction">Direction to cast the cylinder.</param>
    /// <param name="maxDistance">Maximum distance to cast.</param>
    /// <param name="hitInfo">Information about what was hit.</param>
    /// <returns>True if the cylinder hit something.</returns>
    public bool CylinderCast(Float3 origin, float radius, float height, Quaternion orientation, Float3 direction, float maxDistance, out ShapeCastHit hitInfo)
    {
        return CylinderCast(origin, radius, height, orientation, direction, maxDistance, out hitInfo, LayerMask.Everything);
    }

    /// <summary>
    /// Casts a cylinder along a direction with layer filtering and returns the closest hit.
    /// </summary>
    public bool CylinderCast(Float3 origin, float radius, float height, Quaternion orientation, Float3 direction, float maxDistance, out ShapeCastHit hitInfo, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
    {
        var cylinder = new CylinderShape(height, radius);
        return ShapeCast(cylinder, orientation, origin, direction, maxDistance, out hitInfo, layerMask,
            queryTriggerInteraction);
    }

    /// <summary>
    /// Casts a cylinder along a direction and returns all hits.
    /// </summary>
    /// <param name="origin">Starting position of the cylinder center.</param>
    /// <param name="radius">Radius of the cylinder.</param>
    /// <param name="height">Height of the cylinder.</param>
    /// <param name="orientation">Orientation of the cylinder.</param>
    /// <param name="direction">Direction to cast the cylinder.</param>
    /// <param name="maxDistance">Maximum distance to cast.</param>
    /// <param name="hits">List to populate with all hits found.</param>
    /// <returns>Number of hits found.</returns>
    public int CylinderCastAll(Float3 origin, float radius, float height, Quaternion orientation, Float3 direction, float maxDistance, List<ShapeCastHit> hits)
    {
        return CylinderCastAll(origin, radius, height, orientation, direction, maxDistance, hits, LayerMask.Everything);
    }

    /// <summary>
    /// Casts a cylinder along a direction with layer filtering and returns all hits.
    /// </summary>
    public int CylinderCastAll(Float3 origin, float radius, float height, Quaternion orientation, Float3 direction, float maxDistance, List<ShapeCastHit> hits, LayerMask layerMask)
    {
        var cylinder = new CylinderShape(height, radius);
        return ShapeCastAll(cylinder, orientation, origin, direction, maxDistance, hits, layerMask);
    }

    /// <summary>
    /// Casts a cone along a direction and returns the closest hit.
    /// </summary>
    /// <param name="origin">Starting position of the cone center.</param>
    /// <param name="radius">Base radius of the cone.</param>
    /// <param name="height">Height of the cone.</param>
    /// <param name="orientation">Orientation of the cone.</param>
    /// <param name="direction">Direction to cast the cone.</param>
    /// <param name="maxDistance">Maximum distance to cast.</param>
    /// <param name="hitInfo">Information about what was hit.</param>
    /// <returns>True if the cone hit something.</returns>
    public bool ConeCast(Float3 origin, float radius, float height, Quaternion orientation, Float3 direction, float maxDistance, out ShapeCastHit hitInfo)
    {
        return ConeCast(origin, radius, height, orientation, direction, maxDistance, out hitInfo, LayerMask.Everything);
    }

    /// <summary>
    /// Casts a cone along a direction with layer filtering and returns the closest hit.
    /// </summary>
    public bool ConeCast(Float3 origin, float radius, float height, Quaternion orientation, Float3 direction, float maxDistance, out ShapeCastHit hitInfo, LayerMask layerMask)
    {
        var cone = new ConeShape(radius, height);
        return ShapeCast(cone, orientation, origin, direction, maxDistance, out hitInfo, layerMask);
    }

    /// <summary>
    /// Casts a cone along a direction and returns all hits.
    /// </summary>
    /// <param name="origin">Starting position of the cone center.</param>
    /// <param name="radius">Base radius of the cone.</param>
    /// <param name="height">Height of the cone.</param>
    /// <param name="orientation">Orientation of the cone.</param>
    /// <param name="direction">Direction to cast the cone.</param>
    /// <param name="maxDistance">Maximum distance to cast.</param>
    /// <param name="hits">List to populate with all hits found.</param>
    /// <returns>Number of hits found.</returns>
    public int ConeCastAll(Float3 origin, float radius, float height, Quaternion orientation, Float3 direction, float maxDistance, List<ShapeCastHit> hits)
    {
        return ConeCastAll(origin, radius, height, orientation, direction, maxDistance, hits, LayerMask.Everything);
    }

    /// <summary>
    /// Casts a cone along a direction with layer filtering and returns all hits.
    /// </summary>
    public int ConeCastAll(Float3 origin, float radius, float height, Quaternion orientation, Float3 direction, float maxDistance, List<ShapeCastHit> hits, LayerMask layerMask)
    {
        var cone = new ConeShape(radius, height);
        return ShapeCastAll(cone, orientation, origin, direction, maxDistance, hits, layerMask);
    }

    #endregion

    #region Overlap Queries

    /// <summary>
    /// Generic overlap query that returns all colliders overlapping the given shape.
    /// </summary>
    /// <param name="shape">The shape to test for overlaps.</param>
    /// <param name="orientation">The orientation of the shape.</param>
    /// <param name="position">Position of the shape.</param>
    /// <param name="hits">List to populate with all overlapping colliders.</param>
    /// <param name="layerMask">Layer mask for filtering.</param>
    /// <returns>Number of overlapping colliders found.</returns>
    public int Overlap(RigidBodyShape shape, Quaternion orientation, Float3 position, List<ShapeCastHit> hits, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
    {
        ArgumentNullException.ThrowIfNull(hits);
        hits.Clear();
        if (AutoSyncTransforms) SyncTransforms();

        var jPosition = new JVector(position.X, position.Y, position.Z);
        var jOrientation = new JQuaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
        shape.CalculateBoundingBox(jOrientation, jPosition, out JBoundingBox shapeBounds);

        _queryProxies.Clear();
        World.DynamicTree.Query(_queryProxies, in shapeBounds);

        bool hitTriggers = HitsTriggers(queryTriggerInteraction);
        foreach (IDynamicTreeProxy proxy in _queryProxies)
        {
            if (proxy is not RigidBodyShape targetShape) continue;
            if (ReferenceEquals(targetShape, shape)) continue;
            if (!AllowQueryShape(targetShape, layerMask, hitTriggers)) continue;

            Jitter2.Dynamics.RigidBody targetBody = targetShape.RigidBody;

            bool overlaps = NarrowPhase.MprEpa(
                shape, targetShape,
                jOrientation, targetBody.Data.Orientation,
                jPosition, targetBody.Data.Position,
                out JVector pointA, out JVector pointB, out JVector normal, out float penetration);

            if (overlaps && penetration > 0)
            {
                Rigidbody3D targetRigidbody = ColliderBinding.GetRigidbody(targetShape);
                Collider targetCollider = ColliderBinding.GetCollider(targetShape);
                hits.Add(new ShapeCastHit
                {
                    Hit = true,
                    Fraction = 0,
                    Penetration = penetration,
                    Normal = -(new Float3(normal.X, normal.Y, normal.Z)),
                    Point = new Float3(pointA.X, pointA.Y, pointA.Z),
                    HitPoint = new Float3(pointB.X, pointB.Y, pointB.Z),
                    Rigidbody = targetRigidbody,
                    Shape = targetShape,
                    Collider = targetCollider,
                    Transform = targetCollider?.GameObject?.Transform ?? targetRigidbody?.GameObject?.Transform
                });
            }
        }

        return hits.Count;
    }

    /// <summary>
    /// Generic overlap query with default layer mask.
    /// </summary>
    public int Overlap(RigidBodyShape shape, Quaternion orientation, Float3 position, List<ShapeCastHit> hits)
    {
        return Overlap(shape, orientation, position, hits, LayerMask.Everything);
    }

    /// <summary>
    /// Tests if a sphere overlaps with any colliders.
    /// </summary>
    /// <param name="position">Center position of the sphere.</param>
    /// <param name="radius">Radius of the sphere.</param>
    /// <param name="hits">List to populate with all overlapping colliders.</param>
    /// <returns>Number of overlapping colliders found.</returns>
    public int OverlapSphere(Float3 position, float radius, List<ShapeCastHit> hits)
    {
        return OverlapSphere(position, radius, hits, LayerMask.Everything);
    }

    /// <summary>
    /// Tests if a sphere overlaps with any colliders with layer filtering.
    /// </summary>
    public int OverlapSphere(Float3 position, float radius, List<ShapeCastHit> hits, LayerMask layerMask)
    {
        var sphere = new SphereShape(radius);
        return Overlap(sphere, Quaternion.Identity, position, hits, layerMask);
    }

    /// <summary>
    /// Tests if a capsule overlaps with any colliders.
    /// </summary>
    /// <param name="point1">Start point of the capsule's line segment.</param>
    /// <param name="point2">End point of the capsule's line segment.</param>
    /// <param name="radius">Radius of the capsule.</param>
    /// <param name="hits">List to populate with all overlapping colliders.</param>
    /// <returns>Number of overlapping colliders found.</returns>
    public int OverlapCapsule(Float3 point1, Float3 point2, float radius, List<ShapeCastHit> hits)
    {
        return OverlapCapsule(point1, point2, radius, hits, LayerMask.Everything);
    }

    /// <summary>
    /// Tests if a capsule overlaps with any colliders with layer filtering.
    /// </summary>
    public int OverlapCapsule(Float3 point1, Float3 point2, float radius, List<ShapeCastHit> hits, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
    {
        // Calculate capsule properties
        Float3 capsuleCenter = (point1 + point2) * 0.5f;
        Float3 capsuleAxis = point2 - point1;
        float capsuleLength = Float3.Length(capsuleAxis);

        // Create a capsule shape (aligned along Y-axis)
        var capsule = new CapsuleShape(radius, capsuleLength);

        // Calculate orientation to align capsule with the segment
        Quaternion capsuleOrientation = CalculateCapsuleOrientation(capsuleAxis, capsuleLength);

        return Overlap(capsule, capsuleOrientation, capsuleCenter, hits, layerMask, queryTriggerInteraction);
    }

    /// <summary>
    /// Tests if a box overlaps with any colliders.
    /// </summary>
    /// <param name="position">Center position of the box.</param>
    /// <param name="size">Size of the box (width, height, depth).</param>
    /// <param name="orientation">Orientation of the box.</param>
    /// <param name="hits">List to populate with all overlapping colliders.</param>
    /// <returns>Number of overlapping colliders found.</returns>
    public int OverlapBox(Float3 position, Float3 size, Quaternion orientation, List<ShapeCastHit> hits)
    {
        return OverlapBox(position, size, orientation, hits, LayerMask.Everything);
    }

    /// <summary>
    /// Tests if a box overlaps with any colliders with layer filtering.
    /// </summary>
    public int OverlapBox(Float3 position, Float3 size, Quaternion orientation, List<ShapeCastHit> hits, LayerMask layerMask)
    {
        var box = new BoxShape(size.X, size.Y, size.Z);
        return Overlap(box, orientation, position, hits, layerMask);
    }

    /// <summary>
    /// Tests if a cylinder overlaps with any colliders.
    /// </summary>
    /// <param name="position">Center position of the cylinder.</param>
    /// <param name="radius">Radius of the cylinder.</param>
    /// <param name="height">Height of the cylinder.</param>
    /// <param name="orientation">Orientation of the cylinder.</param>
    /// <param name="hits">List to populate with all overlapping colliders.</param>
    /// <returns>Number of overlapping colliders found.</returns>
    public int OverlapCylinder(Float3 position, float radius, float height, Quaternion orientation, List<ShapeCastHit> hits)
    {
        return OverlapCylinder(position, radius, height, orientation, hits, LayerMask.Everything);
    }

    /// <summary>
    /// Tests if a cylinder overlaps with any colliders with layer filtering.
    /// </summary>
    public int OverlapCylinder(Float3 position, float radius, float height, Quaternion orientation, List<ShapeCastHit> hits, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
    {
        var cylinder = new CylinderShape(height, radius);
        return Overlap(cylinder, orientation, position, hits, layerMask, queryTriggerInteraction);
    }

    /// <summary>
    /// Tests if a cone overlaps with any colliders.
    /// </summary>
    /// <param name="position">Center position of the cone.</param>
    /// <param name="radius">Base radius of the cone.</param>
    /// <param name="height">Height of the cone.</param>
    /// <param name="orientation">Orientation of the cone.</param>
    /// <param name="hits">List to populate with all overlapping colliders.</param>
    /// <returns>Number of overlapping colliders found.</returns>
    public int OverlapCone(Float3 position, float radius, float height, Quaternion orientation, List<ShapeCastHit> hits)
    {
        return OverlapCone(position, radius, height, orientation, hits, LayerMask.Everything);
    }

    /// <summary>
    /// Tests if a cone overlaps with any colliders with layer filtering.
    /// </summary>
    public int OverlapCone(Float3 position, float radius, float height, Quaternion orientation, List<ShapeCastHit> hits, LayerMask layerMask)
    {
        var cone = new ConeShape(radius, height);
        return Overlap(cone, orientation, position, hits, layerMask);
    }

    #endregion

    #region Check Queries

    /// <summary>
    /// Checks if a sphere overlaps with any colliders.
    /// </summary>
    /// <param name="position">Center position of the sphere.</param>
    /// <param name="radius">Radius of the sphere.</param>
    /// <returns>True if the sphere overlaps with any collider.</returns>
    public bool CheckSphere(Float3 position, float radius)
    {
        return CheckSphere(position, radius, LayerMask.Everything);
    }

    /// <summary>
    /// Checks if a sphere overlaps with any colliders with layer filtering.
    /// </summary>
    public bool CheckSphere(Float3 position, float radius, LayerMask layerMask)
    {
        return OverlapSphere(position, radius, _scratchCastHits, layerMask) > 0;
    }

    /// <summary>
    /// Checks if a capsule overlaps with any colliders.
    /// </summary>
    /// <param name="point1">Start point of the capsule's line segment.</param>
    /// <param name="point2">End point of the capsule's line segment.</param>
    /// <param name="radius">Radius of the capsule.</param>
    /// <returns>True if the capsule overlaps with any collider.</returns>
    public bool CheckCapsule(Float3 point1, Float3 point2, float radius)
    {
        return CheckCapsule(point1, point2, radius, LayerMask.Everything);
    }

    /// <summary>
    /// Checks if a capsule overlaps with any colliders with layer filtering.
    /// </summary>
    public bool CheckCapsule(Float3 point1, Float3 point2, float radius, LayerMask layerMask)
    {
        return OverlapCapsule(point1, point2, radius, _scratchCastHits, layerMask) > 0;
    }

    /// <summary>
    /// Checks if a box overlaps with any colliders.
    /// </summary>
    /// <param name="position">Center position of the box.</param>
    /// <param name="size">Size of the box (width, height, depth).</param>
    /// <param name="orientation">Orientation of the box.</param>
    /// <returns>True if the box overlaps with any collider.</returns>
    public bool CheckBox(Float3 position, Float3 size, Quaternion orientation)
    {
        return CheckBox(position, size, orientation, LayerMask.Everything);
    }

    /// <summary>
    /// Checks if a box overlaps with any colliders with layer filtering.
    /// </summary>
    public bool CheckBox(Float3 position, Float3 size, Quaternion orientation, LayerMask layerMask)
    {
        return OverlapBox(position, size, orientation, _scratchCastHits, layerMask) > 0;
    }

    /// <summary>
    /// Checks if a cylinder overlaps with any colliders.
    /// </summary>
    /// <param name="position">Center position of the cylinder.</param>
    /// <param name="radius">Radius of the cylinder.</param>
    /// <param name="height">Height of the cylinder.</param>
    /// <param name="orientation">Orientation of the cylinder.</param>
    /// <returns>True if the cylinder overlaps with any collider.</returns>
    public bool CheckCylinder(Float3 position, float radius, float height, Quaternion orientation)
    {
        return CheckCylinder(position, radius, height, orientation, LayerMask.Everything);
    }

    /// <summary>
    /// Checks if a cylinder overlaps with any colliders with layer filtering.
    /// </summary>
    public bool CheckCylinder(Float3 position, float radius, float height, Quaternion orientation, LayerMask layerMask)
    {
        return OverlapCylinder(position, radius, height, orientation, _scratchCastHits, layerMask) > 0;
    }

    /// <summary>
    /// Checks if a cone overlaps with any colliders.
    /// </summary>
    /// <param name="position">Center position of the cone.</param>
    /// <param name="radius">Base radius of the cone.</param>
    /// <param name="height">Height of the cone.</param>
    /// <param name="orientation">Orientation of the cone.</param>
    /// <returns>True if the cone overlaps with any collider.</returns>
    public bool CheckCone(Float3 position, float radius, float height, Quaternion orientation)
    {
        return CheckCone(position, radius, height, orientation, LayerMask.Everything);
    }

    /// <summary>
    /// Checks if a cone overlaps with any colliders with layer filtering.
    /// </summary>
    public bool CheckCone(Float3 position, float radius, float height, Quaternion orientation, LayerMask layerMask)
    {
        return OverlapCone(position, radius, height, orientation, _scratchCastHits, layerMask) > 0;
    }

    #endregion

    #region Terrain Collision

    /// <summary>
    /// Registers a terrain collider with the physics world.
    /// </summary>
    /// <param name="heightmapProxy">The terrain heightmap proxy for raycasting.</param>
    /// <param name="collisionFilter">The terrain collision filter for broad phase collision detection.</param>
    /// <param name="heightProvider">Sampler used to query terrain height at arbitrary points during contact resolution.</param>
    /// <param name="terrainOrigin">World-space origin (corner) of the terrain grid.</param>
    /// <param name="cellSize">World-space spacing between height samples in the grid.</param>
    public void RegisterTerrain(TerrainHeightmapProxy heightmapProxy, TerrainCollisionFilter collisionFilter,
        ITerrainHeightProvider heightProvider, JVector terrainOrigin, float cellSize)
    {
        if (heightmapProxy == null || collisionFilter == null)
            return;

        World.DynamicTree.AddProxy(heightmapProxy, false);
        _compositeBroadPhaseFilter.AddFilter(collisionFilter);

        _terrainProxies[heightmapProxy] = new TerrainInfo
        {
            HeightProvider = heightProvider,
            Origin = terrainOrigin,
            CellSize = cellSize,
        };
    }

    /// <summary>
    /// Unregisters a terrain collider from the physics world.
    /// </summary>
    /// <param name="heightmapProxy">The terrain heightmap proxy to remove.</param>
    /// <param name="collisionFilter">The terrain collision filter to remove.</param>
    public void UnregisterTerrain(TerrainHeightmapProxy heightmapProxy, TerrainCollisionFilter collisionFilter)
    {
        if (heightmapProxy == null || collisionFilter == null)
            return;

        _terrainProxies.Remove(heightmapProxy);

        if (heightmapProxy.SetIndex != -1)
        {
            World.DynamicTree.RemoveProxy(heightmapProxy);
        }

        // Remove the terrain collision filter from the composite filter
        _compositeBroadPhaseFilter.RemoveFilter(collisionFilter);
    }

    #endregion
}
