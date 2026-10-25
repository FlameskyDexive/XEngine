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
/// Tests for continuous collision detection: which pairs get a swept test under each mode, that a
/// swept pair actually stops a body that would otherwise tunnel, and the per-collider threshold that
/// decides when sweeping is worth it.
/// </summary>
public class CollisionDetectionModeTests : RuntimeTestBase
{
    private const float Dt = 1f / 60f;

    // A body this fast crosses 1.67 units per step, so it passes clean through a 0.1-thick wall with
    // no overlap at either end of the step for a discrete narrow phase to find.
    private const float TunnellingSpeed = 100f;

    // ---- Kernel: does the pair tunnel? -----------------------------------------------------------

    /// <summary>
    /// Fires a small fast box at a thin wall and reports where it ended up. Positive means it got
    /// through.
    /// </summary>
    private static float FireAtWall(
        ContinuousDetectionMode projectileMode,
        MotionType wallMotion = MotionType.Static,
        ContinuousDetectionMode wallMode = ContinuousDetectionMode.Discrete,
        float? wallSweepThreshold = null,
        float? projectileSweepThreshold = null)
    {
        var world = new World { Gravity = JVector.Zero };

        RigidBody wall = world.CreateRigidBody();
        var wallShape = new BoxShape(0.1f, 10, 10);
        wall.AddShape(wallShape);
        wall.Position = JVector.Zero;
        wall.MotionType = wallMotion;
        wall.ContinuousDetection = wallMode;
        wallShape.SweepVelocityThreshold = wallSweepThreshold;

        RigidBody projectile = world.CreateRigidBody();
        var projectileShape = new BoxShape(0.2f, 0.2f, 0.2f);
        projectile.AddShape(projectileShape);
        projectile.Position = new JVector(-2, 0, 0);
        projectile.ContinuousDetection = projectileMode;
        projectile.Damping = (0f, 0f);
        projectile.Velocity = new JVector(TunnellingSpeed, 0, 0);
        projectileShape.SweepVelocityThreshold = projectileSweepThreshold;

        for (int i = 0; i < 10; i++) world.Step(Dt, false);

        return projectile.Position.X;
    }

    private static void AssertTunnelled(float x)
        => Assert.True(x > 1f, $"expected the projectile through the wall, it stopped at x={x}");

    private static void AssertStopped(float x)
        => Assert.True(x < 0f, $"expected the projectile stopped short of the wall, it reached x={x}");

    [Fact]
    public void Discrete_LetsAFastBodyTunnel()
    {
        // The problem being solved, stated as a test: without sweeping there is no overlap at either
        // end of the step for the narrow phase to react to.
        AssertTunnelled(FireAtWall(ContinuousDetectionMode.Discrete));
    }

    [Fact]
    public void Continuous_StopsAFastBodyAtAStaticWall()
    {
        AssertStopped(FireAtWall(ContinuousDetectionMode.Continuous));
    }

    [Fact]
    public void Continuous_StopsAFastBodyAtAKinematicWall()
    {
        // Kinematic counts as non-dynamic, so moving platforms are covered by the cheap mode.
        AssertStopped(FireAtWall(ContinuousDetectionMode.Continuous, MotionType.Kinematic));
    }

    [Fact]
    public void Continuous_DeclinesADynamicPartnerEvenAContinuousOne()
    {
        // Unity's distinction, and the reason Continuous is the cheap mode: a dynamic partner is not
        // swept against, however continuous that partner is itself. Contrast with
        // ContinuousDynamic_SweepsAgainstAContinuousPartner, which is the same pair with the
        // projectile upgraded one mode.
        AssertTunnelled(FireAtWall(
            ContinuousDetectionMode.Continuous,
            MotionType.Dynamic,
            ContinuousDetectionMode.Continuous));
    }

    [Fact]
    public void ContinuousDynamic_SweepsAgainstAContinuousPartner()
    {
        AssertStopped(FireAtWall(
            ContinuousDetectionMode.ContinuousDynamic,
            MotionType.Dynamic,
            ContinuousDetectionMode.Continuous));
    }

    [Fact]
    public void ContinuousDynamic_DeclinesADiscretePartner()
    {
        // Both sides have to opt in, which is what keeps a fast body among many discrete objects from
        // sweeping against all of them.
        AssertTunnelled(FireAtWall(
            ContinuousDetectionMode.ContinuousDynamic,
            MotionType.Dynamic,
            ContinuousDetectionMode.Discrete));
    }

    [Fact]
    public void ContinuousDynamic_StillSweepsAgainstStatic()
    {
        AssertStopped(FireAtWall(ContinuousDetectionMode.ContinuousDynamic));
    }

