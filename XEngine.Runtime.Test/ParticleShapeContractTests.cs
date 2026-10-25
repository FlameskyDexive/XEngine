using System;
using XEngine.Runtime.ParticleSystem.Modules;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ParticleShapeContractTests
{
    [Theory]
    [InlineData(90f)]
    [InlineData(260f)]
    [InlineData(360f)]
    public void UnityCircle_UsesCounterClockwiseXYArcAndRadialVelocity(float arc)
    {
        var shape = new EmissionModule { Shape = EmissionShape.Circle, ShapeUsesZAxis = true,
            Radius = 2f, RadiusThickness = 0f, Arc = arc };
        var random = new Random(1234);
        float minimumAngle = 360f, maximumAngle = 0f;
        for (int i = 0; i < 4096; i++)
        {
            shape.GetShapePositionAndDirection(random, out var position, out var direction);
            Assert.Equal(0f, position.Z);
            Assert.InRange(Float3.Length(position), 1.99999f, 2.00001f);
            Assert.InRange(Float3.Length(direction), .99999f, 1.00001f);
            Assert.InRange(Float3.Length(position - direction * 2f), 0f, .00001f);
            float angle = MathF.Atan2(position.Y, position.X) * (180f / MathF.PI);
            if (angle < 0f) angle += 360f;
            Assert.InRange(angle, 0f, arc + .0001f);
            minimumAngle = MathF.Min(minimumAngle, angle);
            maximumAngle = MathF.Max(maximumAngle, angle);
        }
        Assert.InRange(minimumAngle, 0f, arc * .01f);
        Assert.InRange(maximumAngle, arc * .99f, arc);
    }

    [Theory]
    [InlineData(0f, 4f)]
    [InlineData(.5f, 2.5f)]
    [InlineData(1f, 2f)]
    public void UnityCircle_RadiusThicknessHasUniformAreaDensity(float thickness, float expectedRadiusSquared)
    {
        var shape = new EmissionModule { Shape = EmissionShape.Circle, ShapeUsesZAxis = true,
            Radius = 2f, RadiusThickness = thickness };
        var random = new Random(1234);
        double radiusSquared = 0;
        for (int i = 0; i < 10000; i++)
        {
            shape.GetShapePositionAndDirection(random, out var position, out _);
            float radius = Float3.Length(position);
            Assert.InRange(radius, 2f * (1f - thickness) - .00001f, 2.00001f);
            radiusSquared += radius * radius;
        }
        // Unity 256-sample probe gives 4, 2.537 and 2.049, respectively.
        Assert.InRange(radiusSquared / 10000, expectedRadiusSquared - .04, expectedRadiusSquared + .04);
    }

    [Fact]
    public void UnityCone_QuarterArcUsesPositiveXYAndForwardZ()
    {
        var shape = new EmissionModule { Shape = EmissionShape.Cone, ShapeUsesZAxis = true,
            Radius = 2f, RadiusThickness = 0f, ConeAngle = 25f, Arc = 90f };
        var random = new Random(1234);
        for (int i = 0; i < 100; i++)
        {
            shape.GetShapePositionAndDirection(random, out var position, out var direction);
            Assert.Equal(0f, position.Z);
            Assert.InRange(position.X, 0f, 2f); Assert.InRange(position.Y, 0f, 2f);
            Assert.True(direction.X >= 0f && direction.Y >= 0f && direction.Z > 0f);
        }
    }

    [Fact]
    public void UnitySphere_ZeroThicknessEmitsOnShell()
    {
        var shape = new EmissionModule { Shape = EmissionShape.Sphere, ShapeUsesZAxis = true,
            Radius = 2f, RadiusThickness = 0f };
        var random = new Random(1234);
        for (int i = 0; i < 100; i++)
        {
            shape.GetShapePositionAndDirection(random, out var position, out var direction);
            Assert.InRange(Float3.Length(position), 1.99999f, 2.00001f);
            Assert.InRange(Float3.Length(position - direction * 2f), 0f, .00001f);
        }
    }
}
