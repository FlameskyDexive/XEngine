// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Jitter2;
using Jitter2.Collision.Shapes;
using Jitter2.Dynamics;
using Jitter2.LinearMath;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Tests for the sensor support XEngine adds to the vendored Jitter2 kernel
/// (see ThirdParty/Jitter2/PATCHES.md). These drive <see cref="World"/> directly rather than going
/// through <c>Collider</c>, so a failure points at the kernel patch rather than the engine wiring.
/// </summary>
public class JitterSensorTests
{
    private const float Dt = 1f / 60f;

    /// <summary>
    /// Builds a body with one box shape. Sensors also opt into event reporting, which is what makes
    /// the kernel track the pair.
    /// </summary>
    private static (RigidBody Body, RigidBodyShape Shape) AddBox(
        World world, JVector position, bool sensor = false, MotionType motion = MotionType.Dynamic)
    {
        var body = world.CreateRigidBody();
        var shape = new BoxShape(1f);
        body.AddShape(shape);
        body.Position = position;
        body.MotionType = motion;

        if (sensor)
        {
            shape.IsSensor = true;
            shape.ReportsPairEvents = true;
        }
        else
        {
            shape.ReportsPairEvents = true;
        }

        return (body, shape);
    }

    private static World CreateWorld()
    {
        var world = new World();
        world.Gravity = JVector.Zero; // keep bodies where the test puts them
        return world;
    }

    [Fact]
    public void Sensor_OverlappingPair_ReportsBeginOnce_ThenStays()
    {
        using var world = CreateWorld();
        var (_, sensorShape) = AddBox(world, JVector.Zero, sensor: true);
        var (_, otherShape) = AddBox(world, new JVector(0.5f, 0f, 0f));

        world.Step(Dt, multiThread: false);

        Assert.Equal(1, world.SensorOverlapsBegan.Length);
        Assert.Equal(0, world.SensorOverlapsEnded.Length);
        var pair = world.SensorOverlapsBegan[0];
        Assert.NotNull(pair.Other(sensorShape));
        Assert.Same(otherShape, pair.Other(sensorShape));

        // Second step: still overlapping, so it stays without being reported as new.
        world.Step(Dt, multiThread: false);

        Assert.Equal(0, world.SensorOverlapsBegan.Length);
        Assert.Equal(0, world.SensorOverlapsEnded.Length);
        Assert.Single(world.SensorOverlaps);
    }

    [Fact]
    public void Sensor_DoesNotCreateContacts_SoBodiesPassThrough()
    {
        using var world = CreateWorld();
        var (sensorBody, _) = AddBox(world, JVector.Zero, sensor: true, motion: MotionType.Static);
        var (moving, _) = AddBox(world, new JVector(-2f, 0f, 0f));

        moving.Damping = (0f, 0f); // so the velocity check below only reflects contact forces
        moving.Velocity = new JVector(4f, 0f, 0f);

        bool sawOverlap = false;
        for (int i = 0; i < 60; i++)
        {
            world.Step(Dt, multiThread: false);
            if (world.SensorOverlaps.Count > 0) sawOverlap = true;

            // The defining property of a sensor: it never produces a contact constraint.
            Assert.Empty(sensorBody.Contacts);
            Assert.Empty(moving.Contacts);
        }

        Assert.True(sawOverlap, "the moving body should have overlapped the sensor on its way through");

        // Nothing pushed back, so the body kept its velocity and is now past the sensor.
        Assert.True(moving.Position.X > 1f, $"expected the body to pass through, got x={moving.Position.X}");
        Assert.Equal(4f, moving.Velocity.X, 3);
    }

    [Fact]
    public void Sensor_Separating_ReportsEnd()
    {
        using var world = CreateWorld();
        AddBox(world, JVector.Zero, sensor: true, motion: MotionType.Static);
        var (moving, _) = AddBox(world, new JVector(0.5f, 0f, 0f));

        world.Step(Dt, multiThread: false);
        Assert.Equal(1, world.SensorOverlapsBegan.Length);

        // Teleport clear of the sensor.
        moving.Position = new JVector(20f, 0f, 0f);
        world.Step(Dt, multiThread: false);

        Assert.Equal(1, world.SensorOverlapsEnded.Length);
        Assert.Empty(world.SensorOverlaps);
    }