    [Fact]
    public void Speculative_SweepsAgainstADiscreteDynamicPartner()
    {
        // The unconditional mode: it does not care what the partner asked for.
        AssertStopped(FireAtWall(
            ContinuousDetectionMode.Speculative,
            MotionType.Dynamic,
            ContinuousDetectionMode.Discrete));
    }

    [Fact]
    public void ADiscreteBody_IsNotRescuedByAStationaryPartnersMode()
    {
        // The narrow-phase rule is "either side asking is enough", but reaching the narrow phase comes
        // first: only a continuous body enlarges its broad-phase bounds to cover where it is going, so a
        // discrete projectile and a stationary partner never produce an overlapping pair to test, no
        // matter what the partner asked for. Unity behaves the same way — CCD is a property of the body
        // that is moving.
        AssertTunnelled(FireAtWall(
            ContinuousDetectionMode.Discrete,
            MotionType.Dynamic,
            ContinuousDetectionMode.Speculative));
    }

    [Fact]
    public void AMovingPartnersRequest_ProtectsADiscreteBody()
    {
        // Where "either side asking is enough" is observable: the partner is moving, so its own
        // enlarged bounds cover the closing gap, the pair reaches the narrow phase, and its request for
        // a sweep stops both bodies even though the projectile asked for nothing.
        static float ConvergeAndReport(ContinuousDetectionMode partnerMode)
        {
            var world = new World { Gravity = JVector.Zero };

            RigidBody projectile = world.CreateRigidBody();
            projectile.AddShape(new BoxShape(0.2f, 0.2f, 0.2f));
            projectile.Position = new JVector(-2, 0, 0);
            projectile.ContinuousDetection = ContinuousDetectionMode.Discrete;
            projectile.Damping = (0f, 0f);
            projectile.Velocity = new JVector(50, 0, 0);

            RigidBody partner = world.CreateRigidBody();
            partner.AddShape(new BoxShape(0.1f, 10, 10));
            partner.Position = new JVector(2, 0, 0);
            partner.ContinuousDetection = partnerMode;
            partner.Damping = (0f, 0f);
            partner.Velocity = new JVector(-50, 0, 0);
            partner.SetMassInertia(1000f);

            for (int i = 0; i < 10; i++) world.Step(Dt, false);

            return projectile.Position.X;
        }

        AssertTunnelled(ConvergeAndReport(ContinuousDetectionMode.Discrete));
        AssertStopped(ConvergeAndReport(ContinuousDetectionMode.Speculative));
    }

    // ---- Kernel: broad-phase bounds ---------------------------------------------------------------

    private static float BoundsWidthAfterStep(ContinuousDetectionMode mode)
    {
        var world = new World { Gravity = JVector.Zero };
        RigidBody body = world.CreateRigidBody();
        var shape = new BoxShape(1, 1, 1);
        body.AddShape(shape);
        body.ContinuousDetection = mode;
        body.Damping = (0f, 0f);
        body.Velocity = new JVector(TunnellingSpeed, 0, 0);

        world.Step(Dt, false);

        return shape.WorldBoundingBox.Max.X - shape.WorldBoundingBox.Min.X;
    }

    [Fact]
    public void Discrete_DoesNotEnlargeTheBroadPhaseBounds()
    {
        Assert.Equal(1.0, (double)BoundsWidthAfterStep(ContinuousDetectionMode.Discrete), 3);
    }

    [Theory]
    [InlineData(ContinuousDetectionMode.Continuous)]
    [InlineData(ContinuousDetectionMode.ContinuousDynamic)]
    [InlineData(ContinuousDetectionMode.Speculative)]
    public void ContinuousModes_EnlargeTheBoundsByTheTravelDistance(ContinuousDetectionMode mode)
    {
        // Without this the broad phase would never offer the pair and the sweep could not run, so the
        // enlargement has to follow the mode rather than the old boolean.
        float width = BoundsWidthAfterStep(mode);
        Assert.True(width > 1.0 + TunnellingSpeed * Dt,
            $"{mode} should reserve the swept extent, bounds were only {width} wide");
    }

    // ---- Kernel: the legacy boolean ---------------------------------------------------------------

    [Fact]
    public void ContinuousDetection_DefaultsToDiscrete()
    {
        var world = new World();
        RigidBody body = world.CreateRigidBody();

        Assert.Equal(ContinuousDetectionMode.Discrete, body.ContinuousDetection);
        Assert.False(body.EnableSpeculativeContacts);
    }

