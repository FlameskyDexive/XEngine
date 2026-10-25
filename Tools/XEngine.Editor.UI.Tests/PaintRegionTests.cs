using XEngine.Editor.UI;

namespace XEngine.Editor.UI.Tests;

public sealed class PaintRegionTests
{
    private sealed class Painter(UiNodeStore nodes) : IUiRegionPainter
    {
        public int Calls;
        public bool Fail, Mutate;
        public UiNodeHandle Reinvalidate;
        public bool TryPaintRegion(in UiPaintRegionWork work)
        {
            Calls++;
            if (!Reinvalidate.IsNull) { nodes.Invalidate(Reinvalidate, UiInvalidation.Paint); Reinvalidate = default; }
            if (Mutate) nodes.SetRootBounds(work.Root, new(0, 0, 200, 200));
            return !Fail;
        }
    }
    private static UiNodeHandle Child(UiNodeStore nodes, UiNodeHandle parent)
    {
        var child = nodes.Create(parent);
        nodes.SetStyle(child, UiStyle.Default with { Width = UiLength.Fixed(20), Height = UiLength.Fixed(20) });
        return child;
    }

    [Fact]
    public void SiblingDirtyControlsCoalesceAndIdleDoesNotAllocate()
    {
        using var nodes = new UiNodeStore(); var root = nodes.Create();
        var a = Child(nodes, root); var b = Child(nodes, root);
        nodes.SetRootBounds(root, new(0, 0, 100, 100)); nodes.Layout();
        using var regions = new UiPaintRegionScheduler(nodes, 2, 8); regions.Register(root);
        var painter = new Painter(nodes);
        var first = regions.Paint(ref painter);
        Assert.Equal(1, first.PaintedRegions); Assert.Equal(3, first.AcknowledgedNodes);
        nodes.Invalidate(a, UiInvalidation.Paint); nodes.Invalidate(b, UiInvalidation.Paint);
        var second = regions.Paint(ref painter);
        Assert.Equal(1, second.PaintedRegions); Assert.Equal(2, second.AcknowledgedNodes);
        for (int i = 0; i < 1000; i++) regions.Paint(ref painter);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) regions.Paint(ref painter);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated); Assert.Equal(default, regions.Paint(ref painter)); Assert.Equal(2, painter.Calls);
        for (int i = 0; i < 1000; i++) { nodes.Invalidate(a, UiInvalidation.Paint); regions.Paint(ref painter); }
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { nodes.Invalidate(a, UiInvalidation.Paint); regions.Paint(ref painter); }
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void FailedAndSupersededRegionWorkRetriesWithoutLosingChildInvalidation()
    {
        using var nodes = new UiNodeStore(); var root = nodes.Create(); var child = Child(nodes, root);
        nodes.SetRootBounds(root, new(0, 0, 100, 100)); nodes.Layout();
        using var regions = new UiPaintRegionScheduler(nodes, 1, 4); regions.Register(root);
        var painter = new Painter(nodes) { Fail = true };
        Assert.Equal(2, regions.Paint(ref painter).PendingNodes);
        painter.Fail = false; painter.Reinvalidate = child;
        Assert.Equal(1, regions.Paint(ref painter).PendingNodes);
        Assert.Equal(1, regions.Paint(ref painter).AcknowledgedNodes);
        Assert.Equal(default, regions.Paint(ref painter));
        nodes.Invalidate(root, UiInvalidation.Paint); painter.Mutate = true;
        Assert.Throws<InvalidOperationException>(() => regions.Paint(ref painter));
        Assert.NotEqual(UiInvalidation.None, nodes.GetInvalidation(root) & UiInvalidation.Paint);
    }

    [Fact]
    public void HiddenDeletedAndNestedRegionsHaveExplicitBoundaries()
    {
        using var nodes = new UiNodeStore(); var root = nodes.Create(); var child = Child(nodes, root);
        nodes.SetRootBounds(root, new(0, 0, 100, 100)); nodes.Layout();
        using var regions = new UiPaintRegionScheduler(nodes, 2, 4); regions.Register(root);
        Assert.Throws<InvalidOperationException>(() => regions.Register(child));
        var painter = new Painter(nodes);
        nodes.SetStyle(root, UiStyle.Default with { Visible = false }); nodes.Layout();
        Assert.Equal(0, regions.Paint(ref painter).PaintedRegions);
        nodes.SetStyle(root, UiStyle.Default); nodes.Layout();
        Assert.Equal(1, regions.Paint(ref painter).PaintedRegions);
        nodes.Remove(root);
        Assert.False(regions.Contains(root));
        Assert.Equal(0, regions.Paint(ref painter).PaintedRegions);
        Assert.True(regions.Unregister(root)); Assert.Equal(0, regions.RegionCount);
    }

    [Fact]
    public void HidingRemovingAndMovingChildrenRepaintsTheirOldRegions()
    {
        using var nodes = new UiNodeStore(); var a = nodes.Create(); var b = nodes.Create();
        nodes.SetRootBounds(a, new(0, 0, 100, 100)); nodes.SetRootBounds(b, new(100, 0, 100, 100));
        var child = Child(nodes, a); nodes.Layout();
        using var regions = new UiPaintRegionScheduler(nodes, 2, 8); regions.Register(a); regions.Register(b);
        var painter = new Painter(nodes); Assert.Equal(2, regions.Paint(ref painter).PaintedRegions);
        nodes.SetStyle(child, nodes.GetStyle(child) with { Visible = false }); nodes.Layout();
        Assert.Equal(1, regions.Paint(ref painter).PaintedRegions);
        nodes.SetStyle(child, nodes.GetStyle(child) with { Visible = true }); nodes.Layout();
        Assert.Equal(1, regions.Paint(ref painter).PaintedRegions);
        nodes.Reparent(child, b); nodes.Layout();
        Assert.Equal(2, regions.Paint(ref painter).PaintedRegions);
        nodes.Remove(child); nodes.Layout();
        Assert.Equal(1, regions.Paint(ref painter).PaintedRegions);
        Assert.Equal(default, regions.Paint(ref painter));
    }

    [Fact]
    public void ReparentingOverlapAndSnapshotOverflowFailBeforeCapture()
    {
        using var nodes = new UiNodeStore(); var a = nodes.Create(); var b = nodes.Create();
        nodes.SetRootBounds(a, new(0, 0, 100, 100)); nodes.SetRootBounds(b, new(0, 0, 100, 100)); nodes.Layout();
        using var regions = new UiPaintRegionScheduler(nodes, 2, 1); regions.Register(a); regions.Register(b);
        var painter = new Painter(nodes);
        nodes.Reparent(b, a); nodes.Layout();
        Assert.Throws<InvalidOperationException>(() => regions.Paint(ref painter)); Assert.Equal(0, painter.Calls);
        regions.Unregister(b); nodes.SetStyle(b, UiStyle.Default with { Width = UiLength.Fixed(20), Height = UiLength.Fixed(20) }); nodes.Layout();
        Assert.Throws<InvalidOperationException>(() => regions.Paint(ref painter)); Assert.Equal(0, painter.Calls);
        Assert.NotEqual(UiInvalidation.None, nodes.GetInvalidation(a) & UiInvalidation.Paint);
        Assert.NotEqual(UiInvalidation.None, nodes.GetInvalidation(b) & UiInvalidation.Paint);
    }
}
