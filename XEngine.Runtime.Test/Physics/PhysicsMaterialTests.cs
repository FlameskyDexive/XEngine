// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Jitter2;
using Jitter2.Collision.Shapes;
using Jitter2.Dynamics;
using Jitter2.LinearMath;

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Tests for per-shape physics materials: how two colliders' coefficients combine, how a material
/// reaches the solver, and the bounce threshold that keeps restitution from preventing rest.
/// </summary>
public class PhysicsMaterialTests : RuntimeTestBase
{
    private const float Dt = 1f / 60f;

    // ---- Combine resolution ---------------------------------------------------------------------

    [Theory]
    [InlineData(MaterialCombine.Average, 0.6f)]
    [InlineData(MaterialCombine.Minimum, 0.2f)]
    [InlineData(MaterialCombine.Multiply, 0.2f)]
    [InlineData(MaterialCombine.Maximum, 1.0f)]
    public void Resolve_AppliesTheRequestedMode(MaterialCombine mode, float expected)
    {
        Assert.Equal(expected, MaterialCombineExtensions.Resolve(0.2f, mode, 1.0f, mode), 4);
    }

    [Fact]
    public void Resolve_IsSymmetricInOperandOrder()
    {
        foreach (MaterialCombine mode in Enum.GetValues<MaterialCombine>())
        {
            // Multiply, min, max and average are all commutative, so a pair must resolve the same
            // regardless of which shape the narrow phase happened to visit first.
            Assert.Equal(
                MaterialCombineExtensions.Resolve(0.3f, mode, 0.8f, mode),
                MaterialCombineExtensions.Resolve(0.8f, mode, 0.3f, mode),
                5);
        }
    }

    [Fact]
    public void Resolve_LetsTheMoreDominantModeWin()
    {
        // Ordinal precedence: Maximum > Multiply > Minimum > Average. Whichever side asks for the
        // higher mode decides, so the outcome does not depend on authoring order.
        Assert.Equal(1.0f, MaterialCombineExtensions.Resolve(
            0.2f, MaterialCombine.Average, 1.0f, MaterialCombine.Maximum), 4);

        Assert.Equal(0.2f, MaterialCombineExtensions.Resolve(
            0.2f, MaterialCombine.Minimum, 1.0f, MaterialCombine.Average), 4);

        Assert.Equal(0.2f, MaterialCombineExtensions.Resolve(
            0.2f, MaterialCombine.Multiply, 1.0f, MaterialCombine.Minimum), 4);
    }

    [Fact]
    public void Reconcile_AbstainingSideDefersToTheOther()
    {
        // A shape with no material must not vote. If it defaulted to a concrete mode it would either
        // override every material (Maximum, the dominant ordinal) or change how material-less bodies
        // behave (anything else).
        Assert.Equal(MaterialCombine.Minimum, MaterialCombineExtensions.Reconcile(null, MaterialCombine.Minimum));
        Assert.Equal(MaterialCombine.Average, MaterialCombineExtensions.Reconcile(MaterialCombine.Average, null));

        // Both abstaining is upstream Jitter2's max().
        Assert.Equal(MaterialCombine.Maximum, MaterialCombineExtensions.Reconcile(null, null));
    }

    [Fact]
    public void Resolve_AbstainingSideDoesNotForceMaximum()
    {
        // The regression this guards: with the abstaining side pinned to Maximum, a material asking
        // for Minimum would be ignored and the pair would resolve to 1.0.
        Assert.Equal(0.2f, MaterialCombineExtensions.Resolve(
            0.2f, MaterialCombine.Minimum, 1.0f, null), 4);
    }

    // ---- Kernel: shape materials versus body materials ------------------------------------------

    private static (World World, RigidBodyShape ShapeA, RigidBodyShape ShapeB) CreateTouchingPair()
    {
        var world = new World();
        world.Gravity = JVector.Zero;

        RigidBody floor = world.CreateRigidBody();
        var floorShape = new BoxShape(20, 1, 20);
        floor.AddShape(floorShape);
        floor.Position = new JVector(0, -0.5f, 0);
        floor.MotionType = MotionType.Static;

        RigidBody box = world.CreateRigidBody();
        var boxShape = new BoxShape(1, 1, 1);
        box.AddShape(boxShape);
        box.Position = new JVector(0, 0.499f, 0);

        // Settle so the pair has a real arbiter with contacts.
        for (int i = 0; i < 4; i++) world.Step(Dt, false);

        return (world, floorShape, boxShape);
    }

