# XEngine patches to the Jitter2 kernel

Every local modification to `Jitter2/` is recorded here so a version bump can be replayed
deliberately. Each change is marked in the source with a `XENGINE:` comment, so
`rg "XENGINE:" Jitter2` enumerates the live patch set and can be cross-checked against this file.

Baseline for diffing: `.upstream-baseline/Jitter2` (commit `8bf70b32`). See `README.md`.

## Conventions

- Patches are **additive** where possible: new members and new overloads rather than changed
  signatures, so upstream call sites and the vendored `Tests/` suite keep compiling.
- Anything that changes existing numerical behaviour must keep upstream defaults, so an
  unconfigured world simulates exactly as upstream does.
- "Upstreamable" marks a patch that is generally useful and could be offered upstream; "XEngine
  specific" marks one that only makes sense for this engine's Unity-facing semantics.

## Patch log

The initial vendoring is a verbatim copy of upstream: only the project files were replaced. The
entries below are appended in order.

### Worker index for callbacks

- **Files**: `Parallelization/ThreadPool.cs`, `Collision/DynamicTree/DynamicTree.cs`,
  `Collision/CollisionFilter/IBroadPhaseFilter.cs`,
  `Collision/CollisionFilter/INarrowPhaseFilter.cs`, `World.Detect.cs`, `World.Step.cs`
- **Motive**: filters run concurrently during the narrow phase but upstream tells the callback
  nothing about which thread it is on, so anything a filter wants to record has to go through a
  concurrent collection or a thread-local lookup. Trigger overlaps and pair events are exactly
  that kind of recording. A worker index turns it into an indexed write into per-worker storage.
