using XEngine.Editor.UI;

namespace XEngine.Editor.UI.Tests;

public sealed class LayoutTests
{
    [Fact]
    public void ColumnPaddingGapStretchAndScrollHaveExpectedGeometry()
    {
        using var store = new UiNodeStore();
        var root = store.Create();
        store.SetRootBounds(root, new(10, 20, 200, 120));
        store.SetStyle(root, new() { Padding = 10, Gap = 5 });
        var a = store.Create(root);
        var b = store.Create(root);
        store.SetStyle(a, new() { Width = UiLength.Stretch(), Height = UiLength.Fixed(25) });
        store.SetStyle(b, new() { Width = UiLength.Stretch(), Height = UiLength.Stretch() });
        Assert.Equal(new UiLayoutMetrics(3, 3), store.Layout());
        Assert.Equal(new UiRect(20, 30, 180, 25), store.GetBounds(a));
        Assert.Equal(new UiRect(20, 60, 180, 70), store.GetBounds(b));
        Assert.Equal(b, store.HitTest(30, 70));
        Assert.Equal(root, store.HitTest(15, 25));
        Assert.Equal(default, store.HitTest(210, 140));
        Assert.Equal(default, store.Layout());
        store.SetScroll(root, 0, 15);
        store.Layout();
        Assert.Equal(new UiRect(20, 15, 180, 25), store.GetBounds(a));
        Assert.Equal(new UiRect(20, 30, 180, 10), store.GetClip(a));
    }

    [Fact]
    public void PaintOnlyDoesNotLayoutAndSeparatePanelIsNotVisited()
    {
        using var store = new UiNodeStore();
        var a = store.Create();
        var field = store.Create(a);
        var b = store.Create();
        for (int i = 0; i < 1000; i++) store.Create(b);
        store.SetRootBounds(a, new(0, 0, 200, 200));
        store.SetRootBounds(b, new(200, 0, 200, 200));
        store.Layout();
        store.Invalidate(field, UiInvalidation.Paint);
        Assert.Equal(default, store.Layout());
        store.SetContentSize(field, new(50, 20));
        Assert.Equal(new UiLayoutMetrics(2, 2), store.Layout());
    }

    [Fact]
    public void HiddenAncestorCannotBeHitAndReappearsCorrectly()
    {
        using var store = new UiNodeStore();
        var root = store.Create();
        var panel = store.Create(root);
        var child = store.Create(panel);
        store.SetRootBounds(root, new(0, 0, 100, 100));
        store.SetStyle(panel, new() { Width = UiLength.Stretch(), Height = UiLength.Stretch() });
        store.SetStyle(child, new() { Width = UiLength.Stretch(), Height = UiLength.Stretch(), Focusable = true });
        store.Layout();
        Assert.Equal(child, store.HitTest(20, 20));
        store.SetFocus(child);
        store.SetCapture(child);
        var style = store.GetStyle(panel);
        store.SetStyle(panel, style with { Visible = false });
        store.Layout();
        Assert.True(store.Focused.IsNull);
        Assert.True(store.Captured.IsNull);
        Assert.Equal(root, store.HitTest(20, 20));
        Assert.Equal(0, store.GetClip(child).Width);
        store.SetStyle(panel, style);
        store.Layout();
        Assert.Equal(child, store.HitTest(20, 20));
    }

    [Fact]
    public void IncrementalMatchesUncachedReferenceAcrossDeterministicMutations()
    {
        using var incremental = new UiNodeStore();
        using var reference = new UiNodeStore();
        var a = new UiNodeHandle[100];
        var b = new UiNodeHandle[100];
        var random = new Random(18417);
        for (int i = 0; i < a.Length; i++)
        {
            int parent = i < 3 ? -1 : random.Next(i);
            a[i] = incremental.Create(parent < 0 ? default : a[parent]);
            b[i] = reference.Create(parent < 0 ? default : b[parent]);
            if (parent < 0)
            {
                incremental.SetRootBounds(a[i], new(i * 300, 0, 300, 600));
                reference.SetRootBounds(b[i], new(i * 300, 0, 300, 600));
            }
        }
        for (int step = 0; step < 1000; step++)
        {
            int index = random.Next(a.Length);
            var style = new UiStyle { Flow = (UiFlow)random.Next(3), Padding = random.Next(5), Gap = random.Next(5),
                Width = random.Next(2) == 0 ? UiLength.Stretch() : UiLength.Fixed(random.Next(100)),
                Height = random.Next(2) == 0 ? UiLength.Stretch() : UiLength.Fixed(random.Next(100)), Visible = random.Next(5) != 0 };
            incremental.SetStyle(a[index], style);
            reference.SetStyle(b[index], style);
            var size = new UiSize(random.Next(100), random.Next(100));
            incremental.SetContentSize(a[index], size);
            reference.SetContentSize(b[index], size);
            if (step % 7 == 0)
            {
                int parent = random.Next(index + 1) - 1;
                if (index >= 3)
                {
                    incremental.Reparent(a[index], parent < 0 ? default : a[parent]);
                    reference.Reparent(b[index], parent < 0 ? default : b[parent]);
                }
            }
            incremental.Layout(); reference.Layout(forceFull: true);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.Equal(reference.GetDesiredSize(b[i]), incremental.GetDesiredSize(a[i]));
                Assert.Equal(reference.GetBounds(b[i]), incremental.GetBounds(a[i]));
                Assert.Equal(reference.GetClip(b[i]), incremental.GetClip(a[i]));
            }
            int x = random.Next(900), y = random.Next(600);
            var hitA = incremental.HitTest(x, y);
            var hitB = reference.HitTest(x, y);
            Assert.Equal(Array.IndexOf(b, hitB), Array.IndexOf(a, hitA));
            Assert.Equal(default, incremental.Layout());
        }
    }

    [Fact]
    public void WarmLayoutAndHitTestingAllocateNoManagedBytes()
    {
        using var store = new UiNodeStore();
        var root = store.Create();
        var child = store.Create(root);
        store.SetRootBounds(root, new(0, 0, 200, 200));
        for (int i = 0; i < 10_000; i++) Step(store, child, i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) Step(store, child, i);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void Step(UiNodeStore store, UiNodeHandle child, int step)
    {
        store.SetContentSize(child, new(50, 10 + step % 10));
        store.Layout();
        store.HitTest(5, 5);
    }
}