    private static ContactData ContactOf(RigidBody body)
    {
        foreach (Arbiter arbiter in body.Contacts)
            return arbiter.Handle.Data;

        throw new InvalidOperationException("body has no contacts");
    }

    [Fact]
    public void UnsetShapeMaterials_FallBackToTheBodies()
    {
        (World world, RigidBodyShape floor, RigidBodyShape box) = CreateTouchingPair();

        floor.RigidBody.Friction = 0.9f;
        box.RigidBody.Friction = 0.1f;
        floor.RigidBody.Restitution = 0.4f;
        box.RigidBody.Restitution = 0.0f;

        // Force a fresh resolve now that the body values are set.
        world.RefreshMaterial(box);

        ContactData data = ContactOf(box.RigidBody);

        // Defaults are null coefficients with Maximum combine, which is exactly upstream's max().
        Assert.Equal(0.9f, data.Friction, 4);
        Assert.Equal(0.4f, data.Restitution, 4);
    }

    [Fact]
    public void ShapeMaterial_OverridesTheBody()
    {
        (World world, RigidBodyShape floor, RigidBodyShape box) = CreateTouchingPair();

        floor.RigidBody.Friction = 0.9f;
        box.RigidBody.Friction = 0.9f;

        box.Friction = 0.05f;
        box.FrictionCombine = MaterialCombine.Minimum;
        world.RefreshMaterial(box);

        // The shape's 0.05 replaces its body's 0.9, and the floor abstains, so Minimum applies.
        Assert.Equal(0.05f, ContactOf(box.RigidBody).Friction, 4);
    }

    [Fact]
    public void ShapesOnOneBody_CanCarryDifferentMaterials()
    {
        var world = new World();
        world.Gravity = JVector.Zero;

        RigidBody floor = world.CreateRigidBody();
        var floorShape = new BoxShape(20, 1, 20);
        floor.AddShape(floorShape);
        floor.Position = new JVector(0, -0.5f, 0);
        floor.MotionType = MotionType.Static;

        // One body, two shapes side by side, each with its own surface. This is the case the
        // body-level coefficient cannot express.
        RigidBody body = world.CreateRigidBody();
        var slippery = new TransformedShape(new BoxShape(1, 1, 1), new JVector(-1, 0, 0));
        var grippy = new TransformedShape(new BoxShape(1, 1, 1), new JVector(1, 0, 0));
        body.AddShape(slippery);
        body.AddShape(grippy);
        body.Position = new JVector(0, 0.499f, 0);

        slippery.Friction = 0.01f;
        slippery.FrictionCombine = MaterialCombine.Minimum;
        grippy.Friction = 2.0f;
        grippy.FrictionCombine = MaterialCombine.Maximum;

        for (int i = 0; i < 4; i++) world.Step(Dt, false);

        float slipperyFriction = float.NaN;
        float grippyFriction = float.NaN;

        foreach (Arbiter arbiter in body.Contacts)
        {
            if (arbiter.Shape1 == slippery || arbiter.Shape2 == slippery)
                slipperyFriction = arbiter.Handle.Data.Friction;
            if (arbiter.Shape1 == grippy || arbiter.Shape2 == grippy)
                grippyFriction = arbiter.Handle.Data.Friction;
        }

        Assert.Equal(0.01f, slipperyFriction, 4);
        Assert.Equal(2.0f, grippyFriction, 4);
    }

    [Fact]
    public void RefreshMaterial_UpdatesAnExistingContactWithoutLosingImpulses()
    {
        (World world, _, RigidBodyShape box) = CreateTouchingPair();

        float impulseBefore = 0f;
        foreach (Arbiter arbiter in box.RigidBody.Contacts)
            impulseBefore = arbiter.Handle.Data.Contact0.Impulse;

        box.Friction = 1.5f;
        box.FrictionCombine = MaterialCombine.Maximum;
        world.RefreshMaterial(box);

        ContactData data = ContactOf(box.RigidBody);

        Assert.Equal(1.5f, data.Friction, 4);

        // The point of re-resolving in place rather than tearing the arbiter down: warm-starting
        // survives, so a material change mid-contact does not pop.
        Assert.Equal(impulseBefore, data.Contact0.Impulse, 4);
    }