- **Shape of change**: additive. `ThreadPool.CurrentWorkerIndex` (thread-static, published in
  `ThreadProc`; the thread that calls `Execute` is worker 0). `DynamicTree.EnumerateOverlaps` gains
  an overload taking `Action<IDynamicTreeProxy, IDynamicTreeProxy, int>`, resolving the thread-static
  once per batch rather than per pair. Both filter interfaces gain a `workerIndex` overload as a
  *default interface method* forwarding to the existing signature, so every existing implementation
  (including upstream's `TriangleEdgeCollisionFilter`) compiles and behaves unchanged. `World.Detect`
  takes the index and calls the new overloads.
- **Note**: the pool does work stealing, so a task is not bound to the queue it was enqueued to.
  The index therefore identifies the running thread, not the queue, which is what gives exclusive
  access to per-worker storage.
- **Defaults**: unchanged. A world with no filters, or with filters that implement only the
  worker-agnostic overload, behaves exactly as upstream.
- **Upstreamable**: yes.
- **Tests**: covered indirectly by all of `Tests/` (the narrow phase runs on this path);
  `TriangleEdgeCollisionFilter` exercises the forwarding default implementation.

### Shape owner and shape flags

- **Files**: `Collision/Shapes/Shape.cs`, `Collision/Shapes/ShapeFlags.cs` (new)
- **Motive**: results that name a shape (collision callbacks, ray and sweep hits) have to be
  resolved back to the engine object that created it. `RigidBody.Tag` is per body, and one body
  routinely owns shapes from several colliders, so it cannot answer the question. The alternative,
  a side table keyed by `ShapeId`, would have to be safe for the concurrent narrow phase.
- **Shape of change**: additive. `Shape.Owner` (`object?`, engine never reads it),
  `Shape.Flags` plus the `Shape.IsSensor` / `Shape.ReportsPairEvents` accessors over it.
- **Deviation from the plan**: the plan called for a listener *bitmap* indexed densely by shape,
  copying BEPU's `IndexSet` pre-check. BEPU needs a side bitmap because its collidables are handles
  into unmanaged buffers with no object to hang a flag on. Jitter2 shapes are managed objects, and
  `ShapeId` is a monotonic `ulong` rather than a dense index, so the equivalent here is a flags field
  on the shape: it answers the same question in one read of a cache line the narrow phase has already
  touched, with no capacity tracking and no index remapping when shapes are added or removed.
- **Defaults**: `ShapeFlags.None`, so a shape that opts into nothing takes the upstream path.
- **Upstreamable**: `Owner` yes; the flags are XEngine specific until sensors land upstream.
- **Tests**: `Tests/` confirms no behavioural change; engine-side coverage in `PhysicsTests`.

### Native sensors

- **Files**: `World.PairEvents.cs` (new), `World.Detect.cs`, `World.Step.cs`, `World.cs`
- **Motive**: Unity trigger volumes detect overlap without responding to it. Upstream has no such
  concept. Emulating one through `INarrowPhaseFilter` — returning `false` for its side effects — still
  pays for the `MprEpa` manifold, and the filter has no way to report an overlap outward, so the
  engine would have to keep its own concurrent pair table beside it. Forking `Detect` avoids both:
  the sensor branch never reaches `RegisterContact`, so there is no arbiter to allocate and nothing
  for the solver to iterate.
- **Shape of change**: `Detect` forks after `MprEpa` on `ShapeFlags.Sensor`. New pair bookkeeping in
  `World.PairEvents.cs` exposes `SensorOverlaps` (the current, "stay" set) plus
  `SensorOverlapsBegan` / `SensorOverlapsEnded` for a step's transitions. `StepInternal` calls
  `PrepareSensorOverlaps` before the narrow phase and `FlushSensorOverlaps` after it; `World.Clear`
  drops tracked overlaps.
- **Two upstream early-outs, treated differently**:
  - *Neither body dynamic* is relaxed for sensor pairs. Unity reports trigger events for a kinematic
    body moving through static geometry; upstream skips the pair because there is nothing to solve.
  - *Both bodies inactive* is deliberately **left alone**. Relaxing it would run `MprEpa` every step
    for every sleeping pair, which is the cost sleeping exists to avoid. Sleep correctness comes from
    the freshness pre-pass instead.
- **Sleep correctness**: `PrepareSensorOverlaps` pre-marks a pair fresh when both bodies are inactive,
  following BEPU's contact-event bookkeeping, so the sweep does not mistake "not tested" for
  "separated". The addition over BEPU is the bounding-box test: pre-marking on sleep alone would
  never end an overlap after a sensor is moved off a body that stays asleep. Shapes that have left the
  world are excluded here too, since they have no body left to query.
- **`touching` definition**: overlap requires `penetration >= 0`, matching BEPU. Speculative contacts
  mean "about to touch", so sensors skip that branch entirely.
- **Determinism**: per-worker buffers are merged in worker order and the transition lists are sorted
  by shape id, so the reported sequence does not depend on thread scheduling.
- **Defaults**: `ShapeFlags.None`, so no shape is a sensor unless asked. Tracking additionally
  requires `ReportsPairEvents`, so a trigger with no listener costs no bookkeeping.
- **Upstreamable**: yes in principle; the sensor concept is generally useful.
- **Tests**: `XEngine.Runtime.Test/Physics/JitterSensorTests.cs` (13 tests) drives the kernel
  directly, covering pass-through, kinematic-vs-static, the three sleep/removal paths, speculative
  suppression, and non-sensor pairs still colliding. Kept in the engine test project rather than
  `Tests/`, because `UpdateSources.ps1 -Force` mirrors that folder and would delete files upstream
  does not have.

### Shapes on the arbiter

- **Files**: `Dynamics/Arbiter.cs`, `World.Detect.cs`
- **Motive**: upstream identifies an arbiter by the two bodies and an opaque `ulong` pair key built
  from shape ids. That is all the solver needs, but Unity's collision callbacks are per *collider*:
  `Collision.Collider` and each `ContactPoint.ThisCollider` name a shape, not a body, and one body
  routinely carries several. Recovering the shapes from the key would mean the engine keeping its own
  `ulong -> shape` side table and holding it in sync with shape add/remove — bookkeeping the kernel
  already does when it builds the arbiter.
- **Shape of change**: `Arbiter` gains `Shape1` / `Shape2` (`internal set`), nulled in
  `ReturnToPool` alongside `Body1` / `Body2`. `GetOrCreateArbiter` grows a shape-taking overload that
  stamps them on creation; the existing `id0`/`id1` overload delegates to it with nulls, so external
  callers registering synthetic contacts are unaffected. `Detect` uses the shape overload.
- **Cost**: two reference fields on a pooled, per-pair object that already holds two body references.
  Nothing is looked up or computed — the shapes are in hand at the call site.
- **Nullability**: `Shape1` / `Shape2` are declared nullable precisely because the `id0`/`id1` entry
  point cannot supply them. Engine-side dispatch skips an arbiter whose shapes are null rather than
  guessing, so a hand-registered contact silently produces no callback instead of a wrong one.
- **Upstreamable**: yes; it is additive and costs nothing when unused.
- **Tests**: exercised through `XEngine.Runtime.Test/Physics/PhysicsCallbackTests.cs`, which asserts
  per-collider identity and contact-normal orientation on both sides of a pair.

### Per-shape materials

- **Files**: `Dynamics/MaterialCombine.cs` (new), `Collision/Shapes/RigidBodyShape.cs`,
  `Dynamics/Contact.cs`, `World.Detect.cs`
- **Motive**: upstream carries friction and restitution on the *body* and combines a pair with `max`.
  A body with several shapes therefore cannot have several surfaces — a crate with one rubber face, an
  ice patch welded into a stone floor — and `max` is only one of the four combine rules Unity exposes.
- **Shape of change**: `RigidBodyShape` gains `Friction`, `Restitution` (both `Real?`) and
  `FrictionCombine`, `RestitutionCombine` (both `MaterialCombine?`). `ContactData.Init` gains a
  shape-taking overload that resolves them into the existing `Friction` / `Restitution` fields; the
  body-only overload keeps upstream's `max`. `Detect` passes the shapes it already has.
- **Why resolve at arbiter creation rather than every step**: the solver reads two `Real` fields, so a
  persisting pair costs nothing. Re-resolving each step would mean touching every arbiter to recompute a
  value that almost never changes. It also keeps `ContactData` free of managed references, which is
  required — it lives in unmanaged memory. The cost is staleness, addressed by `World.RefreshMaterial`,
  called from the engine's material setter: it re-resolves in place, so accumulated impulses survive and
  a change mid-contact does not pop.
- **Nullability is the load-bearing detail**, in two places:
  - *Coefficients*: `null` means "inherit from the body". A concrete default would mean a shape nobody
    gave a material silently outvoted `RigidBody.Friction`, breaking upstream's contract.
  - *Combine modes*: `null` means "abstain". This is subtler. Modes reconcile by ordinal (PhysX's rule,
    which is why the enum values are pinned), and `Maximum` is the dominant ordinal. So defaulting an
    unset shape to `Maximum` — the obvious choice for upstream compatibility — would make *every*
    material's mode unreachable whenever it touched plain geometry. Defaulting to `Average` instead
    would change how material-less bodies behave. Abstaining satisfies both: two abstaining shapes
    combine with `Maximum` (upstream `max`), and one abstaining shape lets the other's mode apply
    unopposed.
- **Static friction**: not added. The solver applies one Coulomb bound (`Friction * normalImpulse`) at a
  single site in each of the scalar and SIMD iterate paths, and the SIMD one is written specifically to
  keep the clamp vectorized. Splitting it needs a slip test and a branch there. Unity's `staticFriction`
  is therefore not exposed rather than being stored and ignored.
- **Bounce threshold**: `Contact.Initialize` skipped restitution below a hardcoded approach speed of
  `1.0`. That literal is now `ContactData.RestitutionThreshold`, fed from `World.RestitutionThreshold` —
  Unity's `Physics.bounceThreshold`. The `Initialize` parameter defaults to `1.0`, so the vendored
  upstream tests that call it directly stay byte-identical to upstream and behave as before.
- **Defaults reproduce upstream exactly**: all four shape fields `null` reduces resolution to
  `max(body1, body2)`, and the threshold default matches the old literal. Verified by the 320 vendored
  upstream tests passing with no edits.
- **Upstreamable**: yes; per-shape materials and a tunable bounce threshold are both generally useful.
- **Tests**: `XEngine.Runtime.Test/Physics/PhysicsMaterialTests.cs` (28 tests) covers each combine mode,
  ordinal precedence, abstention, body fallback, two materials on one body, in-place refresh preserving
  impulses, range validation, and the threshold's effect on whether an impact bounces.

### Frozen axes

- **Files**: `Dynamics/AxisLock.cs` (new), `Dynamics/RigidBody.cs`, `World.Step.cs`
- **Motive**: Unity's `RigidbodyConstraints` freezes individual world axes. Upstream has no equivalent,
  and the usual workaround — zeroing velocity from a `PostStep` callback — cannot work, because the pose
  has already been integrated by the time it runs.
- **Shape of change**: an `AxisLock` flags enum (three linear, three angular). `RigidBodyData.LockedAxes`
  stores it in bits 4–9 of the existing `Flags` int; `ApplyAxisLocks()` clears the locked velocity
  components. `RigidBody.LockedAxes` is the public setter and rebuilds the world inertia.
- **Why the bits and not a field**: `RigidBodyData` has `LayoutKind.Explicit` with a fixed
  `Precision.RigidBodyDataSize` and is allocated out of unmanaged memory. Widening it would ripple into
  that constant and every buffer stride. `Flags` already packs motion type (0–1), active (2) and
  gyroscopic (3), leaving bits 4–9 free — exactly six, one per axis.
- **Where the mask runs, and why placement is the whole patch**: velocity is written at three points in a
  substep — `IntegrateForces` (gravity and queued forces), the solver's iterate passes, and
  `IntegrateVelocities` (gyroscopic). Only `IntegrateVelocities` turns velocity into pose. So the call
  sited immediately *before* the pose integration there is what makes a lock exact; nothing the solver
  just did on a locked axis can reach the transform. **Do not move it below the integration.** The other
  two calls are quality, not correctness:
  - In `IntegrateForces`, after the force deltas land, so the solver does not size contact impulses
    against motion that is going to be discarded.
  - At the top of `RigidBody.Update`, before the sleep test reads the velocity magnitudes. Two things
    depend on this: a body held still purely by its locks is allowed to fall asleep instead of being kept
    awake forever by gravity it can never act on, and a caller reading velocity between steps does not
    see the residue `RelaxVelocities` leaves after the last integration.
- **Angular locks also project the inertia**: `AxisLock.ProjectInverseInertia` zeroes row *and* column
  *i* of `InverseInertiaWorld` for each locked angular axis — the projection `P·I⁻¹·P`, which keeps the
  tensor symmetric and positive semi-definite and reads as infinite inertia about that axis. The velocity
  mask alone would already stop the rotation, but a contact would size its impulse as if the body could
  spin, so friction and restitution would come out softer than they should for a body that cannot. It
  also makes torque about a locked axis a no-op for free, since torque reaches velocity through this
  tensor. Applied at both sites that build the tensor (`UpdateWorldInertia` and the inlined copy in
  `RigidBody.Update`).
- **Kinematic bodies**: locks apply to them too, since `IntegrateVelocities` runs for anything non-static.
  Unity treats constraints as a dynamics-only feature, so a kinematic body driven by velocity along a
  frozen axis moves in Unity and does not here. "Frozen means frozen" was chosen as the less surprising
  reading of the switch.
- **Cost when unused**: one `int` mask and compare per body per call site, no branch misprediction of
  consequence and no memory touched beyond `Flags`, which is already in cache.
- **Upstreamable**: yes, and it is self-contained.
- **Tests**: `XEngine.Runtime.Test/Physics/RigidbodyConstraintsTests.cs` (28 tests) covers the bitfield
  round trip and its non-interference with the other `Flags` bits, the inertia projection per axis and
  its neutrality on the linear solve, and engine-level behaviour: holding height under gravity, resisting
  a 200× mass contact push and a 40 u/s impact, leaving free axes free, reading back as zero velocity,
  surviving `OnValidate`, being liftable at runtime, and still sleeping.

### Continuous detection modes

- **Files**: `Dynamics/ContinuousDetectionMode.cs` (new), `Dynamics/RigidBody.cs`,
  `Collision/Shapes/RigidBodyShape.cs`, `World.Detect.cs`
- **Motive**: upstream's anti-tunnelling is one `bool` per body, `EnableSpeculativeContacts`, and a pair
  sweeps if *either* side sets it. So the only choice is "sweep against everything or nothing". Unity
  instead lets a body say *which* partners are worth the cost, which matters because the sweep is the
  expensive part of the frame for a fast body in a crowded scene.
- **Shape of change**: a four-value `ContinuousDetectionMode` enum on `RigidBody.ContinuousDetection`,
  and `World.WantsSweep(a, b)` replacing the two-`bool` disjunction in `Detect`. The machinery below is
  untouched — every mode still routes to the same `NarrowPhase.Sweep`. The enum only decides which pairs
  reach it.
- **The pair rule is asymmetric on purpose.** Either side asking is enough, since one sweep protects
  both. But `Continuous` declines a dynamic partner *even when that partner is also continuous*, while
  `ContinuousDynamic` accepts it if it is. That reads like an inconsistency and is Unity's actual
  behaviour: `Continuous` means "do not tunnel through the level", `ContinuousDynamic` adds "and not
  through other fast things either". Keeping the asymmetry is what makes the two modes different prices
  rather than two spellings of one.
- **Bounds expansion is not optional for any non-discrete mode.** `UpdateWorldBoundingBox` previously
  keyed the swept expansion off `EnableSpeculativeContacts`; it now expands for anything that is not
  `Discrete`. A pair the broad phase never offers cannot be swept, so a mode that skipped the expansion
  would silently do nothing. The corollary bounds the feature: **only the moving body's own expansion can
  save it.** A static wall in `Continuous` mode does not protect a `Discrete` projectile, because the
  wall's bounds do not grow along a path it is not travelling and the projectile's do not either. This is
  a broad-phase property, not a gap in the pair rule, and the tests assert it in both directions.
- **Per-shape velocity gate**: the speculative branch compared relative approach speed against the
  world-wide `SpeculativeVelocityThreshold`. `RigidBodyShape.SweepVelocityThreshold` (`Real?`, `null`
  inherits the world value) now overrides it per shape, and a pair gates on the **lower** of the two.
  The threshold that avoids tunnelling is roughly `thickness / dt`, so it was never really global: one
  value has to be tuned for the thinnest shape in the scene, and every thicker shape then pays for
  sweeps it cannot need. Taking the minimum means the more tunnel-prone shape of the pair decides.
- **`EnableSpeculativeContacts` survives as a view** over the enum — reading it is
  `ContinuousDetection != Discrete`, setting it selects `Speculative`, which is exactly "sweep every
  partner", what the boolean always meant. Call sites and the vendored upstream tests are unaffected.
- **Cost when unused**: `Discrete` on both sides short-circuits `WantsSweep` before either
  `WantsSweepAgainst` call, so a discrete scene does one enum compare more per pair than upstream's two
  `bool` loads, and no shape's bounds grow.
- **Upstreamable**: yes. The enum subsumes the boolean without changing its meaning, and per-shape
  thresholds are useful independently.
- **Tests**: `XEngine.Runtime.Test/Physics/CollisionDetectionModeTests.cs` (34 tests) fires a projectile
  at a thin wall for every mode pairing, and covers bounds expansion per mode, the legacy boolean's
  mapping in both directions, per-shape thresholds including the pair minimum and the
  world-value fallback, and the broad-phase asymmetry above.

### Struct-generic query handlers

- **Files**: `Collision/DynamicTree/IRayHitHandler.cs` (new),
  `Collision/DynamicTree/DynamicTree.RayCast.cs`,
  `Collision/DynamicTree/DynamicTree.SweepCast.cs`,
  `Collision/DynamicTree/DynamicTree.FindNearest.cs`
- **Motive**: the engine's Unity-facing queries need to filter by layer and trigger policy *and* shrink
  the search bound as closer hits arrive, without allocating a delegate per call. Upstream's
  `RayCast` / `SweepCast` / `FindNearest` take `Func` pre/post filters; a layer mask therefore
  captured a closure, and a `maxDistance` argument still walked the unbounded overload and discarded
  far hits afterwards. Closest-hit shape casts additionally allocated a list, collected every hit,
  then scanned it.
- **Shape of change**: additive. New `IRayHitHandler` / `ISweepHitHandler` / `INearestHitHandler`
  interfaces (AllowTest + OnHit with `ref Real` bound). Generic
  `RayCast<THandler>` / `SweepCast<T,THandler>` / `FindNearest<T,THandler>` specialise the traversal
  for a struct handler. The original delegate overloads remain and now forward through a private
  adapter struct, so there is one copy of each walk. A closest-hit handler that writes the hit
  lambda back into the bound prunes the rest of the tree the same way the old "keep the best"
  path did.
- **Defaults**: unchanged. Callers that still pass delegates get identical closest-hit behaviour.
- **Upstreamable**: yes; this is how BEPU's `IRayHitHandler` is shaped, and it is generally useful.
- **Tests**: vendored `Tests/` still exercise the delegate overloads. Engine coverage in
  `XEngine.Runtime.Test/Physics/PhysicsQueryTests.cs`.

### SpringSettings

- **Files**: `Dynamics/Constraints/SpringSettings.cs` (new), `SoftBodies/SpringConstraint.cs`
- **Motive**: joints want a frequency / damping-ratio parameterization (Unity, BEPU) but every Jitter2
  constraint already solves with softness and bias. Duplicating the conversion on each joint would
  drift; the conversion already existed on `SpringConstraint.SetSpringParameters`.
- **Shape of change**: additive. `SpringSettings.Compute(effectiveMass, dt, out softness, out bias)`
  is the extracted formula. Frequency 0 means rigid and returns false so callers keep their defaults.
  `SetSpringParameters` now forwards through it. Existing Softness/Bias setters are unchanged.
- **Defaults**: unchanged. A constraint nobody springs still uses `DefaultLinearSoftness` /
  `DefaultLinearBias`.
- **Upstreamable**: yes.
- **Tests**: `PhysicsJointAndCharacterTests.SpringSettings_Compute_MatchesTheMassAwareFormula`.

