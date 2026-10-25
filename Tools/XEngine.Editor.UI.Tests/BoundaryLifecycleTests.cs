using System.Runtime.CompilerServices;
using XEngine.Editor.UI;

namespace XEngine.Editor.UI.Tests;

public sealed class BoundaryLifecycleTests
{
    [Fact]
    public void DefinedEnumValidationRemainsAllocationFreeAfterDiagnosticCollection()
    {
        using var store = new UiNodeStore(2, 2);
        var root = store.Create();
        for (int i = 0; i < 10000; i++) CreateStyledControl(store, root);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        CreateStyledControl(store, root);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ClosedEnumBoundsRejectUndefinedValuesBeforeMutation()
    {
        using var store = new UiNodeStore();
        var root = store.Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.CreateControl((UiControlKind)255));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetStyle(root, new() { Flow = (UiFlow)255 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetStyle(root, new() { Position = (UiPosition)255 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetStyle(root, new() { Width = new((UiSizing)255) }));
        Assert.Equal(1, store.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateStyledControl(UiNodeStore store, UiNodeHandle root)
    {
        var child = store.CreateControl(UiControlKind.Button, root);
        store.SetStyle(child, store.GetStyle(child) with { Width = UiLength.Stretch(), Padding = 2 });
        store.Remove(child);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.MaxValue)]
    [InlineData(-1)]
    public void InvalidSizesAreRejectedWithoutMutatingLayout(float value)
    {
        using var store = new UiNodeStore();
        var root = store.Create();
        store.SetRootBounds(root, new(0, 0, 100, 100));
        store.Layout();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetContentSize(root, new(value, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetScroll(root, value, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetStyle(root, new() { Padding = value }));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetRootBounds(root, new(0, 0, value, 100)));
        Assert.Equal(default, store.Layout());
        Assert.Equal(new UiRect(0, 0, 100, 100), store.GetBounds(root));
    }

    [Fact]
    public void DominantWeightSaturationDoesNotLoseSmallRemainingWeight()
    {
        using var store = new UiNodeStore();
        var root = store.Create();
        store.SetRootBounds(root, new(0, 0, 100, 300));
        var a = store.Create(root); var b = store.Create(root);
        store.SetStyle(a, new() { Height = UiLength.Stretch(1e6f), MaxHeight = 10 });
        store.SetStyle(b, new() { Height = UiLength.Stretch(1e-6f) });
        store.Layout();
        Assert.Equal(10, store.GetBounds(a).Height);
        Assert.Equal(290, store.GetBounds(b).Height);
    }

    [Fact]
    public void RepeatedPanelLifecycleKeepsCapacityAndInvalidatesEveryOldHandle()
    {
        using var store = new UiNodeStore(64, 64);
        var root = store.Create();
        store.SetRootBounds(root, new(0, 0, 800, 600));
        UiNodeHandle stale = default;
        for (int cycle = 0; cycle < 2000; cycle++)
        {
            var panel = store.Create(root);
            for (int i = 0; i < 30; i++)
            {
                var button = store.CreateControl(UiControlKind.Button, panel);
                store.SetContentSize(button, new(100, 20));
                if (i == 29) { store.SetFocus(button); store.SetCapture(button); stale = button; }
            }
            store.Layout();
            store.Remove(panel);
            store.Layout();
            Assert.False(store.IsAlive(stale));
            Assert.True(store.Focused.IsNull);
            Assert.True(store.Captured.IsNull);
            Assert.Equal(1, store.Count);
            Assert.Equal(64, store.Capacity);
        }
    }

    [Fact]
    public void RetainedHandleDoesNotKeepNodeStoreAlive()
    {
        var (handle, weak) = MakeStore();
        // Explicit diagnostic collection, never a frame-loop optimization.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(weak.IsAlive);
        GC.KeepAlive(handle);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (UiNodeHandle, WeakReference) MakeStore()
    {
        var store = new UiNodeStore();
        return (store.Create(), new WeakReference(store));
    }
}