    /// <summary>
    /// Unity raises trigger events between a kinematic body and static geometry. Upstream skips any
    /// pair with nothing dynamic in it, because there is nothing to solve; the patch lets sensor pairs
    /// past that early-out.
    /// </summary>
    [Fact]
    public void Sensor_KinematicVersusStatic_StillReports()
    {
        using var world = CreateWorld();
        AddBox(world, JVector.Zero, sensor: true, motion: MotionType.Static);
        var (kinematic, _) = AddBox(world, new JVector(0.5f, 0f, 0f), motion: MotionType.Kinematic);

        world.Step(Dt, multiThread: false);

        Assert.Equal(1, world.SensorOverlapsBegan.Length);
        Assert.Empty(kinematic.Contacts);
    }

    /// <summary>
    /// A body that falls asleep inside a sensor must not be reported as having left it. The narrow
    /// phase skips sleeping pairs, so without the freshness pre-pass the overlap would look stale.
    /// </summary>
    [Fact]
    public void Sensor_SleepingBodyInside_DoesNotReportEnd()
    {
        using var world = CreateWorld();
        AddBox(world, JVector.Zero, sensor: true, motion: MotionType.Static);
        var (sleeper, _) = AddBox(world, new JVector(0.5f, 0f, 0f));

        world.Step(Dt, multiThread: false);
        Assert.Equal(1, world.SensorOverlapsBegan.Length);

        // Idle long enough to deactivate, then keep stepping while asleep.
        for (int i = 0; i < 240; i++)
        {
            world.Step(Dt, multiThread: false);
            Assert.Equal(0, world.SensorOverlapsEnded.Length);
        }

        Assert.False(sleeper.Data.IsActive, "the body should have gone to sleep");
        Assert.Single(world.SensorOverlaps);
    }

    /// <summary>
    /// The counterpart of the previous test: freshness is pre-marked only while the bounding boxes
    /// still overlap, so moving the sensor away from a sleeping body still ends the overlap.
    /// </summary>
    [Fact]
    public void Sensor_MovedAwayFromSleepingBody_ReportsEnd()
    {
        using var world = CreateWorld();
        var (sensorBody, _) = AddBox(world, JVector.Zero, sensor: true, motion: MotionType.Static);
        var (sleeper, _) = AddBox(world, new JVector(0.5f, 0f, 0f));

        for (int i = 0; i < 240; i++) world.Step(Dt, multiThread: false);
        Assert.False(sleeper.Data.IsActive);
        Assert.Single(world.SensorOverlaps);

        sensorBody.Position = new JVector(50f, 0f, 0f);
        world.Step(Dt, multiThread: false);

        Assert.Equal(1, world.SensorOverlapsEnded.Length);
        Assert.Empty(world.SensorOverlaps);
    }

    [Fact]
    public void Sensor_ShapeRemoved_ReportsEnd()
    {
        using var world = CreateWorld();
        var (sensorBody, sensorShape) = AddBox(world, JVector.Zero, sensor: true, motion: MotionType.Static);
        AddBox(world, new JVector(0.5f, 0f, 0f));

        world.Step(Dt, multiThread: false);
        Assert.Single(world.SensorOverlaps);

        sensorBody.RemoveShape(sensorShape, MassInertiaUpdateMode.Preserve);
        world.Step(Dt, multiThread: false);

        Assert.Equal(1, world.SensorOverlapsEnded.Length);
        Assert.Empty(world.SensorOverlaps);
    }

