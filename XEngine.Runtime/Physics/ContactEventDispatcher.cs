// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Jitter2;
using Jitter2.Collision.Shapes;
using Jitter2.Dynamics;
using Jitter2.LinearMath;

using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Turns the physics step's overlap and contact state into Unity-style <c>OnTrigger*</c> and
/// <c>OnCollision*</c> callbacks. Runs once per step, on the main thread, after
/// <see cref="World.Step(float, bool)"/> has finished.
/// </summary>
/// <remarks>
/// Trigger transitions come straight from the kernel, which tracks sensor overlaps natively (see
/// <c>ThirdParty/Jitter2/PATCHES.md</c>). Collisions are tracked here instead, because "touching" is
/// finer-grained than an arbiter's lifetime: an arbiter can exist with no live contact slots while the
/// shapes sit within the speculative margin, which is not a collision as far as gameplay is concerned.
/// The freshness bookkeeping mirrors the kernel's, so both event kinds behave the same around sleep.
/// </remarks>
internal sealed class ContactEventDispatcher
{
    /// <summary>An unordered pair of shape ids, ordered so lookup does not depend on argument order.</summary>
    private readonly struct PairKey : System.IEquatable<PairKey>
    {
        private readonly ulong _a;
        private readonly ulong _b;

        public PairKey(ulong a, ulong b)
        {
            if (b < a) (a, b) = (b, a);
            _a = a;
            _b = b;
        }

        public bool Equals(PairKey other) => _a == other._a && _b == other._b;
        public override bool Equals(object? obj) => obj is PairKey other && Equals(other);
        public override int GetHashCode() => System.HashCode.Combine(_a, _b);
    }

    /// <summary>A colliding pair we have already reported an enter for.</summary>
    private struct TrackedCollision
    {
        public Collider A;
        public Collider B;
        public bool Fresh;
    }

    /// <summary>
    /// Bodies worth scanning for contacts, ref-counted by the number of event-reporting colliders
    /// attached to each. Static colliders share one body per layer, so counting lets a body drop out
    /// only once its last listening collider is gone.
    /// </summary>
    private readonly Dictionary<RigidBody, int> _bodies = new();

    private readonly Dictionary<PairKey, TrackedCollision> _collisions = new();
    private readonly List<PairKey> _staleScratch = new();
    private readonly List<RigidBody> _bodyScratch = new();

    /// <summary>
    /// Reused across every callback. Unity does the same, which is why its <c>Collision</c> is
    /// documented as invalid outside the callback that received it.
    /// </summary>
    private readonly Collision _collision = new();

    /// <summary>
    /// Starts scanning <paramref name="body"/> for collision events. Called when a collider that
    /// implements <c>OnCollision*</c> attaches.
    /// </summary>
    public void AddBody(RigidBody body)
    {
        if (body == null) return;
        _bodies[body] = _bodies.TryGetValue(body, out int count) ? count + 1 : 1;
    }

    /// <summary>
    /// Stops scanning <paramref name="body"/> once its last event-reporting collider detaches.
    /// </summary>
    public void RemoveBody(RigidBody body)
    {
        if (body == null) return;
        if (!_bodies.TryGetValue(body, out int count)) return;

        if (count <= 1) _bodies.Remove(body);
        else _bodies[body] = count - 1;
    }

    public void Clear()
    {
        _bodies.Clear();
        _collisions.Clear();
    }

    /// <summary>
    /// Dispatches every trigger and collision callback for the step that just finished.
    /// </summary>
    public void Dispatch(World world)
    {
        DispatchTriggers(world);
        DispatchCollisions();
    }

    // ---- Triggers -------------------------------------------------------------------------------

