using System;
using System.Collections.Generic;
using System.Reflection;
using XEngine.Runtime.ParticleSystem;
using XEngine.Runtime.ParticleSystem.Modules;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ParticleSimulationParityTests : RuntimeTestBase
{
    [Fact]
    public void NonLoopingSystem_FinishesParticlesAfterEmissionDuration()
    {
        using var go = new GameObject("OneShot");
        var system = CreateSystem(go, 0.1f, false);
        system.Initial.StartLifetime = new MinMaxCurve(0.5f);
        system.Play();
        system.Simulate(0.2f);
        Assert.Equal(1, system.ParticleCount);
        Assert.True(system.IsPlaying);
        system.Simulate(0.4f);
        Assert.Equal(0, system.ParticleCount);
        Assert.False(system.IsPlaying);
    }

    [Fact]
    public void LoopingSystem_RepeatsBurstsAndPreservesRateRemainder()
    {
        using var go = new GameObject("Loop");
        var system = CreateSystem(go, 0.2f, true);
        system.Emission.RateOverTime = new MinMaxCurve(2f);
        system.Play();
        system.Simulate(0.45f);
        Assert.Equal(3, system.ParticleCount);
        system.Simulate(0.1f);
        Assert.Equal(4, system.ParticleCount);
    }

    [Fact]
    public void Delay_DoesNotAdvanceSystemTime_AndZeroStepDoesNotEmit()
    {
        using var go = new GameObject("Delay");
        var system = CreateSystem(go, 2f, false);
        system.StartDelay = 0.5f;
        system.Emission.Bursts[0] = new ParticleBurst(0.25f, 1);
        system.Play();
        system.Simulate(0f);
        system.Simulate(0.6f);
        Assert.Equal(0, system.ParticleCount);
        system.Simulate(0.2f);
        Assert.Equal(1, system.ParticleCount);
    }

    [Fact]
    public void Bursts_UseSecondsAndFiniteRepeats_WhenAFrameCrossesSeveral()
    {
        var module = new EmissionModule { Enabled = true };
        module.Bursts.Add(new ParticleBurst(0.5f, 2) { CycleCount = 3, RepeatInterval = 0.1f });
        var random = new Random(1);
        Assert.Equal(0, module.CalculateEmitCount(0.4f, 0.4f, random, 4f));
        Assert.Equal(6, module.CalculateEmitCount(0.4f, 0.8f, random, 4f));
        Assert.Equal(0, module.CalculateEmitCount(0.2f, 1f, random, 4f));
    }

    [Fact]
    public void FixedSeed_PlayAfterClear_ReproducesParticles()
    {
        using var go = new GameObject("Replay");
        var system = CreateSystem(go, 2f, true);
        system.RandomSeed = 123;
        system.Initial.StartSpeed = new MinMaxCurve(2f);
        system.Play();
        system.Simulate(0.1f);
        Particle first = Particles(system)[0];
        system.Clear();
        system.Play();
        system.Simulate(0.1f);
        Particle replay = Particles(system)[0];
        Assert.Equal(first.RandomSeed, replay.RandomSeed);
        Assert.Equal(first.Position, replay.Position);
        Assert.Equal(first.Velocity, replay.Velocity);
    }

    [Fact]
    public void BillboardRandomChannel_PreservesWorldDepthAndScale()
    {
        using var go = new GameObject("Billboard");
        go.Transform.Position = new Float3(2f, 3f, 7f);
        var system = CreateSystem(go, 2f, true);
        system.Initial.StartSize = new MinMaxCurve(2f);
        system.Emit(1);
        Float4x4 matrix = Instance(system);
        Assert.Equal(new Float4(2f, 3f, 7f, 1f), matrix.c3);
        Assert.Equal(new Float4(2f, 0f, 0f, 0f), matrix.c0);
        Assert.Equal(new Float4(0f, 2f, 0f, 0f), matrix.c1);
        Assert.InRange(matrix.c2.X, 0f, 1f);
        Assert.Equal(0f, matrix.c2.W);
    }

    [Fact]
    public void MeshTransform_ComposesEmitterAndParticleRotationAndThreeAxisSize()
    {
        using var go = new GameObject("Mesh");
        go.Transform.Position = new Float3(2f, 3f, 7f);
        go.Transform.LocalRotation = Quaternion.FromEuler(40f, 20f, 30f);
        go.Transform.LocalScale = new Float3(2f, 3f, 4f);
        var system = CreateSystem(go, 2f, true);
        system.RenderMode = ParticleRenderMode.Mesh;
        system.Initial.SeparateSizeAxes = true;
        system.Initial.StartSize = new MinMaxCurve(1f);
        system.Initial.StartSizeY = new MinMaxCurve(2f);
        system.Initial.StartSizeZ = new MinMaxCurve(3f);
        system.Initial.SeparateRotationAxes = true;
        system.Initial.StartRotationX = new MinMaxCurve(MathF.PI * 0.5f);
        system.Initial.StartRotationY = new MinMaxCurve(MathF.PI);
        system.Initial.StartRotation = new MinMaxCurve(MathF.PI * 0.5f);
        system.Emit(1);
        Float4x4 expected = go.Transform.LocalToWorldMatrix * Float4x4.CreateTRS(Float3.Zero,
            Quaternion.FromEuler(90f, 180f, 90f), new Float3(1f, 2f, 3f));
        Assert.Equal(expected, Instance(system));
    }

    [Fact]
    public void DisabledUnityShape_EmitsAlongForwardWithoutRandomDirection()
    {
        var shape = new EmissionModule { ShapeEnabled = false, ShapeForward = Float3.UnitZ };
        shape.GetShapePositionAndDirection(new Random(1), out Float3 position, out Float3 direction);
        Assert.Equal(Float3.Zero, position);
        Assert.Equal(Float3.UnitZ, direction);
    }

    [Theory]
    [InlineData(ParticleRenderAlignment.Local)]
    [InlineData(ParticleRenderAlignment.World)]
    public void OrientedBillboard_UsesAuthoredPlaneInsteadOfViewPlane(ParticleRenderAlignment alignment)
    {
        using var go = new GameObject("Circle");
        go.Transform.Position = new Float3(2f, 3f, 7f);
        go.Transform.LocalRotation = Quaternion.FromEuler(30f, 20f, 0f);
        var system = CreateSystem(go, 2f, true);
        system.RenderAlignment = alignment;
        system.Initial.SeparateRotationAxes = true;
        system.Initial.StartRotationX = new MinMaxCurve(MathF.PI * 0.5f);
        system.Emit(1);
        Float4x4 local = Float4x4.CreateTRS(Float3.Zero,
            Quaternion.FromEuler(90f, 0f, 0f), Float3.One);
        Float4x4 expected = alignment == ParticleRenderAlignment.Local
            ? go.Transform.LocalToWorldMatrix * local
            : Float4x4.CreateTranslation(go.Transform.Position) * local;
        Assert.Equal(expected, Instance(system));
    }

    [Fact]
    public void LifetimeVelocity_IsAnAdditiveVelocity_AndDoesNotAccelerate()
    {
        var module = new VelocityOverLifetimeModule
        {
            Enabled = true,
            VelocityY = new AnimationCurve([new KeyFrame(0f, 3f), new KeyFrame(1f, 3f)]),
        };
        var particle = new Particle { Velocity = new Float3(1f, 2f, 0f), Lifetime = 1f, StartLifetime = 1f };
        for (int i = 0; i < 60; i++)
            module.OnParticleUpdate(ref particle, 1f / 60f);
        Assert.Equal(new Float3(1f, 5f, 0f), particle.Velocity);
        module.Enabled = false;
        module.OnParticleUpdate(ref particle, 1f / 60f);
        Assert.Equal(new Float3(1f, 2f, 0f), particle.Velocity);
    }

    [Fact]
    public void SubEmitter_ResolvesWithinNestedEffectInstance_AndAdvancesSpawnedParticles()
    {
        using var container = new GameObject("Viewer");
        using var effect = new GameObject("Effect");
        using var emitter = new GameObject("Core");
        using var child = new GameObject("Sparks");
        effect.SetParent(container, false);
        emitter.SetParent(effect, false);
        child.SetParent(effect, false);
        var parent = CreateSystem(emitter, 1f, false);
        var target = CreateSystem(child, 1f, false);
        target.Stop();
        target.Initial.StartLifetime = new MinMaxCurve(0.2f);
        parent.SubEmitters.Enabled = true;
        parent.SubEmitters.Emitters.Add(new SubEmitterSetting { TargetPath = "Sparks" });
        parent.Emit(1);
        Assert.Equal(1, target.ParticleCount);
        target.Simulate(0.3f);
        Assert.Equal(0, target.ParticleCount);
    }

    [Fact]
    public void CustomVectorStream_UsesAuthoredCurveValues_AndPreservesTransformAndFlipbook()
    {
        using var go = new GameObject("CustomVector");
        go.Transform.Position = new Float3(2f, 3f, 7f);
        var system = CreateSystem(go, 2f, true);
        system.CustomData.Enabled = true;
        system.CustomData.Custom1ToUV0ZW = true;
        system.CustomData.Custom1X = new MinMaxCurve(3f);
        system.CustomData.Custom1Y = new MinMaxCurve(0.25f);
        system.CustomData.Custom1Z = new MinMaxCurve(5f);
        system.CustomData.Custom1W = new MinMaxCurve(6f);
        system.Play();
        system.Simulate(0.2f);
        Particle particle = Particles(system)[0];
        Assert.Equal(new Float4(3f, 0.25f, 5f, 6f), particle.Custom1);
        Float4x4 matrix = Instance(system);
        Assert.Equal(3f, matrix.c0.W);
        Assert.Equal(0.25f, matrix.c1.W);
        Assert.Equal(new Float4(2f, 3f, 7f, 1f), matrix.c3);
        var custom = (Float4[])typeof(ParticleSystemComponent).GetField("_customData", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!;
        Assert.Equal(new Float4(particle.NormalizedLifetime, 0f, 0f, 1f), custom[0]);
    }

    [Fact]
    public void CustomVectorRandomBetweenCurves_UsesStableFactorAcrossFrames()
    {
        var module = new ParticleCustomDataModule
        {
            Enabled = true,
            Custom1X = new MinMaxCurve
            {
                Mode = MinMaxCurveMode.Random,
                MinCurve = new AnimationCurve([new KeyFrame(0f, 2f), new KeyFrame(1f, 2f)]),
                MaxCurve = new AnimationCurve([new KeyFrame(0f, 4f), new KeyFrame(1f, 4f)]),
            },
        };
        var particle = new Particle { RandomSeed = 17, StartLifetime = 1f, Lifetime = 1f };
        module.OnParticleSpawn(ref particle, new Random(1));
        float expected = particle.Custom1.X;
        Assert.InRange(expected, 2f, 4f);
        for (int i = 0; i < 20; i++)
        {
            particle.Lifetime -= 0.02f;
            module.OnParticleUpdate(ref particle, 0.02f);
            Assert.Equal(expected, particle.Custom1.X);
        }
    }

    [Fact]
    public void UnityZeroAngleCone_EmitsAcrossBaseDisk_WithParallelForwardVelocity()
    {
        var module = new EmissionModule
        {
            Shape = EmissionShape.Cone,
            ShapeUsesZAxis = true,
            Radius = 1f,
            ConeAngle = 0f,
        };
        var random = new Random(17);
        float sumSquaredRadius = 0f;
        for (int i = 0; i < 2000; i++)
        {
            module.GetShapePositionAndDirection(random, out Float3 position, out Float3 direction);
            Assert.Equal(0f, position.Z);
            Assert.Equal(Float3.UnitZ, direction);
            float radiusSquared = position.X * position.X + position.Y * position.Y;
            Assert.InRange(radiusSquared, 0f, 1f);
            sumSquaredRadius += radiusSquared;
        }
        // Uniform disk density has E[r^2] = R^2 / 2. A collapsed cone has zero.
        Assert.InRange(sumSquaredRadius / 2000f, 0.47f, 0.53f);
    }

    [Fact]
    public void UnityConeShell_PreservesAuthoredRadiusAtZeroAngle()
    {
        var module = new EmissionModule
        {
            Shape = EmissionShape.Cone,
            ShapeUsesZAxis = true,
            Radius = 2f,
            RadiusThickness = 0f,
            ConeAngle = 0f,
        };
        module.GetShapePositionAndDirection(new Random(17), out Float3 position, out _);
        Assert.Equal(2f, Float3.Length(position), 5);
    }

    [Fact]
    public void DistanceEmission_SpawnsAtTravelledIntervalsInsteadOfFrameEndpoint()
    {
        using var go = new GameObject("MovingEmitter");
        var system = CreateSystem(go, 2f, true);
        system.SimulationSpace = SimulationSpace.World;
        system.Emission.Bursts.Clear();
        system.Emission.RateOverDistance = new MinMaxCurve(10f);
        system.Play();
        go.Transform.Position = new Float3(1f, 0f, 0f);
        system.Simulate(0.1f);
        List<Particle> particles = Particles(system);
        Assert.Equal(10, particles.Count);
        for (int i = 0; i < 10; i++)
            Assert.Equal((i + 1) * 0.1f, particles[i].Position.X, 5);
        system.Simulate(0.1f);
        Assert.Equal(10, system.ParticleCount);
    }

    [Fact]
    public void BirthSubEmitter_FollowsEachParentDistance_AndStopsWhenParentClears()
    {
        using var effect = new GameObject("Effect");
        using var parentGo = new GameObject("Parent");
        using var targetGo = new GameObject("Child");
        parentGo.SetParent(effect, false);
        targetGo.SetParent(effect, false);
        var parent = CreateSystem(parentGo, 2f, true);
        parent.Initial.StartSpeed = new MinMaxCurve(1f);
        parent.Emission.ShapeForward = Float3.UnitZ;
        parent.Emission.Bursts.Clear();
        var target = CreateSystem(targetGo, 2f, true);
        target.Stop();
        target.Emission.Bursts.Clear();
        target.Emission.RateOverDistance = new MinMaxCurve(3f);
        parent.SubEmitters.Enabled = true;
        parent.SubEmitters.Emitters.Add(new SubEmitterSetting
        {
            TargetPath = "Child",
            UseTargetEmission = true,
        });
        parent.Play();
        parent.Emit(2);
        Assert.Equal(0, target.ParticleCount);
        parent.Simulate(0.2f);
        Assert.Equal(0, target.ParticleCount); // Per-parent fractional remainders are independent.
        parent.Simulate(0.2f);
        Assert.Equal(2, target.ParticleCount);
        Assert.Equal(1f / 3f, Particles(target)[0].Position.Z, 5);
        Assert.Equal(1f / 3f, Particles(target)[1].Position.Z, 5);
        parent.Clear();
        parent.Simulate(0.5f);
        Assert.Equal(2, target.ParticleCount);
        target.Simulate(3.1f);
        Assert.Equal(0, target.ParticleCount); // Children retain their own independent lifetime.
    }

    [Fact]
    public void BirthSubEmitter_UsesCurrentParentColor_AndTargetEmissionDuration()
    {
        using var effect = new GameObject("Effect");
        using var parentGo = new GameObject("Parent");
        using var targetGo = new GameObject("Child");
        parentGo.SetParent(effect, false);
        targetGo.SetParent(effect, false);
        var parent = CreateSystem(parentGo, 2f, true);
        parent.Emission.Bursts.Clear();
        var target = CreateSystem(targetGo, 0.2f, false);
        target.Stop();
        target.Emission.Bursts.Clear();
        target.Emission.RateOverTime = new MinMaxCurve(10f);
        parent.SubEmitters.Enabled = true;
        parent.SubEmitters.Emitters.Add(new SubEmitterSetting
        {
            TargetPath = "Child",
            UseTargetEmission = true,
            InheritColor = true,
        });
        parent.Play();
        parent.Emit(1);
        List<Particle> parents = Particles(parent);
        Particle changed = parents[0];
        changed.Color = new Color(0.1f, 0.4f, 0.8f, 0.5f);
        parents[0] = changed;
        parent.Simulate(0.3f);
        Assert.Equal(2, target.ParticleCount);
        Assert.Equal(changed.Color, Particles(target)[0].Color);
        parent.Simulate(0.3f);
        Assert.Equal(2, target.ParticleCount);
    }

    [Fact]
    public void OrbitalVelocity_RotatesAroundAuthoredOffsetWithoutGrowingRadius()
    {
        var module = new VelocityOverLifetimeModule
        {
            Enabled = true,
            UseMinMaxCurves = true,
            OrbitalZ = new MinMaxCurve(2f * MathF.PI),
            OrbitalOffsetX = new MinMaxCurve(0.5f),
        };
        var particle = new Particle
        {
            Position = new Float3(1.5f, 0f, 0f),
            StartLifetime = 2f,
            Lifetime = 2f,
        };
        for (int i = 0; i < 60; i++)
        {
            module.OnParticleUpdate(ref particle, 1f / 60f);
            particle.Position += particle.Velocity * (1f / 60f);
        }
        Assert.InRange(Float3.Length(particle.Position - new Float3(1.5f, 0f, 0f)), 0f, 0.0001f);
        Assert.InRange(Float3.Length(particle.Position - new Float3(0.5f, 0f, 0f)), 0.9999f, 1.0001f);
    }

    [Fact]
    public void LocalLifetimeVelocity_TransformsIntoWorldSimulationSpace()
    {
        using var go = new GameObject("RotatedEmitter");
        go.Transform.LocalRotation = Quaternion.FromEuler(0f, 90f, 0f);
        var system = CreateSystem(go, 2f, true);
        system.SimulationSpace = SimulationSpace.World;
        system.VelocityOverLifetime.Enabled = true;
        system.VelocityOverLifetime.UseMinMaxCurves = true;
        system.VelocityOverLifetime.LinearZ = new MinMaxCurve(2f);
        system.Play();
        system.Simulate(0.1f);
        Float3 expected = go.Transform.Rotation * new Float3(0f, 0f, 0.2f);
        Assert.InRange(Float3.Length(Particles(system)[0].Position - expected), 0f, 0.00001f);
    }

    [Fact]
    public void RadialVelocity_AndSpeedModifier_AreVelocityContributions()
    {
        var module = new VelocityOverLifetimeModule
        {
            Enabled = true,
            UseMinMaxCurves = true,
            Radial = new MinMaxCurve(2f),
            SpeedModifier = new MinMaxCurve(2f),
        };
        var particle = new Particle
        {
            Position = Float3.UnitX,
            Velocity = Float3.UnitX,
            StartLifetime = 2f,
            Lifetime = 2f,
        };
        for (int i = 0; i < 20; i++)
            module.OnParticleUpdate(ref particle, 0.02f);
        Assert.Equal(new Float3(6f, 0f, 0f), particle.Velocity);
    }

    [Fact]
    public void BillboardStartRotation_ConvertsRadianQuarterTurnToVisibleQuarterTurn()
    {
        using var go = new GameObject("RotatedBillboard");
        var system = CreateSystem(go, 2f, true);
        system.Initial.StartRotation = new MinMaxCurve(MathF.PI * 0.5f);
        system.Emit(1);
        Float4x4 matrix = Instance(system);
        Assert.InRange(MathF.Abs(matrix.c0.X), 0f, 0.00001f);
        Assert.InRange(matrix.c0.Y, 0.99999f, 1.00001f);
        Assert.InRange(matrix.c1.X, -1.00001f, -0.99999f);
        Assert.InRange(MathF.Abs(matrix.c1.Y), 0f, 0.00001f);
    }

    [Fact]
    public void ForceOverLifetime_AccumulatesAccelerationAlongsideAdditiveVelocity()
    {
        using var go = new GameObject("RisingTrail");
        var system = CreateSystem(go, 2f, true);
        system.ForceOverLifetime.Enabled = true;
        system.ForceOverLifetime.ForceY = new MinMaxCurve(2f);
        system.VelocityOverLifetime.Enabled = true;
        system.VelocityOverLifetime.UseMinMaxCurves = true;
        system.VelocityOverLifetime.LinearX = new MinMaxCurve(3f);
        system.Play();
        for (int i = 0; i < 60; i++) system.Simulate(1f / 60f);
        Particle particle = Particles(system)[0];
        Assert.Equal(3f, particle.Velocity.X, 4);
        Assert.Equal(2f, particle.Velocity.Y, 4);
        Assert.Equal(3f, particle.Position.X, 4);
        Assert.InRange(particle.Position.Y, 1f, 1.02f);
    }

    [Theory]
    [InlineData(SimulationSpace.Local, SimulationSpace.World, 1f, 0f)]
    [InlineData(SimulationSpace.World, SimulationSpace.Local, -1f, 0f)]
    [InlineData(SimulationSpace.World, SimulationSpace.World, 0f, 1f)]
    [InlineData(SimulationSpace.Local, SimulationSpace.Local, 0f, 1f)]
    public void ForceSpace_RotatesAuthoredAxesWithoutScalingAcceleration(
        SimulationSpace forceSpace, SimulationSpace simulationSpace, float expectedX, float expectedZ)
    {
        using var go = new GameObject("ForceAxes");
        go.Transform.LocalRotation = Quaternion.FromEuler(0f, 90f, 0f);
        go.Transform.LocalScale = new Float3(2f, 3f, 4f);
        var system = CreateSystem(go, 2f, true);
        system.SimulationSpace = simulationSpace;
        system.ForceOverLifetime.Enabled = true;
        system.ForceOverLifetime.Space = forceSpace;
        system.ForceOverLifetime.ForceZ = new MinMaxCurve(2f);
        system.Play();
        system.Simulate(0.5f);
        Float3 velocity = Particles(system)[0].Velocity;
        Assert.Equal(expectedX, velocity.X, 5);
        Assert.Equal(0f, velocity.Y, 5);
        Assert.Equal(expectedZ, velocity.Z, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForceRandomRange_RespectsStableOrPerFrameSelection_AndReplays(bool randomize)
    {
        var module = new ForceOverLifetimeModule
        {
            Enabled = true,
            RandomizeEachFrame = randomize,
            ForceX = new MinMaxCurve
            {
                Mode = MinMaxCurveMode.Random,
                MinCurve = new AnimationCurve([new KeyFrame(0f, 2f), new KeyFrame(1f, 2f)]),
                MaxCurve = new AnimationCurve([new KeyFrame(0f, 4f), new KeyFrame(1f, 4f)]),
            },
        };
        Particle initial = new() { RandomSeed = 37, StartLifetime = 1f, Lifetime = 1f };
        Particle particle = initial;
        float first = 0f;
        bool varied = false;
        for (int i = 0; i < 20; i++)
        {
            float previous = particle.Velocity.X;
            module.OnParticleUpdate(ref particle, 0.02f);
            float acceleration = (particle.Velocity.X - previous) / 0.02f;
            Assert.InRange(acceleration, 1.99999f, 4.00001f);
            if (i == 0) first = acceleration;
            else if (MathF.Abs(acceleration - first) > 0.00001f) varied = true;
        }
        Assert.Equal(randomize, varied);
        Particle replay = initial;
        for (int i = 0; i < 20; i++) module.OnParticleUpdate(ref replay, 0.02f);
        Assert.Equal(particle.Velocity, replay.Velocity);
    }

    [Fact]
    public void ForceCurve_EvaluatesNormalizedLifetimeAndStopsWhenDisabled()
    {
        var module = new ForceOverLifetimeModule
        {
            Enabled = true,
            ForceY = new MinMaxCurve
            {
                Mode = MinMaxCurveMode.Curve,
                Curve = new AnimationCurve([new KeyFrame(0f, 0f), new KeyFrame(1f, 4f)]),
            },
        };
        var particle = new Particle { StartLifetime = 2f, Lifetime = 1f };
        module.OnParticleUpdate(ref particle, 0.5f);
        Assert.Equal(1f, particle.Velocity.Y, 5);
        module.Enabled = false;
        module.OnParticleUpdate(ref particle, 0.5f);
        Assert.Equal(1f, particle.Velocity.Y, 5);
    }

    private static ParticleSystemComponent CreateSystem(GameObject go, float duration, bool looping)
    {
        var system = go.AddComponent<ParticleSystemComponent>();
        system.Duration = duration;
        system.Looping = looping;
        system.PlayOnEnable = false;
        system.Initial.StartLifetime = new MinMaxCurve(3f);
        system.Initial.StartSpeed = new MinMaxCurve(0f);
        system.Emission.ShapeEnabled = false;
        system.Emission.Bursts.Add(new ParticleBurst(0f, 1));
        return system;
    }

    private static List<Particle> Particles(ParticleSystemComponent system) =>
        (List<Particle>)typeof(ParticleSystemComponent).GetField("_particles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!;

    private static Float4x4 Instance(ParticleSystemComponent system)
    {
        typeof(ParticleSystemComponent).GetMethod("UpdateInstanceData", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(system, null);
        return ((Float4x4[])typeof(ParticleSystemComponent).GetField("_transforms", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!)[0];
    }
}
