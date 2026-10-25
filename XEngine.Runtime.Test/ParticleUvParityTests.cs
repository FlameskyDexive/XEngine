using XEngine.Runtime.ParticleSystem;
using XEngine.Runtime.ParticleSystem.Modules;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ParticleUvParityTests
{
    // Unity 6000.7.0a4 BakeMesh probe:
    // docs/superpowers/evidence/rpgvfx/20260906-r1-vertex-probe/unity/hovl-particle-contracts.json
    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.3f)]
    [InlineData(0.6f)]
    public void UnityConstantQuarter_CyclesRepeatTimeWithoutAnimatingAConstant(float time)
    {
        var module = Authored(2, 2);
        module.CycleCount = 2;
        module.FrameOverTimeCurve = new MinMaxCurve(0.25f);
        Particle particle = At(time);
        module.OnParticleUpdate(ref particle, 0.01f);
        Assert.Equal(new Float4(0.5f, 0.5f, 0.5f, 0.5f), module.GetUVTileInfo(particle));
    }

    [Theory]
    [InlineData(0.1f, 0.5f)]
    [InlineData(0.3f, 0f)]
    [InlineData(0.6f, 0.5f)]
    public void UnityLinearTwoCycles_AddsStableStartOffsetAndCountsFromTopRow(float time, float offsetY)
    {
        var module = Authored(2, 2);
        module.CycleCount = 2;
        module.FrameOverTimeCurve = Linear();
        module.StartFrameCurve = new MinMaxCurve(0.25f);
        Particle particle = At(time);
        module.OnParticleUpdate(ref particle, 0.01f);
        Assert.Equal(new Float4(0.5f, offsetY, 0.5f, 0.5f), module.GetUVTileInfo(particle));
    }

    [Theory]
    [InlineData(0.1f, 0f, 0.5f)]
    [InlineData(0.3f, 1f / 3f, 0.5f)]
    [InlineData(0.6f, 0f, 0f)]
    public void UnityThreeByTwoGrid_RetainsIndependentRowAndColumnScale(float time, float offsetX, float offsetY)
    {
        var module = Authored(3, 2);
        module.FrameOverTimeCurve = Linear();
        Particle particle = At(time);
        module.OnParticleUpdate(ref particle, 0.01f);
        Assert.Equal(new Float4(offsetX, offsetY, 1f / 3f, 0.5f), module.GetUVTileInfo(particle));
    }

    [Fact]
    public void HovlRandomStaticTiles_RetainFrameAcrossLifetimeAndDifferAcrossParticles()
    {
        var module = Authored(2, 2);
        module.CycleCount = 4;
        module.FrameOverTimeCurve = new MinMaxCurve(0.9999f);
        module.StartFrameCurve = new MinMaxCurve
        {
            Mode = MinMaxCurveMode.Random,
            MinCurve = new AnimationCurve([new KeyFrame(0f, 0f), new KeyFrame(1f, 0f)]),
            MaxCurve = new AnimationCurve([new KeyFrame(0f, 0.9999f), new KeyFrame(1f, 0.9999f)]),
        };
        int tilesSeen = 0;
        for (uint seed = 1; seed <= 32; seed++)
        {
            Particle particle = At(0f, seed);
            module.OnParticleSpawn(ref particle, new Random(1));
            int first = (int)particle.UVFrame;
            tilesSeen |= 1 << first;
            for (int frame = 1; frame < 60; frame++)
            {
                particle.Lifetime = 1f - frame / 60f;
                module.OnParticleUpdate(ref particle, 1f / 60f);
                Assert.Equal(first, (int)particle.UVFrame);
            }
        }
        Assert.Equal(15, tilesSeen);
    }

    [Fact]
    public void SingleRow_AnimatesColumnsWithoutChangingAuthoredRow()
    {
        var module = Authored(4, 3);
        module.SingleRow = true;
        module.RandomRow = false;
        module.RowIndex = 1;
        module.FrameOverTimeCurve = Linear();
        Particle particle = At(0.6f);
        module.OnParticleUpdate(ref particle, 0.01f);
        Assert.Equal(new Float4(0.5f, 1f / 3f, 0.25f, 1f / 3f), module.GetUVTileInfo(particle));
    }

    [Fact]
    public void AuthoredCurveUpdates_AllocateNothingAfterWarmup()
    {
        var module = Authored(8, 8);
        module.FrameOverTimeCurve = Linear();
        Particle particle = At(0.3f);
        for (int i = 0; i < 4096; i++) module.OnParticleUpdate(ref particle, 1f / 60f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4096; i++) module.OnParticleUpdate(ref particle, 1f / 60f);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static UVModule Authored(int columns, int rows) => new()
    {
        Enabled = true, UseMinMaxCurves = true, TopRowFirst = true,
        TilesX = columns, TilesY = rows,
    };

    private static Particle At(float time, uint seed = 123) => new()
    {
        StartLifetime = 1f, Lifetime = 1f - time, TotalTime = time, RandomSeed = seed,
    };

    private static MinMaxCurve Linear()
    {
        var curve = new AnimationCurve();
        curve.Keys.Add(new KeyFrame(0f, 0f, 1f, 1f));
        curve.Keys.Add(new KeyFrame(1f, 1f, 1f, 1f));
        return new MinMaxCurve { Mode = MinMaxCurveMode.Curve, Curve = curve };
    }
}