    private void DispatchTriggers(World world)
    {
        foreach (SensorOverlap overlap in world.SensorOverlapsEnded)
        {
            if (!Resolve(overlap.ShapeA, overlap.ShapeB, out Collider a, out Collider b)) continue;
            SendTrigger(PhysicsEventPhase.Exit, a, b);
        }

        foreach (SensorOverlap overlap in world.SensorOverlapsBegan)
        {
            if (!Resolve(overlap.ShapeA, overlap.ShapeB, out Collider a, out Collider b)) continue;
            SendTrigger(PhysicsEventPhase.Enter, a, b);
        }

        // Unity raises Stay on the entering step too, so this deliberately includes pairs that just
        // began.
        foreach (SensorOverlap overlap in world.SensorOverlaps)
        {
            if (!Resolve(overlap.ShapeA, overlap.ShapeB, out Collider a, out Collider b)) continue;
            SendTrigger(PhysicsEventPhase.Stay, a, b);
        }
    }

    /// <summary>
    /// Sends a trigger callback to both sides of the pair, as Unity does: a script can sit on either
    /// the trigger volume or the collider passing through it.
    /// </summary>
    private static void SendTrigger(PhysicsEventPhase phase, Collider a, Collider b)
    {
        DispatchTriggerTo(a, phase, b);
        DispatchTriggerTo(b, phase, a);
    }

    private static void DispatchTriggerTo(Collider receiver, PhysicsEventPhase phase, Collider other)
    {
        GameObject target = receiver.GameObject;
        if (target == null) return;

        PhysicsCallbacks wanted = phase switch
        {
            PhysicsEventPhase.Enter => PhysicsCallbacks.TriggerEnter,
            PhysicsEventPhase.Stay => PhysicsCallbacks.TriggerStay,
            _ => PhysicsCallbacks.TriggerExit,
        };

        Invoke(target, wanted, phase, other, null);

        // A collider on a child of the rigidbody is part of that rigidbody's compound shape, so the
        // script driving it usually lives on the rigidbody's GameObject.
        GameObject rigidbodyOwner = receiver.AttachedRigidbody?.GameObject;
        if (rigidbodyOwner != null && rigidbodyOwner != target)
            Invoke(rigidbodyOwner, wanted, phase, other, null);
    }

    // ---- Collisions -----------------------------------------------------------------------------

    private void DispatchCollisions()
    {
        MarkAllStale();
        ScanContacts();
        ReportSeparations();
    }

    private void MarkAllStale()
    {
        if (_collisions.Count == 0) return;

        _staleScratch.Clear();
        foreach (PairKey key in _collisions.Keys)
            _staleScratch.Add(key);

        foreach (PairKey key in _staleScratch)
        {
            TrackedCollision tracked = _collisions[key];
            tracked.Fresh = false;
            _collisions[key] = tracked;
        }
    }

    private void ScanContacts()
    {
        if (_bodies.Count == 0) return;

        // Copy the keys: a callback may destroy a GameObject, which detaches colliders and mutates
        // the body set mid-iteration.
        _bodyScratch.Clear();
        foreach (RigidBody body in _bodies.Keys)
            _bodyScratch.Add(body);

        foreach (RigidBody body in _bodyScratch)
        {
            if (body.Handle.IsZero) continue;

            foreach (Arbiter arbiter in body.Contacts)
                ProcessArbiter(arbiter);
        }
    }

    private void ProcessArbiter(Arbiter arbiter)
    {
        RigidBodyShape shape1 = arbiter.Shape1;
        RigidBodyShape shape2 = arbiter.Shape2;
        if (shape1 == null || shape2 == null) return;

        ref ContactData data = ref arbiter.Handle.Data;

        // A slot in UsageMask that is not in MaskContactAll was solved but has since broken, so the
        // shapes are no longer touching.
        if ((data.UsageMask & ContactData.MaskContactAll) == 0) return;

        if (!Resolve(shape1, shape2, out Collider a, out Collider b)) return;

        var key = new PairKey(shape1.ShapeId, shape2.ShapeId);
        bool known = _collisions.TryGetValue(key, out TrackedCollision existing);

        // When both bodies of a pair are listening, the arbiter is reached from either end. Everything
        // was marked stale before the scan, so a fresh entry can only mean we already handled it.
        if (known && existing.Fresh) return;

        _collisions[key] = new TrackedCollision { A = a, B = b, Fresh = true };

        if (!known) SendCollision(PhysicsEventPhase.Enter, a, b, arbiter, ref data);
        SendCollision(PhysicsEventPhase.Stay, a, b, arbiter, ref data);
    }

