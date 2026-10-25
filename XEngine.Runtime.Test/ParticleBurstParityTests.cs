using XEngine.Runtime.ParticleSystem;
using XEngine.Runtime.ParticleSystem.Modules;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ParticleBurstParityTests : RuntimeTestBase
{
    public static TheoryData<float, int, int[]> FiniteUnityBursts => new()
    {
        { 0.01f, 1, [3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3] },
        { 0.01f, 3, [3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3] },
        { 0.01f, 5, [3, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6] },
        { 0.02f, 1, [3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3] },
        { 0.02f, 3, [3, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6] },
        { 0.02f, 5, [3, 6, 9, 12, 12, 12, 12, 12, 12, 12, 12, 12] },
        { 0.05f, 1, [3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3] },
        { 0.05f, 3, [3, 3, 6, 6, 6, 9, 9, 9, 9, 9, 9, 9] },
        { 0.05f, 5, [3, 3, 6, 6, 6, 9, 9, 12, 12, 12, 15, 15] },
    };

    [Theory]
    [MemberData(nameof(FiniteUnityBursts))]
    public void FiniteRepeats_DropCrossedEventsWithoutExtendingTheBurstTrain(float interval, int cycles, int[] cumulativeCounts)
    {
        var emission = new EmissionModule { Enabled = true, UseUnityBurstScheduling = true };
        emission.Bursts.Add(new ParticleBurst(0f, 3) { CycleCount = cycles, RepeatInterval = interval });
        var random = new Random(1);
        float time = 0f;
        int count = 0;
        for (int frame = 0; frame < cumulativeCounts.Length; frame++)
        {
            time += 0.02f;
            count += emission.CalculateEmitCount(0.02f, time, random, 5f);
            Assert.Equal(cumulativeCounts[frame], count);
        }
        emission.Reset();
        Assert.Equal(3, emission.CalculateEmitCount(0.02f, 0.02f, random, 5f));
    }

    // Unity 6000.7.0a4, explicit 1/60 steps with fixedTimeStep=false:
    // ../docs/superpowers/evidence/rpgvfx/20260906-r1-vertex-probe/unity/hovl-simulation-contracts.json
    [Theory]
    [InlineData(12, 36)]
    [InlineData(36, 87)]
    [InlineData(72, 87)]
    public void UnityRepeatFasterThanSimulationStep_MatchesReferenceParticleCounts(int frames, int expected)
    {
        using var go = new GameObject("UnityRepeatedBurst");
        var system = go.AddComponent<ParticleSystemComponent>();
        system.PlayOnEnable = false;
        system.Duration = 5f;
        system.Looping = false;
        system.RandomSeed = 1234;
        system.Initial.StartLifetime = new MinMaxCurve(0.5f);
        system.Initial.StartSpeed = new MinMaxCurve(0f);
        system.Emission.ShapeEnabled = false;
        system.Emission.UseUnityBurstScheduling = true;
        system.Emission.Bursts.Add(new ParticleBurst(0f, 3) { CycleCount = 400, RepeatInterval = 0.01f });
        system.Play();
        for (int frame = 0; frame < frames; frame++) system.Simulate(1f / 60f);
        Assert.Equal(expected, system.ParticleCount);
    }

    [Fact]
    public void UnityStepCap_IsPerBurstEntryAndPreservesIndependentBursts()
    {
        var emission = new EmissionModule { Enabled = true, UseUnityBurstScheduling = true };
        emission.Bursts.Add(new ParticleBurst(0f, 3) { CycleCount = 400, RepeatInterval = 0.01f });
        emission.Bursts.Add(new ParticleBurst(0.01f, 2));
        var random = new Random(1);
        Assert.Equal(5, emission.CalculateEmitCount(1f / 60f, 1f / 60f, random));
        Assert.Equal(3, emission.CalculateEmitCount(1f / 60f, 2f / 60f, random));
    }
}
