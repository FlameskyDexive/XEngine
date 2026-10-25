using System;
using System.Reflection;
using XEngine.Runtime.ParticleSystem;
using XEngine.Runtime.ParticleSystem.Modules;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ParticleStretchContractTests : RuntimeTestBase
{
    [Fact]
    public void UnityTrailProbe_ZeroVelocityScale_PreservesAuthoredLengthAndCustomStreams()
    {
        using var go = new GameObject("Unity Trail probe");
        go.Transform.Position = new Float3(2f, 3f, 7f);
        var system = go.AddComponent<ParticleSystemComponent>();
        system.PlayOnEnable = false;
        system.RenderMode = ParticleRenderMode.StretchedBillboard;
        system.Initial.StartSize = new MinMaxCurve(0.709942758f);
        system.Initial.StartSpeed = new MinMaxCurve(0f);
        system.Emission.ShapeEnabled = false;
        system.LengthScale = 3f;
        system.VelocityScale = 0f;
        system.CustomData.Enabled = true;
        system.CustomData.Custom1ToUV0ZW = true;
        system.CustomData.Custom1X = new MinMaxCurve(3f);
        system.CustomData.Custom1Y = new MinMaxCurve(0.25f);
        system.Emit(1);
        Float4x4 matrix = Instance(system);

        // Unity 6000.7 BakeMesh, Magic circle 1 loop / Trail at 0.2 s:
        // width 0.709942758, head-to-tail distance 2.1298283 with velocityScale=0.
        Assert.Equal(2.1298283f, matrix.c2.W - 1f, 5);
        Assert.Equal(0.709942758f, Float3.Length(new Float3(matrix.c0.X, matrix.c0.Y, matrix.c0.Z)), 5);
        Assert.Equal(0.709942758f, Float3.Length(new Float3(matrix.c1.X, matrix.c1.Y, matrix.c1.Z)), 5);
        Assert.Equal(3f, matrix.c0.W);
        Assert.Equal(0.25f, matrix.c1.W);
        Assert.Equal(new Float4(2f, 3f, 7f, 1f), matrix.c3);
    }

    [Theory]
    [InlineData(ParticleRenderMode.Billboard)]
    [InlineData(ParticleRenderMode.Mesh)]
    public void NonStretchedModes_PreserveNativeAffineLayout(ParticleRenderMode mode)
    {
        using var go = new GameObject("Native particle layout");
        go.Transform.Position = new Float3(2f, 3f, 7f);
        var system = go.AddComponent<ParticleSystemComponent>();
        system.PlayOnEnable = false;
        system.RenderMode = mode;
        system.Initial.StartSize = new MinMaxCurve(2f);
        system.Initial.StartSpeed = new MinMaxCurve(0f);
        system.Emission.ShapeEnabled = false;
        system.LengthScale = 9f;
        system.VelocityScale = 4f;
        system.Emit(1);
        Float4x4 matrix = Instance(system);

        Assert.Equal(new Float4(2f, 0f, 0f, 0f), matrix.c0);
        Assert.Equal(new Float4(0f, 2f, 0f, 0f), matrix.c1);
        Assert.Equal(new Float4(2f, 3f, 7f, 1f), matrix.c3);
        Assert.Equal(0f, matrix.c2.W);
        if (mode == ParticleRenderMode.Mesh)
            Assert.Equal(new Float4(0f, 0f, 2f, 0f), matrix.c2);
    }

    private static Float4x4 Instance(ParticleSystemComponent system)
    {
        typeof(ParticleSystemComponent).GetMethod("UpdateInstanceData", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(system, null);
        return ((Float4x4[])typeof(ParticleSystemComponent).GetField("_transforms", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!)[0];
    }

    [Fact]
    public void StretchBounds_ContainTheVisibleTailWhenTheHeadLeavesTheFrustum()
    {
        using var go = new GameObject("Long stretched particle");
        var system = go.AddComponent<ParticleSystemComponent>();
        system.PlayOnEnable = false;
        system.RenderMode = ParticleRenderMode.StretchedBillboard;
        system.SimulationSpace = SimulationSpace.World;
        system.Initial.StartSize = new MinMaxCurve(1f);
        system.LengthScale = 10f;
        system.VelocityScale = 0f;
        system.Emit(1);
        var particles = (System.Collections.Generic.List<Particle>)typeof(ParticleSystemComponent)
            .GetField("_particles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!;
        Particle particle = particles[0];
        particle.Position = new Float3(1f, 2f, 3f);
        particle.Velocity = new Float3(0f, 0f, 2f);
        particles[0] = particle;
        Instance(system);
        AABB bounds = (AABB)typeof(ParticleSystemComponent)
            .GetField("_bounds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!;
        Assert.InRange(-7f, bounds.Min.Z, bounds.Max.Z);
        Assert.InRange(3f, bounds.Min.Z, bounds.Max.Z);
    }
}
