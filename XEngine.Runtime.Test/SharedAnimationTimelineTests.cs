using System.Diagnostics;
using System.Reflection;
using XEngine.Echo;
using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test;

public class SharedAnimationTimelineTests(ITestOutputHelper output)
{
    private static AnimationCurve Create(float valueOffset = 0, float timeOffset = 0)
    {
        var curve = new AnimationCurve { PreLoop = CurveLoopType.CycleOffset, PostLoop = CurveLoopType.Linear };
        curve.Keys.Clear();
        for (int i = 0; i < 600; i++)
            curve.Keys.Add(new KeyFrame(i / 60f + timeOffset, MathF.Sin(i * .1f) + valueOffset, i * .002f, -i * .003f));
        return curve;
    }

    private static float[]? Times(AnimationCurve curve) =>
        (float[]?)typeof(AnimationCurve).GetField("_runtimeTimes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(curve);

    [Fact]
    public void ClipRoundTripSharesOnlyExactTimesAndEditingDetaches()
    {
        using var clip = new AnimationClip();
        var original = new AnimationClip.AnimBone {
            BoneName = "bone", PosX = Create(), PosY = Create(10), PosZ = Create(20, .001f) };
        clip.AddBone(original);
        var source = Serializer.Serialize(clip);
        using var loaded = Serializer.Deserialize<AnimationClip>(source)!;
        var b = loaded.Bones[0];
        Assert.NotNull(Times(b.PosX));
        Assert.Same(Times(b.PosX), Times(b.PosY));
        Assert.Null(Times(b.PosZ));
        for (int i = -100; i < 900; i++)
        {
            float time = i / 53f;
            Assert.Equal(original.PosX.Evaluate(time), b.PosX.Evaluate(time));
            Assert.Equal(original.PosY.Evaluate(time), b.PosY.Evaluate(time));
            Assert.Equal(original.PosZ.Evaluate(time), b.PosZ.Evaluate(time));
        }
        var saved = Serializer.Serialize(loaded);
        foreach (string name in new[] { "PosX", "PosY", "PosZ" })
            Assert.Equal((byte[])source.Get("Bones").List[0].Get(name).Get("KeysBinary").Value,
                (byte[])saved.Get("Bones").List[0].Get(name).Get("KeysBinary").Value);
        Assert.NotNull(Times(b.PosX)); // Saving must not expand the editor key API.
        float other = b.PosY.Evaluate(0);
        b.PosX.Keys[0].Value = 999;
        Assert.Null(Times(b.PosX));
        Assert.NotNull(Times(b.PosY));
        Assert.Equal(other, b.PosY.Evaluate(0));
    }

    [Fact]
    public void RepeatedRegistrationOfOneCurveDoesNotRepackIt()
    {
        var curve = Serializer.Deserialize<AnimationCurve>(Serializer.Serialize(Create()))!;
        var pool = new Dictionary<(int, ulong), List<AnimationCurve>>();
        curve.ShareTimeline(pool);
        curve.ShareTimeline(pool);
        Assert.Null(Times(curve));
        Assert.Equal(600, curve.KeyCount);
    }

    [Fact]
    public void SharedSamplingHasZeroSteadyStateAllocations()
    {
        var packed = Serializer.Deserialize<AnimationCurve>(Serializer.Serialize(Create()))!;
        var shared = Serializer.Deserialize<AnimationCurve>(Serializer.Serialize(Create()))!;
        var peer = Serializer.Deserialize<AnimationCurve>(Serializer.Serialize(Create(10)))!;
        var pool = new Dictionary<(int, ulong), List<AnimationCurve>>();
        shared.ShareTimeline(pool); peer.ShareTimeline(pool);
        for (int i = 0; i < 1000; i++) { Sample(packed, 1000); Sample(shared, 1000); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        float checksum = Sample(shared, 100000);
        long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocation);
        Assert.Equal(Sample(packed, 100000), checksum);
        double[] baseline = new double[7], candidate = new double[7];
        for (int i = 0; i < 7; i++)
        {
            if ((i & 1) == 0) { baseline[i] = Measure(packed); candidate[i] = Measure(shared); }
            else { candidate[i] = Measure(shared); baseline[i] = Measure(packed); }
        }
        Array.Sort(baseline); Array.Sort(candidate);
        output.WriteLine($"200k samples median: interleaved={baseline[3]:F3}ms shared={candidate[3]:F3}ms; allocation={allocation}B; checksum={checksum}");
    }

    private static double Measure(AnimationCurve curve)
    {
        long start = Stopwatch.GetTimestamp();
        Sample(curve, 200000);
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static float Sample(AnimationCurve curve, int count)
    {
        float sum = 0;
        for (int i = 0; i < count; i++) sum += curve.Evaluate((i % 599) / 60f + .005f);
        return sum;
    }
}
