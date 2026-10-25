using XEngine.Editor.UI;

namespace XEngine.Editor.UI.Tests;

public sealed class LayoutConstraintTests
{
    [Fact]
    public void FixedPanelContainsSizeInvalidationWhileScrollOnlyArrangesItsBranch()
    {
        using var store = new UiNodeStore();
        var root = store.Create();
        store.SetRootBounds(root, new(0, 0, 800, 600));
        var panel = store.Create(root);
        store.SetStyle(panel, new() { Width = UiLength.Fixed(200), Height = UiLength.Fixed(200) });
        var field = store.Create(panel);
        var otherPanel = store.Create(root);
        store.SetStyle(otherPanel, new() { Width = UiLength.Fixed(200), Height = UiLength.Fixed(200) });
        var otherField = store.Create(otherPanel);
        store.SetContentSize(otherField, new(100, 20));
        store.Layout();
        store.SetContentSize(field, new(100, 30));
        Assert.Equal(new UiLayoutMetrics(2, 2), store.Layout());
        var fieldBounds = store.GetBounds(field);
        var otherBounds = store.GetBounds(otherField);
        store.Layout(true);
        Assert.Equal(fieldBounds, store.GetBounds(field));
        Assert.Equal(otherBounds, store.GetBounds(otherField));
        store.SetScroll(panel, 0, 5);
        Assert.Equal(new UiLayoutMetrics(0, 2), store.Layout());
        Assert.Equal(default, store.Layout());
        Assert.Equal(otherBounds, store.GetBounds(otherField));
        // Changing the boundary's own dimensions must still invalidate its parent.
        store.SetStyle(panel, store.GetStyle(panel) with { Height = UiLength.Fixed(250) });
        store.Layout();
        Assert.Equal(250, store.GetBounds(otherPanel).Y);
    }

    [Theory]
    [InlineData(300, 0, 50, 0, 1000, 50, 250)]
    [InlineData(300, 220, 1000, 0, 1000, 220, 80)]
    [InlineData(100, 80, 1000, 60, 1000, 80, 60)]
    [InlineData(300, 0, 30, 0, 40, 30, 40)]
    public void StretchRedistributesAfterClampingAndPreservesUnavoidableOverflow(
        float available, float minA, float maxA, float minB, float maxB, float expectedA, float expectedB)
    {
        using var store = new UiNodeStore();
        var root = store.Create();
        store.SetStyle(root, new() { Flow = UiFlow.Row });
        store.SetRootBounds(root, new(0, 0, available, 100));
        var a = store.Create(root); var b = store.Create(root);
        store.SetStyle(a, new() { Width = UiLength.Stretch(), Height = UiLength.Stretch(), MinWidth = minA, MaxWidth = maxA });
        store.SetStyle(b, new() { Width = UiLength.Stretch(), Height = UiLength.Stretch(), MinWidth = minB, MaxWidth = maxB });
        store.Layout();
        Assert.Equal(expectedA, store.GetBounds(a).Width);
        Assert.Equal(expectedB, store.GetBounds(b).Width);
        Assert.Equal(expectedA, store.GetBounds(b).X);
        Assert.True(float.IsFinite(store.GetBounds(b).Width));
    }

    [Fact]
    public void OpposingMinAndMaxViolationsAreNotFrozenTogether()
    {
        using var store = new UiNodeStore();
        var root = store.Create();
        store.SetRootBounds(root, new(0, 0, 100, 300));
        var a = store.Create(root); var b = store.Create(root); var c = store.Create(root);
        store.SetStyle(a, new() { Height = UiLength.Stretch(), MinHeight = 160 });
        store.SetStyle(b, new() { Height = UiLength.Stretch(), MaxHeight = 90 });
        store.SetStyle(c, new() { Height = UiLength.Stretch() });
        store.Layout();
        Assert.Equal(160, store.GetBounds(a).Height);
        Assert.Equal(70, store.GetBounds(b).Height);
        Assert.Equal(70, store.GetBounds(c).Height);
    }

    [Fact]
    public void AbsoluteChildDoesNotConsumeFlowSpaceOrIntrinsicSize()
    {
        using var store = new UiNodeStore();
        var root = store.Create();
        store.SetStyle(root, new() { Padding = 10, Gap = 5 });
        store.SetRootBounds(root, new(0, 0, 200, 200));
        var flow = store.Create(root); var absolute = store.Create(root); var second = store.Create(root);
        store.SetContentSize(flow, new(30, 20)); store.SetContentSize(second, new(30, 20));
        store.SetStyle(absolute, new() { Position = UiPosition.Absolute, Left = 50, Top = -5,
            Width = UiLength.Stretch(), Height = UiLength.Fixed(100) });
        store.Layout();
        Assert.Equal(new UiSize(50, 65), store.GetDesiredSize(root));
        Assert.Equal(new UiRect(10, 35, 30, 20), store.GetBounds(second));
        Assert.Equal(new UiRect(60, 5, 130, 100), store.GetBounds(absolute));
        Assert.Equal(new UiRect(60, 10, 130, 95), store.GetClip(absolute));
        Assert.Equal(absolute, store.HitTest(70, 20));
    }
}