    [Fact]
    public void RefreshMaterial_LeavesOtherPairsAlone()
    {
        var world = new World();
        world.Gravity = JVector.Zero;

        RigidBody floor = world.CreateRigidBody();
        var floorShape = new BoxShape(20, 1, 20);
        floor.AddShape(floorShape);
        floor.Position = new JVector(0, -0.5f, 0);
        floor.MotionType = MotionType.Static;

        RigidBody a = world.CreateRigidBody();
        var aShape = new BoxShape(1, 1, 1);
        a.AddShape(aShape);
        a.Position = new JVector(-3, 0.499f, 0);

        RigidBody b = world.CreateRigidBody();
        var bShape = new BoxShape(1, 1, 1);
        b.AddShape(bShape);
        b.Position = new JVector(3, 0.499f, 0);

        for (int i = 0; i < 4; i++) world.Step(Dt, false);

        aShape.Friction = 1.9f;
        aShape.FrictionCombine = MaterialCombine.Maximum;
        world.RefreshMaterial(aShape);

        Assert.Equal(1.9f, ContactOf(a).Friction, 4);

        // b never asked for a material, so it keeps resolving from its body.
        Assert.Equal(MathF.Max(b.Friction, floor.Friction), ContactOf(b).Friction, 4);
    }

    [Fact]
    public void ShapeMaterial_RejectsOutOfRangeValues()
    {
        var shape = new BoxShape(1, 1, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => shape.Friction = -0.1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => shape.Restitution = -0.1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => shape.Restitution = 1.1f);

        // Clearing back to "inherit" is always allowed.
        shape.Friction = null;
        shape.Restitution = null;
        Assert.Null(shape.Friction);
        Assert.Null(shape.Restitution);
    }

    // ---- Bounce threshold -----------------------------------------------------------------------

    /// <summary>
    /// Drops a fully elastic box onto a floor and reports the upward speed it leaves with.
    /// </summary>
    private static float BounceSpeed(float threshold, float impactSpeed)
    {
        var world = new World();
        world.Gravity = JVector.Zero;
        world.RestitutionThreshold = threshold;

        RigidBody floor = world.CreateRigidBody();
        floor.AddShape(new BoxShape(20, 1, 20));
        floor.Position = new JVector(0, -0.5f, 0);
        floor.MotionType = MotionType.Static;
        floor.Restitution = 1.0f;

        RigidBody box = world.CreateRigidBody();
        box.AddShape(new BoxShape(1, 1, 1));
        box.Position = new JVector(0, 0.52f, 0);
        box.Restitution = 1.0f;
        box.Damping = (0f, 0f);
        box.Velocity = new JVector(0, -impactSpeed, 0);

        for (int i = 0; i < 12; i++) world.Step(Dt, false);

        return box.Velocity.Y;
    }

    [Fact]
    public void ImpactAboveTheThreshold_Bounces()
    {
        // 5 m/s into a threshold of 1: restitution applies and the box comes back up.
        Assert.True(BounceSpeed(threshold: 1f, impactSpeed: 5f) > 1f);
    }

    [Fact]
    public void ImpactBelowTheThreshold_DoesNotBounce()
    {
        // Same impact, but the threshold is now above it, so restitution is skipped. This is what
        // stops a bouncy body from jittering forever on the residual approach speed of each step.
        Assert.True(BounceSpeed(threshold: 8f, impactSpeed: 5f) < 0.5f);
    }

    [Fact]
    public void WorldRestitutionThreshold_RejectsNegativeValues()
    {
        var world = new World();
        Assert.Throws<ArgumentOutOfRangeException>(() => world.RestitutionThreshold = -1f);
    }

    // ---- Engine side: PhysicsMaterial asset and Collider.SharedMaterial -------------------------

    [Fact]
    public void PhysicsMaterial_ClampsItsCoefficients()
    {
        var material = ScriptableObject.CreateInstance<PhysicsMaterial>();

        material.Friction = -5f;
        Assert.Equal(0f, material.Friction);

        material.Bounciness = 3f;
        Assert.Equal(1f, material.Bounciness);

        material.Bounciness = -1f;
        Assert.Equal(0f, material.Bounciness);

        // Friction above 1 is legitimate — it models adhesion — so it is not clamped at the top.
        material.Friction = 4f;
        Assert.Equal(4f, material.Friction);
    }

    [Fact]
    public void PhysicsMaterial_DefaultsToUnityCombineMode()
    {
        var material = ScriptableObject.CreateInstance<PhysicsMaterial>();

        // Unity's default is Average. The kernel's per-shape default is Maximum, which reproduces
        // upstream Jitter2; assigning a material is what opts into Unity's behaviour.
        Assert.Equal(PhysicsMaterialCombine.Average, material.FrictionCombine);
        Assert.Equal(PhysicsMaterialCombine.Average, material.BounceCombine);
    }

