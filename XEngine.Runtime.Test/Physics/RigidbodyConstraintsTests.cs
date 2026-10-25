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
/// Tests for frozen degrees of freedom: that a locked axis holds against gravity, contacts and
/// directly assigned velocity, that the axes left free are untouched, and that locking a rotation
/// axis is reflected in the inertia the solver prepares against.
/// </summary>
public class RigidbodyConstraintsTests : RuntimeTestBase
{
    private const float Dt = 1f / 60f;

    // ---- Kernel: bitfield encoding ---------------------------------------------------------------

    [Fact]
    public void LockedAxes_RoundTripThroughTheSpareFlagBits()
    {
        for (int bits = 0; bits <= (int)AxisLock.All; bits++)
        {
            var data = new RigidBodyData { LockedAxes = (AxisLock)bits };
            Assert.Equal((AxisLock)bits, data.LockedAxes);
        }
    }

    [Fact]
    public void LockedAxes_DoNotDisturbTheOtherFlagBits()
    {
        // The locks share Flags with motion type, the active bit and the gyroscopic bit, so the
        // packing has to be shown not to collide with any of them.
        var data = new RigidBodyData
        {
            MotionType = MotionType.Kinematic,
            IsActive = true,
            EnableGyroscopicForces = true,
        };

        data.LockedAxes = AxisLock.All;

        Assert.Equal(MotionType.Kinematic, data.MotionType);
        Assert.True(data.IsActive);
        Assert.True(data.EnableGyroscopicForces);
        Assert.Equal(AxisLock.All, data.LockedAxes);

        data.LockedAxes = AxisLock.None;

        Assert.Equal(MotionType.Kinematic, data.MotionType);
        Assert.True(data.IsActive);
        Assert.True(data.EnableGyroscopicForces);
    }

    // ---- Kernel: inertia projection ---------------------------------------------------------------

    private static World CreateWorld()
    {
        var world = new World();
        world.Gravity = JVector.Zero;
        return world;
    }

    private static RigidBody CreateBox(World world, float size = 1f)
    {
        RigidBody body = world.CreateRigidBody();
        body.AddShape(new BoxShape(size, size, size));
        return body;
    }

    [Fact]
    public void LockingOneRotationAxis_ZeroesThatRowAndColumnOfTheInverseInertia()
    {
        World world = CreateWorld();
        RigidBody body = CreateBox(world);

        body.LockedAxes = AxisLock.AngularY;
        JMatrix inertia = body.Data.InverseInertiaWorld;

        // Row and column both, so the tensor stays symmetric and a torque about any axis produces no
        // component about the locked one.
        Assert.Equal(0, (double)inertia.M21, 6);
        Assert.Equal(0, (double)inertia.M22, 6);
        Assert.Equal(0, (double)inertia.M23, 6);
        Assert.Equal(0, (double)inertia.M12, 6);
        Assert.Equal(0, (double)inertia.M32, 6);

        // The axes left free keep their inertia.
        Assert.True(inertia.M11 > 0);
        Assert.True(inertia.M33 > 0);
    }

    [Fact]
    public void LockingAllRotation_LeavesTheInverseInertiaAtZero()
    {
        World world = CreateWorld();
        RigidBody body = CreateBox(world);

        body.LockedAxes = AxisLock.Angular;

        // Zero inverse inertia is how the solver spells infinite inertia, which is exactly what a
        // fully rotation-locked body is.
        Assert.Equal(JMatrix.Zero, body.Data.InverseInertiaWorld);
    }

    [Fact]
    public void ClearingARotationLock_RestoresTheInertia()
    {
        World world = CreateWorld();
        RigidBody body = CreateBox(world);
        JMatrix free = body.Data.InverseInertiaWorld;

        body.LockedAxes = AxisLock.Angular;
        body.LockedAxes = AxisLock.None;

        Assert.Equal(free, body.Data.InverseInertiaWorld);
    }

