namespace JitterTests.Robustness;

public class ArgumentValidationTests
{
    [Test]
    public void ConstraintInitialize_WithZeroAxis_Throws()
    {
        using World world = new();
        RigidBody body1 = world.CreateRigidBody();
        RigidBody body2 = world.CreateRigidBody();
        PointOnLine constraint = world.CreateConstraint<PointOnLine>(body1, body2);

        Assert.Throws<ArgumentException>(() => constraint.Initialize(JVector.Zero, JVector.Zero, JVector.Zero));
    }

    [Test]
    public void ShapeCreation_WithNaNDimension_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new SphereShape(Real.NaN));
    }

    [Test]
    public void PointCloudShape_WithDegenerateVertices_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => _ = new PointCloudShape([JVector.Zero]));

        Assert.Throws<InvalidOperationException>(() => _ = new PointCloudShape([
            JVector.Zero,
            JVector.UnitX,
            JVector.UnitY,
            JVector.UnitX + JVector.UnitY
        ]));

        Assert.Throws<InvalidOperationException>(() => _ = new PointCloudShape([
            new JVector(-1, -1, 1),
            new JVector(+1, -1, 1),
            new JVector(+1, +1, 1),
            new JVector(-1, +1, 1)
        ]));
    }

    [Test]
    public void ConvexHullShape_WithZeroMassTriangles_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => _ = new ConvexHullShape([
            new JTriangle(JVector.Zero, JVector.UnitX, JVector.UnitY)
        ]));
    }

    [Test]
    public void SupportPrimitiveCreation_WithInfiniteDimension_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = SupportPrimitives.CreateSphere(Real.PositiveInfinity));
    }

    [Test]
    public void DynamicTreeAddProxy_WithDuplicateOrOversizedProxy_ThrowsDistinctExceptions()
    {
        using World world = new();
        var proxy = new SphereShape((Real)1.0);
        world.DynamicTree.AddProxy(proxy);

        var duplicate = Assert.Throws<ArgumentException>(() => world.DynamicTree.AddProxy(proxy));
        Assert.That(duplicate!.ParamName, Is.EqualTo("proxy"));

        var oversized = new BoxShape((Real)1e8);
        var tooLarge = Assert.Throws<ArgumentOutOfRangeException>(() => world.DynamicTree.AddProxy(oversized));
        Assert.That(tooLarge!.ParamName, Is.EqualTo("proxy"));
    }

    [Test]
    public void TriangleMeshCreate_WithInvalidVertexTypeOrIndexCount_ReportsParameter()
    {
        Real[] rawVertices = [(Real)0.0, (Real)1.0, (Real)2.0];
        int[] triangle = [0, 0, 0];

        var vertexType = Assert.Throws<ArgumentException>(() =>
            TriangleMesh.Create<Real>(rawVertices, triangle, true));
        Assert.That(vertexType!.ParamName, Is.EqualTo("vertices"));

        JVector[] vertices = [JVector.Zero];
        int[] incompleteTriangle = [0, 0];

        var indexCount = Assert.Throws<ArgumentException>(() =>
            TriangleMesh.Create<JVector>(vertices, incompleteTriangle, true));
        Assert.That(indexCount!.ParamName, Is.EqualTo("indices"));
    }

    [Test]
    public void SolverIterations_WithInvalidTupleComponent_ReportsComponent()
    {
        using World world = new();

        var solver = Assert.Throws<ArgumentOutOfRangeException>(() => world.SolverIterations = (0, 0));
        Assert.That(solver!.ParamName, Is.EqualTo("solver"));

        var relaxation = Assert.Throws<ArgumentOutOfRangeException>(() => world.SolverIterations = (1, -1));
        Assert.That(relaxation!.ParamName, Is.EqualTo("relaxation"));
    }

    [Test]
    public void CustomExceptions_HaveStandardConstructors()
    {
        AssertStandardExceptionConstructors(typeof(SameBodyException));
        AssertStandardExceptionConstructors(typeof(World.InvalidCollisionTypeException));
        AssertStandardExceptionConstructors(typeof(TriangleMesh.DegenerateTriangleException));
        AssertStandardExceptionConstructors(typeof(Jitter2.Unmanaged.PartitionedBuffer<int>.MaximumSizeException));
    }

    private static void AssertStandardExceptionConstructors(Type exceptionType)
    {
        Assert.That(exceptionType.GetConstructor(Type.EmptyTypes), Is.Not.Null);
        Assert.That(exceptionType.GetConstructor([typeof(string)]), Is.Not.Null);
        Assert.That(exceptionType.GetConstructor([typeof(string), typeof(Exception)]), Is.Not.Null);
    }
}