    private Scene CreatePhysicsScene()
    {
        var scene = CreateScene(enable: true);
        scene.Physics.UseMultithreading = false;
        return scene;
    }

    private (Rigidbody3D Rb, BoxCollider Collider) AddDynamicBox(Scene scene, Float3 position)
    {
        var go = CreateGameObject("Dynamic");
        go.Transform.Position = position;
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        rb.AffectedByGravity = false;
        BoxCollider collider = go.AddComponent<BoxCollider>();
        scene.Add(go);
        return (rb, collider);
    }

    private BoxCollider AddStaticFloor(Scene scene)
    {
        var go = CreateGameObject("Floor");
        go.Transform.Position = new Float3(0, -0.5f, 0);
        BoxCollider collider = go.AddComponent<BoxCollider>();
        collider.Size = new Float3(20, 1, 20);
        scene.Add(go);
        return collider;
    }

    /// <summary>The friction the solver resolved for the first contact of this collider's body.</summary>
    private static float ResolvedFriction(Rigidbody3D rb)
    {
        foreach (Arbiter arbiter in rb._body.Contacts)
            return arbiter.Handle.Data.Friction;

        throw new InvalidOperationException("body has no contacts");
    }

    [Fact]
    public void SharedMaterial_ReachesTheSolver()
    {
        Scene scene = CreatePhysicsScene();
        AddStaticFloor(scene);
        (Rigidbody3D rb, BoxCollider collider) = AddDynamicBox(scene, new Float3(0, 0.499f, 0));

        // Maximum against the floor's inherited body default of 0.2, so the resolved value is the
        // material's own and cannot be confused with a fallback.
        var material = ScriptableObject.CreateInstance<PhysicsMaterial>();
        material.Friction = 0.77f;
        material.FrictionCombine = PhysicsMaterialCombine.Maximum;
        collider.SharedMaterial = material;

        StepPhysics(scene, 4);

        Assert.Same(material, collider.SharedMaterial);
        Assert.Equal(0.77f, ResolvedFriction(rb), 4);
    }

    [Fact]
    public void ChangingSharedMaterialMidContact_TakesEffectImmediately()
    {
        Scene scene = CreatePhysicsScene();
        AddStaticFloor(scene);
        (Rigidbody3D rb, BoxCollider collider) = AddDynamicBox(scene, new Float3(0, 0.499f, 0));

        var slippery = ScriptableObject.CreateInstance<PhysicsMaterial>();
        slippery.Friction = 0.02f;
        slippery.FrictionCombine = PhysicsMaterialCombine.Minimum;
        collider.SharedMaterial = slippery;

        StepPhysics(scene, 4);
        Assert.Equal(0.02f, ResolvedFriction(rb), 4);

        var grippy = ScriptableObject.CreateInstance<PhysicsMaterial>();
        grippy.Friction = 1.75f;
        grippy.FrictionCombine = PhysicsMaterialCombine.Maximum;
        collider.SharedMaterial = grippy;

        // No step in between: the setter re-resolves the contacts that already exist, rather than
        // waiting for the pair to separate and be rebuilt.
        Assert.Equal(1.75f, ResolvedFriction(rb), 4);
    }

    [Fact]
    public void ClearingSharedMaterial_FallsBackToTheBody()
    {
        Scene scene = CreatePhysicsScene();
        AddStaticFloor(scene);
        (Rigidbody3D rb, BoxCollider collider) = AddDynamicBox(scene, new Float3(0, 0.499f, 0));

        var material = ScriptableObject.CreateInstance<PhysicsMaterial>();
        material.Friction = 0.02f;
        material.FrictionCombine = PhysicsMaterialCombine.Minimum;
        collider.SharedMaterial = material;

        StepPhysics(scene, 4);
        Assert.Equal(0.02f, ResolvedFriction(rb), 4);

        collider.SharedMaterial = null;

        // Back to max of the two bodies, the pre-material behaviour.
        Assert.Equal(MathF.Max(rb._body.Friction, 0.2f), ResolvedFriction(rb), 4);
    }

    [Fact]
    public void DefaultMaterial_AppliesToCollidersWithoutOne()
    {
        Scene scene = CreatePhysicsScene();

        var fallback = ScriptableObject.CreateInstance<PhysicsMaterial>();
        fallback.Friction = 0.42f;
        fallback.FrictionCombine = PhysicsMaterialCombine.Maximum;
        scene.Physics.DefaultMaterial = fallback;

        AddStaticFloor(scene);
        (Rigidbody3D rb, _) = AddDynamicBox(scene, new Float3(0, 0.499f, 0));

        StepPhysics(scene, 4);

        Assert.Equal(0.42f, ResolvedFriction(rb), 4);
    }

