using System;
using System.Diagnostics;
using XEngine.Runtime.ParticleSystem;
using XEngine.Vector;
using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test;

public sealed class ParticleNoiseParityTests
{
    private readonly ITestOutputHelper _output;
    public ParticleNoiseParityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SpatialCurl_IsContinuousAndApproximatelyDivergenceFree()
    {
        Float3 p = new(0.23f, 0.47f, 0.81f);
        const float h = 0.01f;
        Float3 x = new(h, 0f, 0f), y = new(0f, h, 0f), z = new(0f, 0f, h);
        Float3 dx = ParticleNoiseField.Curl(p + x, ParticleNoiseQuality.High)
            - ParticleNoiseField.Curl(p - x, ParticleNoiseQuality.High);
        Float3 dy = ParticleNoiseField.Curl(p + y, ParticleNoiseQuality.High)
            - ParticleNoiseField.Curl(p - y, ParticleNoiseQuality.High);
        Float3 dz = ParticleNoiseField.Curl(p + z, ParticleNoiseQuality.High)
            - ParticleNoiseField.Curl(p - z, ParticleNoiseQuality.High);
        float divergence = (dx.X + dy.Y + dz.Z) / (2f * h);
        Assert.InRange(MathF.Abs(divergence), 0f, 0.01f);
        Assert.InRange(Float3.Length(dx), 0f, 0.5f);
        Assert.InRange(Float3.Length(dy), 0f, 0.5f);
        Assert.InRange(Float3.Length(dz), 0f, 0.5f);
    }

