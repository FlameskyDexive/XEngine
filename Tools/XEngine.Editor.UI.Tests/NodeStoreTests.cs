using XEngine.Editor.UI;

namespace XEngine.Editor.UI.Tests;

public sealed class NodeStoreTests
{
    [Fact]
    public void ReusedSlotRejectsStaleAndForeignHandles()
    {
        using var store = new UiNodeStore(1, 1);
        using var foreign = new UiNodeStore(1, 1);
        var old = store.Create();
        store.Remove(old);
        var current = store.Create();
        Assert.NotEqual(old, current);
        Assert.False(store.IsAlive(old));
        Assert.Throws<ArgumentException>(() => store.Remove(old));
        Assert.Throws<ArgumentException>(() => store.Create(foreign.Create()));
        Assert.True(store.IsAlive(current));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void CapacityFailureDoesNotChangeTreeAndFreedSlotsAreReused()
    {
        using var store = new UiNodeStore(1, 2);
        var root = store.Create();
        var child = store.Create(root);
        Assert.Throws<InvalidOperationException>(() => store.Create(root));
        Assert.Equal(child, store.FirstChild(root));
        Assert.Equal(2, store.Count);
        store.Remove(child);
        var replacement = store.Create(root);
        Assert.Equal(replacement, store.FirstChild(root));
        Assert.True(store.NextSibling(replacement).IsNull);
        Assert.Equal(2, store.Capacity);
    }

    [Fact]
    public void ReparentRejectsCyclesBeforeMutationAndMaintainsSiblingOrder()
    {
        using var store = new UiNodeStore();
        var root = store.Create();
        var a = store.Create(root);
        var b = store.Create(root);
        var leaf = store.Create(a);
        Assert.Throws<ArgumentException>(() => store.Reparent(root, leaf));
        Assert.Equal(a, store.FirstChild(root));
        Assert.Equal(b, store.NextSibling(a));
        store.Reparent(a, root);
        Assert.Equal(b, store.FirstChild(root));
        Assert.Equal(a, store.NextSibling(b));
        store.Reparent(leaf);
        Assert.True(store.Parent(leaf).IsNull);
        Assert.Equal(leaf, store.NextSibling(root));
        Assert.True(store.FirstChild(a).IsNull);
    }

    [Fact]
    public void RemovingSubtreeClearsOnlyItsInputOwnership()
    {
        using var store = new UiNodeStore();
        var root = store.Create();
        var branch = store.Create(root);
        var child = store.Create(branch);
        var sibling = store.Create(root);
        store.SetStyle(child, new() { Focusable = true });
        store.SetFocus(child);
        store.SetCapture(sibling);
        store.Remove(branch);
        Assert.True(store.Focused.IsNull);
        Assert.Equal(sibling, store.Captured);
        Assert.Equal(sibling, store.FirstChild(root));
        Assert.False(store.IsAlive(child));
        Assert.Equal(2, store.Count);
        store.Remove(root);
        Assert.True(store.Captured.IsNull);
        Assert.True(store.FirstRoot.IsNull);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void PaintDoesNotInvalidateLayoutAndSizeDoesNotDirtyOtherRoots()
    {
        using var store = new UiNodeStore();
        var a = store.Create();
        var child = store.Create(a);
        var b = store.Create();
        store.Acknowledge(a, UiInvalidation.All);
        store.Acknowledge(child, UiInvalidation.All);
        store.Acknowledge(b, UiInvalidation.All);
        store.Invalidate(child, UiInvalidation.Paint);
        Assert.Equal(UiInvalidation.None, store.GetInvalidation(a));
        Assert.Equal(UiInvalidation.Paint, store.GetInvalidation(child));
        store.Invalidate(child, UiInvalidation.Measure);
        Assert.True(store.GetInvalidation(a).HasFlag(UiInvalidation.Measure));
        Assert.Equal(UiInvalidation.None, store.GetInvalidation(b));
    }

    [Fact]
    public void DisposeInvalidatesEverythingAndIsIdempotent()
    {
        var store = new UiNodeStore();
        var node = store.Create();
        store.SetStyle(node, new() { Focusable = true });
        store.SetFocus(node);
        store.SetCapture(node);
        store.Dispose();
        store.Dispose();
        Assert.False(store.IsAlive(node));
        Assert.Equal(0, store.Capacity);
        Assert.True(store.Focused.IsNull);
        Assert.True(store.Captured.IsNull);
        Assert.Throws<ObjectDisposedException>(() => store.Create());
    }

    [Fact]
    public void DeepTreeRemovalDoesNotUseCallStack()
    {
        using var store = new UiNodeStore(20_000, 20_000);
        // Build bottom-up to avoid intentionally quadratic ancestor invalidation at construction.
        var root = store.Create();
        for (int i = 1; i < 20_000; i++)
        {
            var parent = store.Create();
            store.Reparent(root, parent);
            root = parent;
        }
        store.Remove(root);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void WarmCreateRemoveAndInvalidationAllocateNoManagedBytes()
    {
        using var store = new UiNodeStore(2, 2);
        var root = store.Create();
        for (int i = 0; i < 10_000; i++) Exercise(store, root);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) Exercise(store, root);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void Exercise(UiNodeStore store, UiNodeHandle root)
    {
        var child = store.CreateControl(UiControlKind.Button, root);
        store.SetFocus(child);
        store.SetCapture(child);
        store.Invalidate(child, UiInvalidation.Measure);
        store.Acknowledge(root, UiInvalidation.All);
        store.Remove(child);
    }
}