    [Fact]
    public void LockedRotationAxis_SwallowsTorqueEntirely()
    {
        World world = CreateWorld();
        RigidBody body = CreateBox(world);
        body.LockedAxes = AxisLock.AngularY;

        body.Torque = new JVector(0, 50, 0);
        world.Step(Dt, false);

        // Torque reaches velocity through the inverse inertia, so the projection alone accounts for
        // this: the impulse is never generated, rather than being generated and then discarded.
        Assert.Equal(0, (double)body.AngularVelocity.Y, 6);
    }

    [Fact]
    public void LockingRotation_DoesNotAffectLinearResponseToForce()
    {
        static float PushBox(AxisLock locked)
        {
            var world = new World { Gravity = JVector.Zero };
            RigidBody body = world.CreateRigidBody();
            body.AddShape(new BoxShape(1, 1, 1));
            body.LockedAxes = locked;
            body.Damping = (0f, 0f);

            body.AddForce(new JVector(10, 0, 0));
            world.Step(Dt, false);

            return body.Velocity.X;
        }

        // The inertia projection must not leak into the linear solve: a rotation lock changes how the
        // body spins, never how a central force accelerates it.
        Assert.Equal((double)PushBox(AxisLock.None), (double)PushBox(AxisLock.Angular), 6);
    }

    // ---- Engine: freezing position ----------------------------------------------------------------

    private Scene CreatePhysicsScene()
    {
        var scene = CreateScene(enable: true);
        scene.Physics.UseMultithreading = false;
        return scene;
    }

    private (Rigidbody3D Rb, BoxCollider Collider) AddBox(Scene scene, Float3 position, string name = "Box")
    {
        var go = CreateGameObject(name);
        go.Transform.Position = position;
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        BoxCollider collider = go.AddComponent<BoxCollider>();
        scene.Add(go);
        return (rb, collider);
    }

    private void AddStaticFloor(Scene scene)
    {
        var go = CreateGameObject("Floor");
        go.Transform.Position = new Float3(0, -0.5f, 0);
        BoxCollider collider = go.AddComponent<BoxCollider>();
        collider.Size = new Float3(40, 1, 40);
        scene.Add(go);
    }

    [Fact]
    public void FreezePositionY_HoldsHeightUnderGravity()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.Gravity = new Float3(0, -9.81f, 0);

        (Rigidbody3D rb, _) = AddBox(scene, new Float3(0, 5, 0));
        rb.Constraints = RigidbodyConstraints.FreezePositionY;

        Tick(scene, 120);