    [Fact]
    public void EnableSpeculativeContacts_IsAViewOverTheMode()
    {
        var world = new World();
        RigidBody body = world.CreateRigidBody();

        // Existing callers and the vendored upstream tests go through this boolean, so it has to keep
        // meaning exactly what it used to: sweep against every partner.
        body.EnableSpeculativeContacts = true;
        Assert.Equal(ContinuousDetectionMode.Speculative, body.ContinuousDetection);

        body.EnableSpeculativeContacts = false;
        Assert.Equal(ContinuousDetectionMode.Discrete, body.ContinuousDetection);

        body.ContinuousDetection = ContinuousDetectionMode.Continuous;
        Assert.True(body.EnableSpeculativeContacts);
    }

    // ---- Kernel: per-shape sweep threshold --------------------------------------------------------

    [Fact]
    public void AThresholdAboveTheApproachSpeed_SuppressesTheSweep()
    {
        // Same continuous pair that otherwise stops the projectile, but both shapes declare they only
        // need sweeping above a speed this projectile never reaches. Both, because the pair takes the
        // lower of the two — see TheLowerOfTheTwoThresholdsDecides.
        AssertTunnelled(FireAtWall(
            ContinuousDetectionMode.Continuous,
            wallSweepThreshold: TunnellingSpeed * 2f,
            projectileSweepThreshold: TunnellingSpeed * 2f));
    }

    [Fact]
    public void TheLowerOfTheTwoThresholdsDecides()
    {
        // One shape asking for a high threshold must not veto a pair the other shape needs swept —
        // otherwise a thin wall could not protect itself from a projectile that opted out.
        AssertStopped(FireAtWall(
            ContinuousDetectionMode.Continuous,
            wallSweepThreshold: 1f,
            projectileSweepThreshold: TunnellingSpeed * 2f));
    }

    [Fact]
    public void AnUnsetThreshold_FallsBackToTheWorldValue()
    {
        var world = new World();
        RigidBody body = world.CreateRigidBody();
        var shape = new BoxShape(1, 1, 1);
        body.AddShape(shape);

        Assert.Null(shape.SweepVelocityThreshold);
        Assert.Equal(10.0, (double)world.SpeculativeVelocityThreshold, 3);
    }

    [Fact]
    public void SweepThreshold_RejectsNegativeValues()
    {
        var world = new World();
        RigidBody body = world.CreateRigidBody();
        var shape = new BoxShape(1, 1, 1);
        body.AddShape(shape);

        Assert.Throws<ArgumentOutOfRangeException>(() => shape.SweepVelocityThreshold = -1f);
    }

    // ---- Engine surface ---------------------------------------------------------------------------

    [Fact]
    public void Modes_MatchUnitysValues()
    {
        Assert.Equal(0, (int)CollisionDetectionMode.Discrete);
        Assert.Equal(1, (int)CollisionDetectionMode.Continuous);
        Assert.Equal(2, (int)CollisionDetectionMode.ContinuousDynamic);
        Assert.Equal(3, (int)CollisionDetectionMode.ContinuousSpeculative);
    }

    [Theory]
    [InlineData(CollisionDetectionMode.Discrete, ContinuousDetectionMode.Discrete)]
    [InlineData(CollisionDetectionMode.Continuous, ContinuousDetectionMode.Continuous)]
    [InlineData(CollisionDetectionMode.ContinuousDynamic, ContinuousDetectionMode.ContinuousDynamic)]
    [InlineData(CollisionDetectionMode.ContinuousSpeculative, ContinuousDetectionMode.Speculative)]
    public void EngineModes_LineUpWithKernelModes(CollisionDetectionMode engine, ContinuousDetectionMode kernel)
    {
        // Rigidbody3D casts between these two enums rather than switching, so their values have to
        // agree — this is what makes that cast safe to read.
        Assert.Equal((int)kernel, (int)engine);
    }

    private Scene CreatePhysicsScene()
    {
        var scene = CreateScene(enable: true);
        scene.Physics.UseMultithreading = false;
        scene.Physics.Gravity = Float3.Zero;
        return scene;
    }

    [Fact]
    public void DiscreteIsTheDefaultMode()
    {
        Scene scene = CreatePhysicsScene();
        var go = CreateGameObject("Body");
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        go.AddComponent<BoxCollider>();
        scene.Add(go);

        Assert.Equal(CollisionDetectionMode.Discrete, rb.CollisionDetectionMode);
        Assert.Equal(ContinuousDetectionMode.Discrete, rb._body.ContinuousDetection);
    }

    [Fact]
    public void SettingTheMode_ReachesTheKernel()
    {
        Scene scene = CreatePhysicsScene();
        var go = CreateGameObject("Body");
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        go.AddComponent<BoxCollider>();
        scene.Add(go);

        rb.CollisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        Assert.Equal(ContinuousDetectionMode.ContinuousDynamic, rb._body.ContinuousDetection);
    }