    private void ReportSeparations()
    {
        if (_collisions.Count == 0) return;

        _staleScratch.Clear();
        foreach (KeyValuePair<PairKey, TrackedCollision> entry in _collisions)
        {
            if (!entry.Value.Fresh) _staleScratch.Add(entry.Key);
        }

        foreach (PairKey key in _staleScratch)
        {
            TrackedCollision tracked = _collisions[key];
            _collisions.Remove(key);

            if (IsAlive(tracked.A) && IsAlive(tracked.B))
                SendCollisionExit(tracked.A, tracked.B);
        }
    }

    private void SendCollision(PhysicsEventPhase phase, Collider a, Collider b, Arbiter arbiter,
        ref ContactData data)
    {
        DispatchCollisionTo(a, phase, b, arbiter, ref data, receiverIsSecondShape: false);
        DispatchCollisionTo(b, phase, a, arbiter, ref data, receiverIsSecondShape: true);
    }

    private void SendCollisionExit(Collider a, Collider b)
    {
        _collision.Reset(b, b.AttachedRigidbody, Float3.Zero, Float3.Zero);
        DispatchCollisionObject(a, PhysicsEventPhase.Exit, _collision);

        _collision.Reset(a, a.AttachedRigidbody, Float3.Zero, Float3.Zero);
        DispatchCollisionObject(b, PhysicsEventPhase.Exit, _collision);
    }

    /// <summary>
    /// Fills the pooled <see cref="Collision"/> from the receiver's point of view and hands it to the
    /// receiver's listeners.
    /// </summary>
    /// <param name="receiverIsSecondShape">
    /// True when the receiver is the arbiter's second shape. The narrow phase reports one normal for
    /// the pair, pointing from the first shape towards the second; Unity's convention is that the
    /// normal points at whichever object is being called, so one of the two sides is mirrored.
    /// </param>
    private void DispatchCollisionTo(Collider receiver, PhysicsEventPhase phase, Collider other,
        Arbiter arbiter, ref ContactData data, bool receiverIsSecondShape)
    {
        if (!HasListener(receiver, phase, trigger: false)) return;

        RigidBody body1 = arbiter.Body1;
        RigidBody body2 = arbiter.Body2;

        JVector relativeVelocity = receiverIsSecondShape
            ? body1.Velocity - body2.Velocity
            : body2.Velocity - body1.Velocity;

        JVector impulse = JVector.Zero;
        uint usage = data.UsageMask;
        _collision.Reset(other, other.AttachedRigidbody, ToFloat3(relativeVelocity), Float3.Zero);

        if ((usage & ContactData.MaskContact0) != 0)
            AddContact(ref data.Contact0, body1, body2, receiver, other, receiverIsSecondShape, ref impulse);
        if ((usage & ContactData.MaskContact1) != 0)
            AddContact(ref data.Contact1, body1, body2, receiver, other, receiverIsSecondShape, ref impulse);
        if ((usage & ContactData.MaskContact2) != 0)
            AddContact(ref data.Contact2, body1, body2, receiver, other, receiverIsSecondShape, ref impulse);
        if ((usage & ContactData.MaskContact3) != 0)
            AddContact(ref data.Contact3, body1, body2, receiver, other, receiverIsSecondShape, ref impulse);

        _collision.SetImpulse(ToFloat3(impulse));
        DispatchCollisionObject(receiver, phase, _collision);
    }

    private void AddContact(ref ContactData.Contact contact,
        RigidBody body1, RigidBody body2, Collider receiver, Collider other, bool receiverIsSecondShape,
        ref JVector impulse)
    {
        JVector p1 = body1.Position + contact.RelativePosition1;
        JVector p2 = body2.Position + contact.RelativePosition2;
        JVector normal = contact.Normal;

        // The narrow phase normal points from the first shape towards the second, and the solver pushes
        // the first shape along its negation. Both already point at the receiver when the receiver is
        // the second shape; the first shape's side is mirrored.
        float penetration = JVector.Dot(normal, p1 - p2);
        JVector contactImpulse = normal * contact.Impulse
                                 + contact.Tangent1 * contact.TangentImpulse1
                                 + contact.Tangent2 * contact.TangentImpulse2;

        if (!receiverIsSecondShape)
        {
            normal = -normal;
            contactImpulse = -contactImpulse;
        }

        impulse += contactImpulse;

        // The contact point on the receiver's own surface, and Unity's separation, which is negative
        // while the shapes overlap.
        _collision.AddContact(new ContactPoint(
            ToFloat3(receiverIsSecondShape ? p2 : p1), ToFloat3(normal), -penetration, receiver, other));
    }

