#if DEBUG

namespace JitterTests.Robustness;

public class DebugValidationTests
{
    [Test]
    public void RigidBodyPosition_WithNaN_Throws()
    {
        using World world = new();
        RigidBody body = world.CreateRigidBody();

        Assert.Throws<ArgumentException>(() => body.Position = new JVector(Real.NaN, 0, 0));
    }

    [Test]
    public void RigidBodyOrientation_WithNonUnitQuaternion_Throws()
    {
        using World world = new();
        RigidBody body = world.CreateRigidBody();

        Assert.Throws<ArgumentException>(() => body.Orientation = new JQuaternion(0, 0, 0, 2));
    }
}

#endif
