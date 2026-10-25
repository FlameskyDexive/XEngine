using System.Threading.Tasks;

namespace JitterTests.Robustness;

public class MultiWorldThreadingTests
{
    [Test]
    public async Task SeparateWorlds_WithSingleThreadedSteps_CanRunOnSeparateThreads()
    {
        Task[] tasks = new Task[4];

        for (int i = 0; i < tasks.Length; i++)
        {
            int worldIndex = i;
            tasks[i] = Task.Run(() => RunSingleThreadedWorld(worldIndex));
        }

        await Task.WhenAll(tasks);
    }

    [Test]
    public async Task SeparateWorlds_WithMultithreadedSteps_AreInternallySerialized()
    {
        Task[] tasks = new Task[3];

        for (int i = 0; i < tasks.Length; i++)
        {
            int worldIndex = i;
            tasks[i] = Task.Run(() => RunMultithreadedWorld(worldIndex));
        }

        await Task.WhenAll(tasks);
    }

    private static void RunSingleThreadedWorld(int worldIndex)
    {
        using var world = new World
        {
            Gravity = JVector.Zero,
            AllowDeactivation = false
        };

        for (int i = 0; i < 40; i++)
        {
            var bodyA = world.CreateRigidBody();
            bodyA.AddShape(new SphereShape(1));
            bodyA.Position = new JVector(worldIndex * 10, 0, 0);

            var bodyB = world.CreateRigidBody();
            bodyB.AddShape(new SphereShape(1));
            bodyB.Position = bodyA.Position + new JVector((Real)0.5, 0, 0);

            var constraint = world.CreateConstraint<BallSocket>(bodyA, bodyB);
            constraint.Initialize(bodyA.Position);

            world.Step((Real)(1.0 / 60.0), multiThread: false);

            world.Remove(constraint);
            bodyB.Position = bodyA.Position + new JVector(10, 0, 0);
            world.Step((Real)(1.0 / 60.0), multiThread: false);

            world.Remove(bodyB);
            world.Remove(bodyA);
        }
    }

    private static void RunMultithreadedWorld(int worldIndex)
    {
        using var world = new World
        {
            Gravity = JVector.Zero,
            AllowDeactivation = false
        };

        var bodyA = world.CreateRigidBody();
        bodyA.AddShape(new SphereShape(1));
        bodyA.Position = new JVector(worldIndex * 10, 0, 0);

        var bodyB = world.CreateRigidBody();
        bodyB.AddShape(new SphereShape(1));
        bodyB.Position = bodyA.Position + new JVector((Real)0.8, 0, 0);

        var constraint = world.CreateConstraint<BallSocket>(bodyA, bodyB);
        constraint.Initialize(bodyA.Position);

        for (int i = 0; i < 8; i++)
        {
            world.Step((Real)(1.0 / 120.0), multiThread: true);
        }

        world.Stabilize((Real)(1.0 / 120.0), solverIterations: 1, multiThread: true);

        Assert.That(Real.IsFinite(bodyA.Position.X), Is.True);
        Assert.That(Real.IsFinite(bodyB.Position.X), Is.True);
    }
}