    /// <summary>
    /// Speculative contacts describe a pair that is about to touch, which is not an overlap. Sensors
    /// skip that path so a fast body does not report an early enter.
    /// </summary>
    [Fact]
    public void Sensor_DoesNotReportBeforeActualOverlap()
    {
        using var world = CreateWorld();
        AddBox(world, JVector.Zero, sensor: true, motion: MotionType.Static);
        var (fast, _) = AddBox(world, new JVector(-5f, 0f, 0f));

        fast.EnableSpeculativeContacts = true;
        fast.Velocity = new JVector(120f, 0f, 0f); // far more than one box per step

        world.Step(Dt, multiThread: false);

        // After one step the body is still short of the sensor, so nothing may have been reported.
        Assert.True(fast.Position.X < -0.9f, $"test setup expects the body to still be clear, x={fast.Position.X}");
        Assert.Equal(0, world.SensorOverlapsBegan.Length);
    }

    /// <summary>
    /// Event tracking is opt-in, so a trigger nobody listens to costs no bookkeeping while still
    /// behaving as a sensor physically.
    /// </summary>
    [Fact]
    public void Sensor_WithoutPairEventReporting_IsNotTracked()
    {
        using var world = CreateWorld();
        var (_, sensorShape) = AddBox(world, JVector.Zero, sensor: true, motion: MotionType.Static);
        var (other, otherShape) = AddBox(world, new JVector(0.5f, 0f, 0f));

        sensorShape.ReportsPairEvents = false;
        otherShape.ReportsPairEvents = false;

        world.Step(Dt, multiThread: false);

        Assert.Empty(world.SensorOverlaps);
        Assert.Equal(0, world.SensorOverlapsBegan.Length);
        Assert.Empty(other.Contacts); // still a sensor: no contact either
    }

    /// <summary>
    /// Ordering by shape id makes a pair independent of the order the broad phase reported it in.
    /// </summary>
    [Fact]
    public void SensorOverlap_IsOrderIndependent()
    {
        using var world = CreateWorld();
        var (_, a) = AddBox(world, JVector.Zero, sensor: true);
        var (_, b) = AddBox(world, new JVector(0.5f, 0f, 0f));

        world.Step(Dt, multiThread: false);

        var pair = world.SensorOverlapsBegan[0];
        Assert.True(pair.ShapeA.ShapeId < pair.ShapeB.ShapeId);
        Assert.Same(b, pair.Other(a));
        Assert.Same(a, pair.Other(b));
    }

    [Fact]
    public void Sensor_BothShapesSensors_ReportsSinglePair()
    {
        using var world = CreateWorld();
        AddBox(world, JVector.Zero, sensor: true, motion: MotionType.Static);
        AddBox(world, new JVector(0.5f, 0f, 0f), sensor: true);

        world.Step(Dt, multiThread: false);

        Assert.Equal(1, world.SensorOverlapsBegan.Length);
        Assert.Single(world.SensorOverlaps);
    }

    [Fact]
    public void Sensor_WorldClear_DropsTrackedOverlaps()
    {
        using var world = CreateWorld();
        AddBox(world, JVector.Zero, sensor: true, motion: MotionType.Static);
        AddBox(world, new JVector(0.5f, 0f, 0f));

        world.Step(Dt, multiThread: false);
        Assert.Single(world.SensorOverlaps);

        world.Clear();

        Assert.Empty(world.SensorOverlaps);
        Assert.Equal(0, world.SensorOverlapsBegan.Length);
        Assert.Equal(0, world.SensorOverlapsEnded.Length);
    }

    /// <summary>
    /// Non-sensor pairs must be untouched by any of this: they still generate contacts and are still
    /// solved.
    /// </summary>
    [Fact]
    public void NonSensorPair_StillCollides()
    {
        using var world = CreateWorld();
        var (staticBody, _) = AddBox(world, JVector.Zero, motion: MotionType.Static);
        var (moving, _) = AddBox(world, new JVector(-2f, 0f, 0f));

        moving.Velocity = new JVector(4f, 0f, 0f);

        for (int i = 0; i < 60; i++) world.Step(Dt, multiThread: false);

        Assert.Empty(world.SensorOverlaps);
        Assert.True(moving.Position.X < 0.5f, $"expected the body to be stopped, got x={moving.Position.X}");
        Assert.NotEmpty(staticBody.Contacts);
    }
}
