using XEngine.Editor.UI;

namespace XEngine.Editor.UI.Tests;

public sealed class ControlTests
{
    [Fact]
    public void ExternalCaptureTransferCancelsPressedGestureAndInvalidatesItsVisual()
    {
        using var store = new UiNodeStore();
        var a = store.CreateControl(UiControlKind.Button);
        var b = store.CreateControl(UiControlKind.Button);
        store.SetRootBounds(a, new(0, 0, 100, 20));
        store.SetRootBounds(b, new(0, 30, 100, 20));
        store.Layout();
        store.PointerDown(5, 5);
        store.Acknowledge(a, UiInvalidation.Paint);
        store.SetCapture(b);
        Assert.False(store.IsPressed(a));
        Assert.True(store.GetInvalidation(a).HasFlag(UiInvalidation.Paint));
        store.SetCapture(a);
        Assert.False(store.IsPressed(a));
        Assert.Equal(default, store.PointerUp(5, 5));
        store.SetCapture(default);
        store.PointerDown(5, 5);
        store.SetCapture(a); // Keeping the same capture preserves this new gesture.
        Assert.True(store.IsPressed(a));
        Assert.Equal(new UiAction(UiActionKind.Activated, a), store.PointerUp(5, 5));
    }

    [Fact]
    public void ButtonActivatesOnlyOnReleaseInsideAndCaptureCanBeCanceled()
    {
        using var store = new UiNodeStore();
        var button = store.CreateControl(UiControlKind.Button);
        store.SetRootBounds(button, new(0, 0, 100, 20));
        store.Layout();
        Assert.Equal(default, store.PointerDown(5, 5));
        Assert.Equal(button, store.Focused);
        Assert.True(store.IsPressed(button));
        Assert.Equal(default, store.PointerUp(110, 5));
        Assert.False(store.IsPressed(button));
        store.PointerDown(5, 5);
        Assert.Equal(new UiAction(UiActionKind.Activated, button), store.PointerUp(10, 5));
        Assert.Equal(default, store.PointerUp(10, 5));
        store.PointerDown(5, 5);
        store.KeyDown(UiKey.Escape);
        Assert.Equal(default, store.PointerUp(10, 5));
        Assert.Equal(default, store.Layout());
    }

    [Fact]
    public void ToggleAndSliderProduceActionsWithoutLayoutInvalidation()
    {
        using var store = new UiNodeStore();
        var toggle = store.CreateControl(UiControlKind.Toggle);
        var slider = store.CreateControl(UiControlKind.Slider);
        store.SetRootBounds(toggle, new(0, 0, 100, 20));
        store.SetRootBounds(slider, new(0, 30, 100, 20));
        store.Layout();
        store.PointerDown(5, 5);
        Assert.Equal(new UiAction(UiActionKind.ValueChanged, toggle, 1), store.PointerUp(5, 5));
        Assert.Equal(default, store.KeyDown(UiKey.Space, repeat: true));
        Assert.Equal(new UiAction(UiActionKind.ValueChanged, toggle, 0), store.KeyDown(UiKey.Space));
        Assert.Equal(new UiAction(UiActionKind.ValueChanged, slider, .25f), store.PointerDown(25, 35));
        Assert.Equal(new UiAction(UiActionKind.ValueChanged, slider, 1), store.PointerMove(150, 200));
        Assert.Equal(new UiAction(UiActionKind.ValueChanged, slider, 0), store.PointerUp(-10, 200));
        Assert.True(store.Captured.IsNull);
        Assert.Equal(new UiAction(UiActionKind.ValueChanged, slider, .01f), store.KeyDown(UiKey.Right));
        Assert.Equal(default, store.Layout());
    }

    [Fact]
    public void TabTraversalWrapsAndSkipsHiddenDisabledSubtrees()
    {
        using var store = new UiNodeStore();
        var root = store.Create();
        var a = store.CreateControl(UiControlKind.Button, root);
        var hidden = store.Create(root);
        store.CreateControl(UiControlKind.Button, hidden);
        store.SetStyle(hidden, new() { Visible = false });
        var disabled = store.Create(root);
        var disabledButton = store.CreateControl(UiControlKind.Button, disabled);
        store.SetStyle(disabled, new() { Enabled = false });
        var b = store.CreateControl(UiControlKind.Toggle, root);
        store.KeyDown(UiKey.Tab); Assert.Equal(a, store.Focused);
        store.KeyDown(UiKey.Tab); Assert.Equal(b, store.Focused);
        store.KeyDown(UiKey.Tab); Assert.Equal(a, store.Focused);
        store.KeyDown(UiKey.Tab, shift: true); Assert.Equal(b, store.Focused);
        Assert.Throws<ArgumentException>(() => store.SetFocus(disabledButton));
        Assert.Throws<ArgumentException>(() => store.SetCapture(disabledButton));
        Assert.Throws<ArgumentException>(() => store.SetFocus(root));
    }

    [Fact]
    public void DisablingOrReparentingCapturedControlCannotActivateItLater()
    {
        using var store = new UiNodeStore();
        var a = store.CreateControl(UiControlKind.Button);
        var disabled = store.Create();
        store.SetRootBounds(a, new(0, 0, 100, 20));
        store.SetStyle(disabled, new() { Enabled = false });
        store.Layout();
        store.PointerDown(5, 5);
        store.Reparent(a, disabled);
        Assert.True(store.Focused.IsNull);
        Assert.True(store.Captured.IsNull);
        Assert.Equal(default, store.PointerUp(5, 5));
        store.Reparent(a);
        store.Layout();
        store.PointerDown(5, 5);
        store.Remove(a);
        var replacement = store.CreateControl(UiControlKind.Button);
        store.SetRootBounds(replacement, new(0, 0, 100, 20));
        store.Layout();
        Assert.Equal(default, store.PointerUp(5, 5));
        Assert.False(store.IsPressed(replacement));
    }
}
