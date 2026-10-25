// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Tests for the Unity-style physics callbacks: OnTriggerEnter/Stay/Exit and
/// OnCollisionEnter/Stay/Exit. The kernel-level overlap detection these build on is covered
/// separately by <see cref="JitterSensorTests"/>; these are about the engine-side dispatch —
/// which components get called, what the arguments say, and when.
/// </summary>
public class PhysicsCallbackTests : RuntimeTestBase
{
    /// <summary>Records every callback it receives, in order.</summary>
    private sealed class CallbackProbe : MonoBehaviour
    {
        public readonly List<string> Events = [];
        public readonly List<Collider> TriggerOthers = [];

        // Copied out per callback, never held as a reference: Collision is pooled and reused, so the
        // instance a probe kept would show whatever the last dispatch of the step put in it.
        public int LastContactCount;
        public Float3 LastNormal;
        public float LastSeparation;
        public Collider LastCollisionCollider;
        public Rigidbody3D LastOtherRigidbody;
        public bool LastOtherRigidbodyWasNull;

        public override void OnTriggerEnter(Collider other)
        {
            Events.Add("TriggerEnter");
            TriggerOthers.Add(other);
        }

        public override void OnTriggerStay(Collider other) => Events.Add("TriggerStay");

        public override void OnTriggerExit(Collider other)
        {
            Events.Add("TriggerExit");
            TriggerOthers.Add(other);
        }

        public override void OnCollisionEnter(Collision collision)
        {
            Events.Add("CollisionEnter");
            Capture(collision);
        }

        public override void OnCollisionStay(Collision collision)
        {
            Events.Add("CollisionStay");
            Capture(collision);
        }

        public override void OnCollisionExit(Collision collision)
        {
            Events.Add("CollisionExit");
            LastCollisionCollider = collision.Collider;
        }

        private void Capture(Collision collision)
        {
            LastCollisionCollider = collision.Collider;
            LastOtherRigidbody = collision.Rigidbody;
            LastOtherRigidbodyWasNull = collision.Rigidbody == null;
            LastContactCount = collision.ContactCount;
            if (collision.ContactCount > 0)
            {
                ContactPoint contact = collision.GetContact(0);
                LastNormal = contact.Normal;
                LastSeparation = contact.Separation;
            }
        }

        public int Count(string name)
        {
            int n = 0;
            foreach (string e in Events)
                if (e == name) n++;
            return n;
        }
    }

    /// <summary>Implements no physics callbacks, so nothing should be tracked on its behalf.</summary>
    private sealed class SilentProbe : MonoBehaviour
    {
        public int UpdateCount;
        public override void Update() => UpdateCount++;
    }

    private Scene CreatePhysicsScene()
    {
        var scene = CreateScene(enable: true);
        scene.Physics.UseMultithreading = false; // deterministic stepping
        return scene;
    }

    /// <summary>A static (rigidbody-less) box collider, optionally a trigger.</summary>
    private (GameObject Go, BoxCollider Collider, CallbackProbe Probe) AddStaticBox(
        Scene scene, Float3 position, Float3 size, bool trigger = false, bool withProbe = true)
    {
        var go = CreateGameObject("Static");
        go.Transform.Position = position;
        CallbackProbe probe = withProbe ? go.AddComponent<CallbackProbe>() : null;
        BoxCollider collider = go.AddComponent<BoxCollider>();
        collider.Size = size;
        collider.IsTrigger = trigger;
        scene.Add(go);
        return (go, collider, probe);
    }

    private (Rigidbody3D Rb, BoxCollider Collider, CallbackProbe Probe) AddDynamicBox(
        Scene scene, Float3 position, bool gravity = false, bool trigger = false, bool withProbe = true)
    {
        var go = CreateGameObject("Dynamic");
        go.Transform.Position = position;
        CallbackProbe probe = withProbe ? go.AddComponent<CallbackProbe>() : null;
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        rb.AffectedByGravity = gravity;
        BoxCollider collider = go.AddComponent<BoxCollider>();
        collider.IsTrigger = trigger;
        scene.Add(go);
        return (rb, collider, probe);
    }