    [Fact]
    public void SharedMaterial_BeatsTheProjectDefault()
    {
        Scene scene = CreatePhysicsScene();

        // Both sides average, so the resolved value is the mean and shows plainly which coefficient
        // each collider contributed.
        var fallback = ScriptableObject.CreateInstance<PhysicsMaterial>();
        fallback.Friction = 1.0f;
        fallback.FrictionCombine = PhysicsMaterialCombine.Average;
        scene.Physics.DefaultMaterial = fallback;

        AddStaticFloor(scene);
        (Rigidbody3D rb, BoxCollider collider) = AddDynamicBox(scene, new Float3(0, 0.499f, 0));

        var own = ScriptableObject.CreateInstance<PhysicsMaterial>();
        own.Friction = 0.2f;
        own.FrictionCombine = PhysicsMaterialCombine.Average;
        collider.SharedMaterial = own;

        StepPhysics(scene, 4);

        // The floor takes the project default (1.0); the box uses its own (0.2) rather than the
        // default. Mean 0.6 — not 1.0, which is what two defaults would have given.
        Assert.Equal(0.6f, ResolvedFriction(rb), 4);
    }

    [Fact]
    public void LowFrictionSlidesFurtherThanHighFriction()
    {
        static float SlideDistance(PhysicsMaterialTests test, float friction)
        {
            Scene scene = test.CreatePhysicsScene();
            scene.Physics.Gravity = new Float3(0, -9.81f, 0);
            test.AddStaticFloor(scene);

            var go = test.CreateGameObject("Slider");
            go.Transform.Position = new Float3(0, 0.499f, 0);
            Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
            BoxCollider collider = go.AddComponent<BoxCollider>();

            var material = ScriptableObject.CreateInstance<PhysicsMaterial>();
            material.Friction = friction;
            material.FrictionCombine = PhysicsMaterialCombine.Minimum;
            collider.SharedMaterial = material;

            scene.Add(go);
            // Tick rather than StepPhysics: the body -> Transform readback happens in Update, so
            // stepping physics alone would leave Transform.Position at its authored value.
            test.Tick(scene, 2);

            rb.LinearVelocity = new Float3(6, 0, 0);
            test.Tick(scene, 90);

            return go.Transform.Position.X;
        }

        float onIce = SlideDistance(this, 0.001f);
        float onRubber = SlideDistance(this, 1.5f);

        // The end-to-end check that friction is not merely stored: the same push travels further on
        // the slipperier surface.
        Assert.True(onIce > onRubber + 1f,
            $"expected ice to outrun rubber by >1m, got ice={onIce:F2} rubber={onRubber:F2}");
    }

    [Fact]
    public void BounceThreshold_ReachesTheKernel()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.BounceThreshold = 3.5f;

        StepPhysics(scene);

        Assert.Equal(3.5f, scene.Physics.World.RestitutionThreshold, 4);
    }

    // ---- Queries and triggers -------------------------------------------------------------------

    [Fact]
    public void QueriesHitTriggers_ControlsWhetherRaycastsSeeTriggers()
    {
        Scene scene = CreatePhysicsScene();

        var go = CreateGameObject("Trigger");
        go.Transform.Position = new Float3(0, 0, 5);
        BoxCollider collider = go.AddComponent<BoxCollider>();
        collider.Size = new Float3(2, 2, 2);
        collider.IsTrigger = true;
        scene.Add(go);

        StepPhysics(scene);

        scene.Physics.QueriesHitTriggers = true;
        Assert.True(scene.Physics.Raycast(Float3.Zero, new Float3(0, 0, 1), 20f, out RaycastHit hit));
        Assert.Same(collider, hit.Collider);

        scene.Physics.QueriesHitTriggers = false;
        Assert.False(scene.Physics.Raycast(Float3.Zero, new Float3(0, 0, 1), 20f, out _));
    }

    [Fact]
    public void QueriesHitTriggers_StillReportsNonTriggers()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.QueriesHitTriggers = false;

        var go = CreateGameObject("Solid");
        go.Transform.Position = new Float3(0, 0, 5);
        BoxCollider collider = go.AddComponent<BoxCollider>();
        collider.Size = new Float3(2, 2, 2);
        scene.Add(go);

        StepPhysics(scene);

        Assert.True(scene.Physics.Raycast(Float3.Zero, new Float3(0, 0, 1), 20f, out RaycastHit hit));
        Assert.Same(collider, hit.Collider);
    }
}
