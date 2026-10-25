using XEngine.Echo;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class EditorPackedCurveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditorReadAndSave_PreserveAllKeysWithoutMaterializing(bool constant)
    {
        var original = new AnimationCurve { PreLoop = CurveLoopType.CycleOffset, PostLoop = CurveLoopType.Linear };
        original.Keys.Clear();
        for (int i = 0; i < 300; i++)
            original.Keys.Add(new KeyFrame(i / 60f, constant ? 3f : MathF.Sin(i * .1f)));
        bool player = Application.IsPlayerBuild;
        try
        {
            Application.IsPlayerBuild = false;
            var source = Serializer.Serialize(original);
            var loaded = Serializer.Deserialize<AnimationCurve>(source)!;
            Assert.True(loaded.HasPackedRuntimeKeys);
            Assert.Equal(300, loaded.KeyCount);
            for (int i = -100; i < 500; i++)
                Assert.Equal(original.Evaluate(i / 30f), loaded.Evaluate(i / 30f));
            var saved = Serializer.Serialize(loaded);
            Assert.True(loaded.HasPackedRuntimeKeys);
            Assert.Equal((byte[])source.Get("KeysBinary").Value, (byte[])saved.Get("KeysBinary").Value);
            var clone = Serializer.Deserialize<AnimationCurve>(saved)!;
            Assert.True(clone.HasPackedRuntimeKeys);
            Assert.Equal(300, clone.Keys.Count);
            clone.Keys[0].Value = 9f;
            Assert.Equal(9f, clone.Evaluate(0));
            Assert.Equal(original.Evaluate(0), loaded.Evaluate(0));
        }
        finally { Application.IsPlayerBuild = player; }
    }
}