    [Fact]
    public void SpatialCurl_SteadyStateAllocatesNoManagedMemory()
    {
        Float3 p = new(0.23f, 0.47f, 0.81f);
        Float3 sum = Float3.Zero;
        for (int i = 0; i < 1000; i++) sum += ParticleNoiseField.Curl(p, ParticleNoiseQuality.High);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        const int samples = 10000;
        for (int i = 0; i < samples; i++) sum += ParticleNoiseField.Curl(p, ParticleNoiseQuality.High);
        long elapsed = Stopwatch.GetTimestamp() - start;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(float.IsFinite(sum.X));
        _output.WriteLine($"High-quality Perlin curl: {elapsed * 1e9 / Stopwatch.Frequency / samples:F1} ns/sample; {allocated} B/{samples} samples.");
        for (int i = 0; i < 1000; i++) sum += ParticleNoiseField.CurlFiniteDifference(p, ParticleNoiseQuality.High);
        long referenceStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++) sum += ParticleNoiseField.CurlFiniteDifference(p, ParticleNoiseQuality.High);
        long referenceElapsed = Stopwatch.GetTimestamp() - referenceStart;
        _output.WriteLine($"Finite-difference reference, same point/count: {referenceElapsed * 1e9 / Stopwatch.Frequency / samples:F1} ns/sample; analytic speedup {(double)referenceElapsed / elapsed:F2}x.");
        Assert.True(float.IsFinite(sum.Y));
    }

    [Fact]
    public void AnalyticPerlinCurl_PreservesFiniteDifferenceFieldAndTrajectoryWithinMeasuredError()
    {
        float maximum = 0f;
        for (int i = 0; i < 256; i++)
        {
            Float3 p = new(i * 0.137f - 17.2f, (i % 13) * 0.283f - 1.71f, (i % 17) * 0.193f + 0.27f);
            Float3 analytical = ParticleNoiseField.Curl(p, ParticleNoiseQuality.High);
            Float3 reference = ParticleNoiseField.CurlFiniteDifference(p, ParticleNoiseQuality.High);
            maximum = MathF.Max(maximum, Float3.Length(analytical - reference));
        }
        Assert.InRange(maximum, 0f, 0.02f);
        Float3 analyticPosition = new(0.23f, 0.47f, 0.81f);
        Float3 referencePosition = analyticPosition;
        for (int frame = 0; frame < 60; frame++)
        {
            Float3 scroll = new((frame + 1) * 0.1f / 60f);
            analyticPosition += ParticleNoiseField.Curl(analyticPosition + scroll, ParticleNoiseQuality.High) * (0.3f / 60f);
            referencePosition += ParticleNoiseField.CurlFiniteDifference(referencePosition + scroll, ParticleNoiseQuality.High) * (0.3f / 60f);
        }
        float trajectoryError = Float3.Length(analyticPosition - referencePosition);
        Assert.InRange(trajectoryError, 0f, 0.002f);
        _output.WriteLine($"256-point curl max error: {maximum:R}; 60-step strength .3 trajectory error: {trajectoryError:R}.");
    }

    // Unity 6000.7 probe: hovl-simulation-contracts.json, stationary/moving/amount-zero.
    // Same position produces identical noise for seeds 17 and 1e9. Base velocity
    // remains zero or 5; disabling noise stops only its additional motion.
    [Fact]
    public void UnityNoiseContract_UsesSharedSpatialFieldAndPreservesBaseVelocity()
    {
        var module = Spatial();
        Particle first = At(new Float3(0.23f, 0.47f, 0.81f), 17);
        Particle otherSeed = At(first.Position, 1000000000);
        Particle moving = At(first.Position, 17);
        moving.Velocity = new Float3(5f, 0f, 0f);
        module.AdvanceTime(1f / 60f, 0f, 37);
        module.OnParticleUpdate(ref first, 1f / 60f);
        module.OnParticleUpdate(ref otherSeed, 1f / 60f);
        module.OnParticleUpdate(ref moving, 1f / 60f);
        Assert.Equal(first.NoiseVelocity, otherSeed.NoiseVelocity);
        Assert.Equal(first.NoiseVelocity, moving.NoiseVelocity);
        Assert.Equal(5f, moving.Velocity.X - moving.NoiseVelocity.X, 5);
        Assert.True(Float3.Length(first.NoiseVelocity) > 0.0001f);
        module.Enabled = false;
        module.OnParticleUpdate(ref first, 1f / 60f);
        module.OnParticleUpdate(ref moving, 1f / 60f);
        Assert.Equal(Float3.Zero, first.Velocity);
        Assert.Equal(new Float3(5f, 0f, 0f), moving.Velocity);
    }

    [Fact]
    public void UnityNoiseContract_FrequencyTwoDampingHalvesNoiseWithoutCappingBaseSpeed()
    {
        var damped = Spatial();
        var undamped = Spatial();
        damped.Frequency = undamped.Frequency = 2f;
        undamped.Damping = false;
        Particle first = At(new Float3(0.23f, 0.47f, 0.81f), 17);
        Particle second = first;
        damped.OnParticleUpdate(ref first, 1f / 60f);
        undamped.OnParticleUpdate(ref second, 1f / 60f);
        Assert.Equal(first.NoiseVelocity * 2f, second.NoiseVelocity);
    }

    [Fact]
    public void UnityNoiseContract_ZeroPositionAmountProducesNoTranslation()
    {
        var module = Spatial();
        module.PositionAmount = new MinMaxCurve(0f);
        Particle particle = At(new Float3(0.23f, 0.47f, 0.81f), 17);
        module.OnParticleUpdate(ref particle, 1f / 60f);
        Assert.Equal(Float3.Zero, particle.Velocity);
    }

    [Fact]
    public void SpatialNoise_DoesNotAccumulateVelocityAndScrollDoesNotUseLargeSeedAsPhase()
    {
        var module = Spatial();
        module.ScrollSpeed = new MinMaxCurve(0f);
        Particle particle = At(new Float3(0.23f, 0.47f, 0.81f), 1000000000);
        module.OnParticleUpdate(ref particle, 1f / 60f);
        Float3 initial = particle.Velocity;
        for (int i = 0; i < 20; i++) module.OnParticleUpdate(ref particle, 1f / 60f);
        Assert.Equal(initial, particle.Velocity);
        module.ScrollSpeed = new MinMaxCurve(0.1f);
        module.AdvanceTime(0.5f, 0.25f, 1000000000);
        module.OnParticleUpdate(ref particle, 1f / 60f);
        Assert.True(Float3.Length(particle.Velocity - initial) > 0.0001f);
    }

    [Fact]
    public void SpatialNoise_ChangingPositionChangesFieldAndResetReplaysScroll()
    {
        var module = Spatial();
        Particle initial = At(new Float3(0.23f, 0.47f, 0.81f), 17);
        Particle particle = initial;
        Particle shifted = At(initial.Position + Float3.UnitX, 17);
        module.AdvanceTime(0.1f, 0.05f, 37);
        module.OnParticleUpdate(ref particle, 0.1f);
        module.OnParticleUpdate(ref shifted, 0.1f);
        Assert.NotEqual(particle.Velocity, shifted.Velocity);
        module.Reset();
        module.AdvanceTime(0.1f, 0.05f, 37);
        Particle replay = initial;
        module.OnParticleUpdate(ref replay, 0.1f);
        Assert.Equal(particle.Velocity, replay.Velocity);
    }

    private static NoiseModule Spatial() => new()
    {
        Enabled = true,
        UseSpatialNoise = true,
        StrengthCurve = new MinMaxCurve(0.3f),
        Frequency = 1f,
        ScrollSpeed = new MinMaxCurve(0.1f),
    };

    private static Particle At(Float3 position, uint seed) => new()
    {
        Position = position,
        RandomSeed = seed,
        Lifetime = 2f,
        StartLifetime = 2f,
    };
}