    [Fact]
    public void TheMode_SurvivesOnValidate()
    {
        Scene scene = CreatePhysicsScene();
        var go = CreateGameObject("Body");
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        go.AddComponent<BoxCollider>();
        scene.Add(go);

        rb.CollisionDetectionMode = CollisionDetectionMode.Continuous;
        rb.OnValidate();

        Assert.Equal(ContinuousDetectionMode.Continuous, rb._body.ContinuousDetection);
    }

    [Fact]
    public void EnableSpeculativeContacts_StillWorksOnTheComponent()
    {
        Scene scene = CreatePhysicsScene();
        var go = CreateGameObject("Body");
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        go.AddComponent<BoxCollider>();
        scene.Add(go);

        Assert.False(rb.EnableSpeculativeContacts);

        rb.EnableSpeculativeContacts = true;

        Assert.Equal(CollisionDetectionMode.ContinuousSpeculative, rb.CollisionDetectionMode);
        Assert.Equal(ContinuousDetectionMode.Speculative, rb._body.ContinuousDetection);
    }

    [Fact]
    public void ColliderSweepThreshold_ReachesTheShapes()
    {
        Scene scene = CreatePhysicsScene();
        var go = CreateGameObject("Body");
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        BoxCollider collider = go.AddComponent<BoxCollider>();
        scene.Add(go);

        collider.SweepVelocityThreshold = 2.5f;
        StepPhysics(scene);

        Assert.Equal(2.5, (double)ShapeOf(rb).SweepVelocityThreshold!.Value, 4);
    }

    [Fact]
    public void AZeroColliderThreshold_LeavesTheShapeOnTheWorldValue()
    {
        Scene scene = CreatePhysicsScene();
        var go = CreateGameObject("Body");
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        BoxCollider collider = go.AddComponent<BoxCollider>();
        scene.Add(go);

        collider.SweepVelocityThreshold = 3f;
        collider.SweepVelocityThreshold = 0f;

        // Zero is the authoring spelling of "no opinion", not a threshold of zero — which would sweep
        // every pair at any speed.
        Assert.Null(ShapeOf(rb).SweepVelocityThreshold);
    }

    [Fact]
    public void ANegativeColliderThreshold_IsClampedAway()
    {
        Scene scene = CreatePhysicsScene();
        var go = CreateGameObject("Body");
        Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
        BoxCollider collider = go.AddComponent<BoxCollider>();
        scene.Add(go);

        collider.SweepVelocityThreshold = -5f;

        Assert.Equal(0f, collider.SweepVelocityThreshold);
        Assert.Null(ShapeOf(rb).SweepVelocityThreshold);
    }

    [Fact]
    public void WorldSweepThreshold_ReachesTheKernel()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.SweepVelocityThreshold = 42f;

        StepPhysics(scene);

        Assert.Equal(42.0, (double)scene.Physics.World.SpeculativeVelocityThreshold, 3);
    }

    private static RigidBodyShape ShapeOf(Rigidbody3D rb)
    {
        foreach (RigidBodyShape shape in rb._body.Shapes)
            return shape;

        throw new InvalidOperationException("body has no shapes");
    }

    // ---- Engine: end to end -----------------------------------------------------------------------

    [Fact]
    public void ContinuousMode_StopsAFastBodyAtAThinWall()
    {
        static float Fire(CollisionDetectionMode mode, CollisionDetectionModeTests test)
        {
            Scene scene = test.CreatePhysicsScene();

            var wall = test.CreateGameObject("Wall");
            wall.Transform.Position = Float3.Zero;
            BoxCollider wallCollider = wall.AddComponent<BoxCollider>();
            wallCollider.Size = new Float3(0.1f, 10, 10);
            scene.Add(wall);

            var go = test.CreateGameObject("Projectile");
            go.Transform.Position = new Float3(-2, 0, 0);
            Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
            rb.CollisionDetectionMode = mode;
            rb.LinearDamping = 0f;
            BoxCollider collider = go.AddComponent<BoxCollider>();
            collider.Size = new Float3(0.2f, 0.2f, 0.2f);
            scene.Add(go);

            rb.LinearVelocity = new Float3(TunnellingSpeed, 0, 0);
            test.Tick(scene, 10);

            return go.Transform.Position.X;
        }

        float discrete = Fire(CollisionDetectionMode.Discrete, this);
        float continuous = Fire(CollisionDetectionMode.Continuous, this);

        AssertTunnelled(discrete);
        AssertStopped(continuous);
    }
}