    // ---- Triggers -------------------------------------------------------------------------------

    [Fact]
    public void TriggerEnter_FiresOnceOnBothSides()
    {
        Scene scene = CreatePhysicsScene();
        (_, BoxCollider triggerCollider, CallbackProbe triggerProbe) =
            AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), trigger: true);
        (_, BoxCollider bodyCollider, CallbackProbe bodyProbe) = AddDynamicBox(scene, Float3.Zero);

        StepPhysics(scene);

        Assert.Equal(1, triggerProbe.Count("TriggerEnter"));
        Assert.Equal(1, bodyProbe.Count("TriggerEnter"));

        // Each side is told about the other, not about itself.
        Assert.Same(bodyCollider, triggerProbe.TriggerOthers[0]);
        Assert.Same(triggerCollider, bodyProbe.TriggerOthers[0]);

        // Staying overlapped does not repeat the enter.
        StepPhysics(scene, 3);
        Assert.Equal(1, triggerProbe.Count("TriggerEnter"));
    }

    [Fact]
    public void TriggerStay_FiresEveryStepIncludingTheFirst()
    {
        Scene scene = CreatePhysicsScene();
        (_, _, CallbackProbe triggerProbe) = AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), trigger: true);
        AddDynamicBox(scene, Float3.Zero);

        StepPhysics(scene, 4);

        Assert.Equal(4, triggerProbe.Count("TriggerStay"));
        Assert.Equal(1, triggerProbe.Count("TriggerEnter"));
    }

    [Fact]
    public void TriggerExit_FiresWhenLeaving()
    {
        Scene scene = CreatePhysicsScene();
        (_, BoxCollider triggerCollider, CallbackProbe triggerProbe) =
            AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), trigger: true);
        (Rigidbody3D rb, _, CallbackProbe bodyProbe) = AddDynamicBox(scene, Float3.Zero);

        StepPhysics(scene);
        Assert.Equal(0, triggerProbe.Count("TriggerExit"));

        rb.Transform.Position = new Float3(50, 0, 0);
        StepPhysics(scene);

        Assert.Equal(1, triggerProbe.Count("TriggerExit"));
        Assert.Equal(1, bodyProbe.Count("TriggerExit"));
        Assert.Same(triggerCollider, bodyProbe.TriggerOthers[^1]);

        // Exit is reported once, not on every subsequent step.
        StepPhysics(scene, 3);
        Assert.Equal(1, triggerProbe.Count("TriggerExit"));
    }

    /// <summary>
    /// The event ordering a script depends on: enter before stay, and exit before anything else in
    /// the step it happens.
    /// </summary>
    [Fact]
    public void TriggerEvents_AreOrderedEnterThenStay()
    {
        Scene scene = CreatePhysicsScene();
        (_, _, CallbackProbe triggerProbe) = AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), trigger: true);
        AddDynamicBox(scene, Float3.Zero);

        StepPhysics(scene);

        Assert.Equal(["TriggerEnter", "TriggerStay"], triggerProbe.Events);
    }

    [Fact]
    public void Trigger_GeneratesNoCollisionCallbacks()
    {
        Scene scene = CreatePhysicsScene();
        (_, _, CallbackProbe triggerProbe) = AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), trigger: true);
        (_, _, CallbackProbe bodyProbe) = AddDynamicBox(scene, Float3.Zero);

        StepPhysics(scene, 5);

        Assert.Equal(0, triggerProbe.Count("CollisionEnter"));
        Assert.Equal(0, bodyProbe.Count("CollisionEnter"));
    }

    /// <summary>
    /// Toggling IsTrigger at runtime must switch the behaviour without a re-attach.
    /// </summary>
    [Fact]
    public void MakingAColliderATrigger_StartsReportingOverlaps()
    {
        Scene scene = CreatePhysicsScene();
        (_, BoxCollider collider, CallbackProbe probe) = AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2));
        AddDynamicBox(scene, Float3.Zero);

        StepPhysics(scene);
        Assert.Equal(0, probe.Count("TriggerEnter"));

        collider.IsTrigger = true;
        StepPhysics(scene);

        Assert.Equal(1, probe.Count("TriggerEnter"));
    }

    /// <summary>
    /// Overlap tracking is opt-in: with no listener anywhere, the kernel is told not to track the
    /// pair at all.
    /// </summary>
    [Fact]
    public void TriggerWithNoListener_IsNotTracked()
    {
        Scene scene = CreatePhysicsScene();
        var go = CreateGameObject("SilentTrigger");
        go.AddComponent<SilentProbe>();
        BoxCollider collider = go.AddComponent<BoxCollider>();
        collider.Size = new Float3(2, 2, 2);
        collider.IsTrigger = true;
        scene.Add(go);

        AddDynamicBox(scene, Float3.Zero, withProbe: false);

        StepPhysics(scene, 3);

        Assert.Empty(scene.Physics.World.SensorOverlaps);
    }

    /// <summary>
    /// A collider on a child of the rigidbody is part of that rigidbody's compound shape, so a script
    /// on the rigidbody's GameObject must still hear about it.
    /// </summary>
    [Fact]
    public void Trigger_ReachesListenerOnTheRigidbodyGameObject()
    {
        Scene scene = CreatePhysicsScene();
        AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), trigger: true, withProbe: false);

        var parent = CreateGameObject("Body");
        CallbackProbe probe = parent.AddComponent<CallbackProbe>();
        parent.AddComponent<Rigidbody3D>().AffectedByGravity = false;

        var child = CreateGameObject("ChildCollider");
        child.AddComponent<BoxCollider>();
        child.Transform.SetParent(parent.Transform);

        scene.Add(parent);

        StepPhysics(scene);

        Assert.Equal(1, probe.Count("TriggerEnter"));
    }

    // ---- Collisions -----------------------------------------------------------------------------

    [Fact]
    public void CollisionEnter_FiresOnceOnBothSides()
    {
        Scene scene = CreatePhysicsScene();
        (_, BoxCollider groundCollider, CallbackProbe groundProbe) =
            AddStaticBox(scene, new Float3(0, -1, 0), new Float3(10, 1, 10));
        (_, BoxCollider boxCollider, CallbackProbe boxProbe) =
            AddDynamicBox(scene, new Float3(0, 0.2f, 0), gravity: true);

        StepPhysics(scene, 20);

        Assert.Equal(1, groundProbe.Count("CollisionEnter"));
        Assert.Equal(1, boxProbe.Count("CollisionEnter"));
        Assert.Same(boxCollider, groundProbe.LastCollisionCollider);
        Assert.Same(groundCollider, boxProbe.LastCollisionCollider);
    }

    [Fact]
    public void CollisionStay_FiresWhileResting()
    {
        Scene scene = CreatePhysicsScene();
        AddStaticBox(scene, new Float3(0, -1, 0), new Float3(10, 1, 10));
        (_, _, CallbackProbe boxProbe) = AddDynamicBox(scene, new Float3(0, 0.2f, 0), gravity: true);

        // The box starts clear of the ground, so the first steps are the fall.
        StepPhysics(scene, 30);

        Assert.True(boxProbe.Count("CollisionStay") > 1,
            $"resting box should keep reporting stay, got {boxProbe.Count("CollisionStay")}");
    }

    [Fact]
    public void CollisionExit_FiresWhenSeparating()
    {
        Scene scene = CreatePhysicsScene();
        (_, BoxCollider groundCollider, _) = AddStaticBox(scene, new Float3(0, -1, 0), new Float3(10, 1, 10));
        (Rigidbody3D rb, _, CallbackProbe boxProbe) = AddDynamicBox(scene, new Float3(0, 0.2f, 0), gravity: true);

        StepPhysics(scene, 20);
        Assert.Equal(1, boxProbe.Count("CollisionEnter"));

        rb.Transform.Position = new Float3(0, 50, 0);
        StepPhysics(scene, 2);

        Assert.Equal(1, boxProbe.Count("CollisionExit"));
        Assert.Same(groundCollider, boxProbe.LastCollisionCollider);
    }

    /// <summary>
    /// Unity's contact normal points from the other collider towards the one being called, and its
    /// separation is negative while the shapes overlap. Both are per-receiver, so the two sides of one
    /// collision see opposite normals.
    /// </summary>
    [Fact]
    public void Collision_ContactNormalPointsAtTheReceiver()
    {
        Scene scene = CreatePhysicsScene();
        (_, _, CallbackProbe groundProbe) = AddStaticBox(scene, new Float3(0, -1, 0), new Float3(10, 1, 10));
        (_, _, CallbackProbe boxProbe) = AddDynamicBox(scene, new Float3(0, 0.2f, 0), gravity: true);

        StepPhysics(scene, 20);

        Assert.True(boxProbe.LastContactCount > 0, "the resting box should have contact points");

        // The box sits above the ground, so its normal points up and the ground's points down.
        Assert.True(boxProbe.LastNormal.Y > 0.5f, $"box normal should point up, was {boxProbe.LastNormal}");
        Assert.True(groundProbe.LastNormal.Y < -0.5f, $"ground normal should point down, was {groundProbe.LastNormal}");
        Assert.True(boxProbe.LastSeparation <= 0.01f, $"separation should be at or below zero, was {boxProbe.LastSeparation}");
    }

    [Fact]
    public void Collision_ReportsTheRigidbodyOfTheOtherCollider()
    {
        Scene scene = CreatePhysicsScene();
        (_, _, CallbackProbe groundProbe) = AddStaticBox(scene, new Float3(0, -1, 0), new Float3(10, 1, 10));
        (Rigidbody3D rb, _, CallbackProbe boxProbe) = AddDynamicBox(scene, new Float3(0, 0.2f, 0), gravity: true);

        StepPhysics(scene, 20);

        Assert.Same(rb, groundProbe.LastOtherRigidbody);
        Assert.True(boxProbe.LastOtherRigidbodyWasNull, "the static ground has no rigidbody to report");
    }

    /// <summary>
    /// The pooled Collision is only valid inside the callback that received it: the next dispatch
    /// overwrites it. Worth pinning down, because a script that stores one would otherwise read
    /// plausible-looking data belonging to a different pair.
    /// </summary>
    [Fact]
    public void Collision_IsPooledAcrossCallbacks()
    {
        Scene scene = CreatePhysicsScene();
        AddStaticBox(scene, new Float3(0, -1, 0), new Float3(10, 1, 10), withProbe: false);
        var go = CreateGameObject("Box");
        go.Transform.Position = new Float3(0, 0.2f, 0);
        var captor = go.AddComponent<CollisionCaptor>();
        go.AddComponent<Rigidbody3D>().AffectedByGravity = true;
        go.AddComponent<BoxCollider>();
        scene.Add(go);

        StepPhysics(scene, 20);

        Assert.NotNull(captor.Held);
        Assert.Same(captor.Held, captor.HeldAgain);
    }

    /// <summary>Holds on to the Collision object it is given, to observe that it is reused.</summary>
    private sealed class CollisionCaptor : MonoBehaviour
    {
        public Collision Held;
        public Collision HeldAgain;

        public override void OnCollisionStay(Collision collision)
        {
            if (Held == null) Held = collision;
            else HeldAgain = collision;
        }
    }

    [Fact]
    public void CollisionEvents_AreOrderedEnterThenStay()
    {
        Scene scene = CreatePhysicsScene();
        AddStaticBox(scene, new Float3(0, -1, 0), new Float3(10, 1, 10), withProbe: false);
        (_, _, CallbackProbe boxProbe) = AddDynamicBox(scene, new Float3(0, 0.2f, 0), gravity: true);

        StepPhysics(scene, 20);

        Assert.Equal("CollisionEnter", boxProbe.Events[0]);
        Assert.Equal("CollisionStay", boxProbe.Events[1]);
    }

    /// <summary>
    /// A destroyed GameObject must not produce callbacks naming a dead collider.
    /// </summary>
    [Fact]
    public void DestroyingACollider_DoesNotDispatchStaleColliders()
    {
        Scene scene = CreatePhysicsScene();
        (GameObject triggerGo, _, _) = AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), trigger: true, withProbe: false);
        (_, _, CallbackProbe bodyProbe) = AddDynamicBox(scene, Float3.Zero);

        StepPhysics(scene);
        Assert.Equal(1, bodyProbe.Count("TriggerEnter"));

        triggerGo.Enabled = false;
        StepPhysics(scene, 2);

        foreach (Collider other in bodyProbe.TriggerOthers)
            Assert.False(other.IsDisposed, "a disposed collider should never be handed to a callback");
    }

    /// <summary>
    /// Callbacks are gameplay, so they stay quiet in edit mode like Update and Start do.
    /// </summary>
    [Fact]
    public void NotPlaying_CallbacksDoNotFire()
    {
        Application.IsPlaying = false;

        Scene scene = CreatePhysicsScene();
        (_, _, CallbackProbe triggerProbe) = AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), trigger: true);
        AddDynamicBox(scene, Float3.Zero);

        StepPhysics(scene, 3);

        Assert.Empty(triggerProbe.Events);
    }

    [Fact]
    public void DisabledComponent_DoesNotReceiveCallbacks()
    {
        Scene scene = CreatePhysicsScene();
        (_, _, CallbackProbe triggerProbe) = AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), trigger: true);
        AddDynamicBox(scene, Float3.Zero);
        triggerProbe.Enabled = false;

        StepPhysics(scene, 3);

        Assert.Empty(triggerProbe.Events);
    }

    /// <summary>
    /// A body that falls asleep inside a static trigger must keep receiving Stay and must not get Exit.
    /// The kernel freshness pre-pass is what keeps the overlap alive; this asserts the engine dispatch
    /// follows it.
    /// </summary>
    [Fact]
    public void TriggerExit_DoesNotFireWhileSleepingInsideStaticTrigger()
    {
        Scene scene = CreatePhysicsScene();
        (_, _, CallbackProbe triggerProbe) =
            AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), trigger: true);
        (Rigidbody3D rb, _, _) = AddDynamicBox(scene, Float3.Zero);

        StepPhysics(scene);
        Assert.Equal(1, triggerProbe.Count("TriggerEnter"));

        rb.Sleep();
        StepPhysics(scene, 8);

        Assert.True(rb.IsSleeping());
        Assert.Equal(0, triggerProbe.Count("TriggerExit"));
        Assert.True(triggerProbe.Count("TriggerStay") > 1);
    }

    /// <summary>
    /// Moving the static trigger away from a sleeping body still ends the overlap.
    /// Static colliders pick up Transform edits in <c>Update</c>, so that has to run before the step.
    /// </summary>
    [Fact]
    public void TriggerExit_FiresWhenStaticTriggerMovesAwayFromSleepingBody()
    {
        Scene scene = CreatePhysicsScene();
        (GameObject triggerGo, _, CallbackProbe triggerProbe) =
            AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), trigger: true);
        (Rigidbody3D rb, _, CallbackProbe bodyProbe) = AddDynamicBox(scene, Float3.Zero);

        StepPhysics(scene);
        rb.Sleep();
        StepPhysics(scene, 8);
        Assert.True(rb.IsSleeping());
        Assert.Equal(0, triggerProbe.Count("TriggerExit"));

        triggerGo.Transform.Position = new Float3(50, 0, 0);
        scene.Update();
        StepPhysics(scene);

        Assert.Equal(1, triggerProbe.Count("TriggerExit"));
        Assert.Equal(1, bodyProbe.Count("TriggerExit"));
    }

    /// <summary>
    /// Two sleeping dynamics — a trigger and a solid — must not report Exit just because the
    /// narrow phase skipped the pair.
    /// </summary>
    [Fact]
    public void TriggerExit_DoesNotFireWhenBothBodiesSleep()
    {
        Scene scene = CreatePhysicsScene();
        (Rigidbody3D triggerRb, _, CallbackProbe triggerProbe) =
            AddDynamicBox(scene, Float3.Zero, trigger: true);
        (Rigidbody3D body, _, _) = AddDynamicBox(scene, Float3.Zero);

        StepPhysics(scene);
        Assert.Equal(1, triggerProbe.Count("TriggerEnter"));

        triggerRb.Sleep();
        body.Sleep();
        StepPhysics(scene, 8);

        Assert.True(triggerRb.IsSleeping());
        Assert.True(body.IsSleeping());
        Assert.Equal(0, triggerProbe.Count("TriggerExit"));
        Assert.True(triggerProbe.Count("TriggerStay") > 1);
    }

    /// <summary>
    /// Removing the trigger shape (disabling the collider) reports Exit to the remaining listener.
    /// </summary>
    [Fact]
    public void TriggerExit_FiresWhenTriggerColliderIsDisabled()
    {
        Scene scene = CreatePhysicsScene();
        (_, BoxCollider triggerCollider, _) =
            AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), trigger: true);
        (_, _, CallbackProbe bodyProbe) = AddDynamicBox(scene, Float3.Zero);

        StepPhysics(scene);
        Assert.Equal(1, bodyProbe.Count("TriggerEnter"));

        triggerCollider.Enabled = false;
        StepPhysics(scene);

        Assert.Equal(1, bodyProbe.Count("TriggerExit"));
        StepPhysics(scene, 2);
        Assert.Equal(1, bodyProbe.Count("TriggerExit"));
    }

    // ---- Collider surface -----------------------------------------------------------------------

    [Fact]
    public void Collider_AttachedRigidbody_IsSetForDynamicAndNullForStatic()
    {
        Scene scene = CreatePhysicsScene();
        (_, BoxCollider staticCollider, _) = AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), withProbe: false);
        (Rigidbody3D rb, BoxCollider dynamicCollider, _) =
            AddDynamicBox(scene, new Float3(20, 0, 0), withProbe: false);

        StepPhysics(scene);

        Assert.Same(rb, dynamicCollider.AttachedRigidbody);
        Assert.Null(staticCollider.AttachedRigidbody);
    }

    [Fact]
    public void Collider_Bounds_EncloseTheCollider()
    {
        Scene scene = CreatePhysicsScene();
        (_, BoxCollider collider, _) =
            AddStaticBox(scene, new Float3(5, 0, 0), new Float3(2, 2, 2), withProbe: false);

        StepPhysics(scene);

        AABB bounds = collider.Bounds;
        Assert.True(bounds.Min.X <= 4.1f, $"min should reach the box's left face, was {bounds.Min.X}");
        Assert.True(bounds.Max.X >= 5.9f, $"max should reach the box's right face, was {bounds.Max.X}");
    }

    [Fact]
    public void RaycastHit_ReportsTheColliderForStaticGeometry()
    {
        Scene scene = CreatePhysicsScene();
        (_, BoxCollider collider, _) =
            AddStaticBox(scene, Float3.Zero, new Float3(2, 2, 2), withProbe: false);

        StepPhysics(scene);

        Assert.True(scene.Physics.Raycast(new Float3(0, 10, 0), new Float3(0, -1, 0), out RaycastHit hit));
        Assert.Same(collider, hit.Collider);
        Assert.Null(hit.Rigidbody); // static, so there is no rigidbody to report
    }
}
