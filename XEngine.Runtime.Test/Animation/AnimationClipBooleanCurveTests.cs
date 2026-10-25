using XEngine.Animation;
using XEngine.Playables;
using Xunit;

namespace XEngine.Runtime.Test.Animation;

public sealed class AnimationClipBooleanCurveTests : RuntimeTestBase
{
    public sealed class BooleanFieldComponent : MonoBehaviour
    {
        public bool Visible;
    }

    private static AnimationClipPlayable Bind(GameObject root, string type, string property, float value)
    {
        var clip = new AnimationClip { Duration = 1f, Wrap = AnimationWrapMode.ClampForever };
        clip.GenericCurves.Add(new AnimationClip.GenericCurve
        {
            TypeName = type, Property = property,
            Curve = AnimationTestHelpers.ConstantCurve(value)
        });
        var playable = new AnimationClipPlayable(clip);
        playable.Bind(SkeletonBinding.Build(root.Transform));
        playable.Play();
        playable.Weight = 1f;
        return playable;
    }

    // Measured against Unity's original Icey BoxCollider2D curve, including a constant-curve
    // threshold probe. Enabled uses abs(value) > 0.001, not a 0.5 rounding threshold.
    [Theory]
    [InlineData(-1f, true)]
    [InlineData(-0.0011f, true)]
    [InlineData(-0.001f, false)]
    [InlineData(0f, false)]
    [InlineData(0.00099f, false)]
    [InlineData(0.001f, false)]
    [InlineData(0.00101f, true)]
    [InlineData(0.5f, true)]
    public void ProcessFrame_BooleanPropertyAndField_UseSourceThreshold(float value, bool expected)
    {
        var root = CreateGameObject();
        var collider = root.AddComponent<BoxCollider2D>();
        var field = root.AddComponent<BooleanFieldComponent>();
        collider.Enabled = !expected;
        field.Visible = !expected;
        var propertyClip = Bind(root, typeof(BoxCollider2D).FullName!, nameof(BoxCollider2D.Enabled), value);
        var fieldClip = Bind(root, typeof(BooleanFieldComponent).FullName!, nameof(BooleanFieldComponent.Visible), value);
        var context = new PlayableContext(0f, 0d, false);
        propertyClip.ProcessFrame(context);
        fieldClip.ProcessFrame(context);
        Assert.Equal(expected, collider.Enabled);
        Assert.Equal(expected, field.Visible);
    }

    [Fact]
    public void ProcessFrame_BooleanBindings_DoNotAllocateAfterWarmup()
    {
        var root = CreateGameObject();
        var collider = root.AddComponent<BoxCollider2D>();
        var field = root.AddComponent<BooleanFieldComponent>();
        var propertyClip = Bind(root, typeof(BoxCollider2D).FullName!, nameof(BoxCollider2D.Enabled), 0f);
        var fieldClip = Bind(root, typeof(BooleanFieldComponent).FullName!, nameof(BooleanFieldComponent.Visible), 1f);
        var context = new PlayableContext(0f, 0d, false);
        for (int i = 0; i < 1000; i++) { propertyClip.ProcessFrame(context); fieldClip.ProcessFrame(context); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { propertyClip.ProcessFrame(context); fieldClip.ProcessFrame(context); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.False(collider.Enabled);
        Assert.True(field.Visible);
    }
}
