using XEngine.Echo;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class PlayerConstantCurveTests
{
    [Fact]
    public void PackedTrack_MatchesSamplesAndMaterializesForEditing()
    {
        var original = new AnimationCurve { PreLoop = CurveLoopType.CycleOffset, PostLoop = CurveLoopType.Oscillate };
        original.Keys.Clear();
        for (int i = 0; i < 300; i++)
            original.Keys.Add(new KeyFrame(i / 60f, MathF.Sin(i * .1f), .017f, -.031f));
        bool player = Application.IsPlayerBuild;
        try
        {
            Application.IsPlayerBuild = true;
            var loaded = Serializer.Deserialize<AnimationCurve>(Serializer.Serialize(original))!;
            Assert.True(loaded.HasPackedRuntimeKeys);
            for (int i = -100; i < 500; i++)
                Assert.Equal(original.Evaluate(i / 30f), loaded.Evaluate(i / 30f));
            Assert.True(loaded.HasPackedRuntimeKeys);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) loaded.Evaluate(i / 300f);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(300, loaded.Keys.Count);
            Assert.False(loaded.HasPackedRuntimeKeys);
            loaded.Keys[0].Value = 7;
            Assert.Equal(7f, loaded.Evaluate(0));
        }
        finally { Application.IsPlayerBuild = player; }
    }

    [Theory]
    [InlineData(CurveLoopType.Constant)]
    [InlineData(CurveLoopType.Cycle)]
    [InlineData(CurveLoopType.CycleOffset)]
    [InlineData(CurveLoopType.Oscillate)]
    [InlineData(CurveLoopType.Linear)]
    public void PlayerConstantTrack_PreservesSamplesAndLoopEndpoints(CurveLoopType loop)
    {
        var original = new AnimationCurve { PreLoop = loop, PostLoop = loop };
        original.Keys.Clear();
        for (int i = 0; i < 300; i++) original.Keys.Add(new KeyFrame(i / 60f, 0.37f));
        bool player = Application.IsPlayerBuild;
        try
        {
            Application.IsPlayerBuild = true;
            var loaded = Serializer.Deserialize<AnimationCurve>(Serializer.Serialize(original))!;
            Assert.Equal(2, loaded.Keys.Count);
            for (int i = -100; i < 500; i++)
                Assert.Equal(original.Evaluate(i / 30f), loaded.Evaluate(i / 30f), 6);
        }
        finally { Application.IsPlayerBuild = player; }
    }

    [Fact]
    public void PlayerCurvedTrack_RetainsIntermediateTangentOvershoot()
    {
        var original = new AnimationCurve();
        original.Keys.Clear();
        original.Keys.Add(new KeyFrame(0, 1));
        original.Keys.Add(new KeyFrame(1, 1, 2, -2));
        original.Keys.Add(new KeyFrame(2, 1));
        bool player = Application.IsPlayerBuild;
        try
        {
            Application.IsPlayerBuild = true;
            var loaded = Serializer.Deserialize<AnimationCurve>(Serializer.Serialize(original))!;
            Assert.Equal(3, loaded.Keys.Count);
            Assert.Equal(original.Evaluate(.5f), loaded.Evaluate(.5f));
        }
        finally { Application.IsPlayerBuild = player; }
    }
}