        // Two seconds of unopposed gravity would be about 20 units of fall. Gravity still lands on
        // the velocity every substep; the lock clears it before it can reach the pose.
        Assert.Equal(5f, rb.Transform.Position.Y, 4);
    }

    [Fact]
    public void FreezePositionY_LeavesTheOtherAxesFree()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.Gravity = Float3.Zero;

        (Rigidbody3D rb, _) = AddBox(scene, new Float3(0, 5, 0));
        rb.Constraints = RigidbodyConstraints.FreezePositionY;
        rb.LinearVelocity = new Float3(3, 4, 0);

        Tick(scene, 30);

        Assert.True(rb.Transform.Position.X > 1.0f, $"X should have advanced, was {rb.Transform.Position.X}");
        Assert.Equal(5f, rb.Transform.Position.Y, 4);
    }

    [Fact]
    public void FrozenAxis_ReadsBackAsZeroVelocity()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.Gravity = new Float3(0, -9.81f, 0);

        (Rigidbody3D rb, _) = AddBox(scene, new Float3(0, 5, 0));
        rb.Constraints = RigidbodyConstraints.FreezePositionY;

        StepPhysics(scene, 10);

        // A caller reading velocity between steps must not see the gravity or relaxation residue that
        // accumulated on an axis the body cannot move along.
        Assert.Equal(0f, rb.LinearVelocity.Y, 5);
    }

    [Fact]
    public void FreezePositionY_ResistsAContactPush()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.Gravity = new Float3(0, -9.81f, 0);
        AddStaticFloor(scene);

        // Frozen box on the floor, heavy box dropped onto it. Gravity alone is a soft test — a
        // contact impulse is what would actually break a lock applied at the wrong point in the step.
        (Rigidbody3D frozen, _) = AddBox(scene, new Float3(0, 0.5f, 0), "Frozen");
        frozen.Constraints = RigidbodyConstraints.FreezePositionY;

        (Rigidbody3D heavy, _) = AddBox(scene, new Float3(0, 3f, 0), "Heavy");
        heavy.Mass = 200f;

        Tick(scene, 120);

        Assert.Equal(0.5f, frozen.Transform.Position.Y, 3);
    }

    [Fact]
    public void FreezeAll_KeepsAPoseThroughAViolentImpact()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.Gravity = new Float3(0, -9.81f, 0);

        (Rigidbody3D frozen, _) = AddBox(scene, new Float3(0, 1f, 0), "Frozen");
        frozen.Constraints = RigidbodyConstraints.FreezeAll;

        (Rigidbody3D bullet, _) = AddBox(scene, new Float3(-4f, 1f, 0), "Bullet");
        bullet.AffectedByGravity = false;
        bullet.Mass = 50f;
        bullet.LinearVelocity = new Float3(40, 0, 0);

        Tick(scene, 60);

        Assert.Equal(0f, frozen.Transform.Position.X, 3);
        Assert.Equal(1f, frozen.Transform.Position.Y, 3);
        Assert.Equal(0f, frozen.Transform.Position.Z, 3);
    }

    // ---- Engine: freezing rotation ----------------------------------------------------------------

    [Fact]
    public void FreezeRotation_SurvivesAnAngularImpulse()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.Gravity = Float3.Zero;

        (Rigidbody3D rb, _) = AddBox(scene, Float3.Zero);
        rb.Constraints = RigidbodyConstraints.FreezeRotation;

        rb.ApplyAngularImpulse(new Float3(12, 12, 12));
        Tick(scene, 60);

        Assert.Equal(0f, rb.AngularVelocity.X, 5);
        Assert.Equal(0f, rb.AngularVelocity.Y, 5);
        Assert.Equal(0f, rb.AngularVelocity.Z, 5);

        Quaternion rotation = rb.Transform.Rotation;
        Assert.Equal(Quaternion.Identity.X, rotation.X, 4);
        Assert.Equal(Quaternion.Identity.Y, rotation.Y, 4);
        Assert.Equal(Quaternion.Identity.Z, rotation.Z, 4);
        Assert.Equal(1f, MathF.Abs(rotation.W), 4);
    }

    [Fact]
    public void FreezeRotationX_LeavesTheOtherSpinAxesFree()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.Gravity = Float3.Zero;

        (Rigidbody3D rb, _) = AddBox(scene, Float3.Zero);
        rb.Constraints = RigidbodyConstraints.FreezeRotationX;

        rb.ApplyAngularImpulse(new Float3(8, 8, 0));
        StepPhysics(scene, 5);

        Assert.Equal(0f, rb.AngularVelocity.X, 5);
        Assert.True(MathF.Abs(rb.AngularVelocity.Y) > 0.1f,
            $"Y spin should survive, was {rb.AngularVelocity.Y}");
    }

    [Fact]
    public void FreezeRotation_StopsATumbleFromAnOffCentreImpact()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.Gravity = Float3.Zero;

        (Rigidbody3D rb, _) = AddBox(scene, Float3.Zero);
        rb.Constraints = RigidbodyConstraints.FreezeRotation;

        // Off-centre so the contact-style impulse carries torque; this is the case a velocity-only
        // lock and an inertia projection would both have to handle.
        rb.ApplyImpulse(new Float3(0, 0, 20), new Float3(0.5f, 0.5f, -0.5f));
        Tick(scene, 30);

        Assert.Equal(0f, rb.AngularVelocity.X, 5);
        Assert.Equal(0f, rb.AngularVelocity.Y, 5);
        Assert.Equal(0f, rb.AngularVelocity.Z, 5);
        Assert.True(rb.Transform.Position.Z > 0.1f, "the linear part of the impulse should still land");
    }

    // ---- Engine: authoring surface -----------------------------------------------------------------

    [Fact]
    public void Constraints_MatchUnitysBitValues()
    {
        // Kept identical so authored masks and inspector bitfields carry over. Unity leaves bit 0
        // unused, which is why FreezePositionX is 2 rather than 1.
        Assert.Equal(0, (int)RigidbodyConstraints.None);
        Assert.Equal(2, (int)RigidbodyConstraints.FreezePositionX);
        Assert.Equal(4, (int)RigidbodyConstraints.FreezePositionY);
        Assert.Equal(8, (int)RigidbodyConstraints.FreezePositionZ);
        Assert.Equal(16, (int)RigidbodyConstraints.FreezeRotationX);
        Assert.Equal(32, (int)RigidbodyConstraints.FreezeRotationY);
        Assert.Equal(64, (int)RigidbodyConstraints.FreezeRotationZ);
        Assert.Equal(14, (int)RigidbodyConstraints.FreezePosition);
        Assert.Equal(112, (int)RigidbodyConstraints.FreezeRotation);
        Assert.Equal(126, (int)RigidbodyConstraints.FreezeAll);
    }

    [Theory]
    [InlineData(RigidbodyConstraints.FreezePositionX, AxisLock.LinearX)]
    [InlineData(RigidbodyConstraints.FreezePositionY, AxisLock.LinearY)]
    [InlineData(RigidbodyConstraints.FreezePositionZ, AxisLock.LinearZ)]
    [InlineData(RigidbodyConstraints.FreezeRotationX, AxisLock.AngularX)]
    [InlineData(RigidbodyConstraints.FreezeRotationY, AxisLock.AngularY)]
    [InlineData(RigidbodyConstraints.FreezeRotationZ, AxisLock.AngularZ)]
    [InlineData(RigidbodyConstraints.FreezePosition, AxisLock.Linear)]
    [InlineData(RigidbodyConstraints.FreezeRotation, AxisLock.Angular)]
    [InlineData(RigidbodyConstraints.FreezeAll, AxisLock.All)]
    public void Constraints_MapOntoTheKernelLocks(RigidbodyConstraints constraints, AxisLock expected)
    {
        Scene scene = CreatePhysicsScene();
        (Rigidbody3D rb, _) = AddBox(scene, Float3.Zero);

        rb.Constraints = constraints;

        Assert.Equal(expected, rb._body.LockedAxes);
    }

    [Fact]
    public void Constraints_CanBeLiftedAtRuntime()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.Gravity = new Float3(0, -9.81f, 0);

        (Rigidbody3D rb, _) = AddBox(scene, new Float3(0, 5, 0));
        rb.Constraints = RigidbodyConstraints.FreezePositionY;
        Tick(scene, 30);
        Assert.Equal(5f, rb.Transform.Position.Y, 4);

        rb.Constraints = RigidbodyConstraints.None;
        Tick(scene, 60);

        Assert.True(rb.Transform.Position.Y < 4.5f,
            $"the body should fall once unfrozen, was at {rb.Transform.Position.Y}");
    }

    [Fact]
    public void Constraints_SurviveOnValidate()
    {
        Scene scene = CreatePhysicsScene();
        (Rigidbody3D rb, _) = AddBox(scene, Float3.Zero);
        rb.Constraints = RigidbodyConstraints.FreezeAll;

        // OnValidate rebuilds the body's properties from the serialized fields, so anything it forgets
        // to push silently reverts the moment a field is edited in the inspector.
        rb.OnValidate();

        Assert.Equal(AxisLock.All, rb._body.LockedAxes);
    }

    [Fact]
    public void FrozenBody_IsStillAllowedToSleep()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.Gravity = new Float3(0, -9.81f, 0);

        (Rigidbody3D rb, _) = AddBox(scene, new Float3(0, 5, 0));
        rb.Constraints = RigidbodyConstraints.FreezeAll;

        // Gravity keeps writing velocity that the lock keeps clearing. If the sleep test ran before
        // the lock it would read that residue and hold the body awake forever.
        StepPhysics(scene, 180);

        Assert.False(rb.IsActive);
    }
}