    private static void DispatchCollisionObject(Collider receiver, PhysicsEventPhase phase, Collision collision)
    {
        GameObject target = receiver.GameObject;
        if (target == null) return;

        PhysicsCallbacks wanted = phase switch
        {
            PhysicsEventPhase.Enter => PhysicsCallbacks.CollisionEnter,
            PhysicsEventPhase.Stay => PhysicsCallbacks.CollisionStay,
            _ => PhysicsCallbacks.CollisionExit,
        };

        Invoke(target, wanted, phase, null, collision);

        GameObject rigidbodyOwner = receiver.AttachedRigidbody?.GameObject;
        if (rigidbodyOwner != null && rigidbodyOwner != target)
            Invoke(rigidbodyOwner, wanted, phase, null, collision);
    }

    // ---- Shared -----------------------------------------------------------------------------

    private static void Invoke(GameObject target, PhysicsCallbacks wanted, PhysicsEventPhase phase,
        Collider other, Collision collision)
    {
        // Snapshot the count: a callback may add a component, and the loop must not see it this step.
        List<MonoBehaviour> components = target._components;
        int count = components.Count;
        for (int i = 0; i < count && i < components.Count; i++)
        {
            MonoBehaviour component = components[i];
            if (component == null || component.IsDisposed || !component.EnabledInHierarchy) continue;
            if ((SceneComponentRegistry.GetPhysicsCallbacks(component) & wanted) == 0) continue;

            if (collision != null) component.InternalOnCollision(phase, collision);
            else component.InternalOnTrigger(phase, other);
        }
    }

    /// <summary>
    /// Whether anything on the receiver's side implements the callback about to be sent. Checked
    /// before the contact geometry is assembled, so a pair with no listener costs almost nothing.
    /// </summary>
    private static bool HasListener(Collider receiver, PhysicsEventPhase phase, bool trigger)
    {
        PhysicsCallbacks wanted = (trigger, phase) switch
        {
            (true, PhysicsEventPhase.Enter) => PhysicsCallbacks.TriggerEnter,
            (true, PhysicsEventPhase.Stay) => PhysicsCallbacks.TriggerStay,
            (true, _) => PhysicsCallbacks.TriggerExit,
            (false, PhysicsEventPhase.Enter) => PhysicsCallbacks.CollisionEnter,
            (false, PhysicsEventPhase.Stay) => PhysicsCallbacks.CollisionStay,
            (false, _) => PhysicsCallbacks.CollisionExit,
        };

        return HasListener(receiver.GameObject, wanted)
               || HasListener(receiver.AttachedRigidbody?.GameObject, wanted);
    }

    private static bool HasListener(GameObject target, PhysicsCallbacks wanted)
    {
        if (target == null) return false;

        List<MonoBehaviour> components = target._components;
        for (int i = 0; i < components.Count; i++)
        {
            MonoBehaviour component = components[i];
            if (component == null || component.IsDisposed || !component.EnabledInHierarchy) continue;
            if ((SceneComponentRegistry.GetPhysicsCallbacks(component) & wanted) != 0) return true;
        }

        return false;
    }

    private static bool Resolve(RigidBodyShape shapeA, RigidBodyShape shapeB, out Collider a, out Collider b)
    {
        a = ColliderBinding.GetCollider(shapeA);
        b = ColliderBinding.GetCollider(shapeB);
        return IsAlive(a) && IsAlive(b);
    }

    private static bool IsAlive(Collider collider)
        => collider != null && !collider.IsDisposed && collider.GameObject != null;

    private static Float3 ToFloat3(in JVector v) => new(v.X, v.Y, v.Z);
}
