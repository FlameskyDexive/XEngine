using XEngine.Editor.UI;

namespace XEngine.Editor.UI.Tests;

public sealed class PaintSchedulerTests
{
    private struct Painter : IUiNodePainter
    {
        public UiNodeStore Nodes;
        public int Calls;
        public bool Fail, InvalidateOnce, MutateLayout;
        public bool TryPaint(in UiPaintWork work)
        {
            Calls++;
            if (InvalidateOnce) { Nodes.Invalidate(work.Node, UiInvalidation.Paint); InvalidateOnce = false; }
            if (MutateLayout) Nodes.SetRootBounds(work.Node, new(0, 0, 200, 200));
            return !Fail;
        }
    }

    [Fact]
    public void OnlyLocalPaintRunsAndIdlePassDoesNotTraverseOrAllocate()
    {
        using var nodes = new UiNodeStore();
        var a = nodes.Create(); var b = nodes.Create();
        nodes.SetRootBounds(a, new(0, 0, 100, 100)); nodes.SetRootBounds(b, new(0, 0, 100, 100)); nodes.Layout();
        var scheduler = new UiPaintScheduler(nodes); var painter = new Painter { Nodes = nodes };
        Assert.Equal(2, scheduler.Paint(ref painter).PaintedNodes);
        nodes.Invalidate(b, UiInvalidation.Paint);
        Assert.Equal(1, scheduler.Paint(ref painter).PaintedNodes);
        Assert.Equal(3, painter.Calls);
        for (int i = 0; i < 1000; i++) scheduler.Paint(ref painter);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) scheduler.Paint(ref painter);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated); Assert.Equal(default, scheduler.Paint(ref painter));
    }

    [Fact]
    public void FailedAndSupersededCapturesRetainDirtyPaintForRetry()
    {
        using var nodes = new UiNodeStore(); var node = nodes.Create();
        nodes.SetRootBounds(node, new(0, 0, 100, 100)); nodes.Layout();
        var scheduler = new UiPaintScheduler(nodes); var painter = new Painter { Nodes = nodes, Fail = true };
        Assert.Equal(1, scheduler.Paint(ref painter).PendingNodes);
        painter.Fail = false; painter.InvalidateOnce = true;
        Assert.Equal(1, scheduler.Paint(ref painter).PendingNodes);
        Assert.Equal(1, scheduler.Paint(ref painter).PaintedNodes);
        Assert.Equal(default, scheduler.Paint(ref painter));
        Assert.Equal(UiInvalidation.None, nodes.GetInvalidation(node) & UiInvalidation.Paint);
    }

    [Fact]
    public void HiddenAncestorDefersChildrenAndRemovedTicketsCannotAcknowledgeNewNodes()
    {
        using var nodes = new UiNodeStore(); var root = nodes.Create(); var child = nodes.Create(root);
        nodes.SetRootBounds(root, new(0, 0, 100, 100));
        nodes.SetStyle(child, new() { Width = UiLength.Fixed(50), Height = UiLength.Fixed(50) }); nodes.Layout();
        Assert.True(nodes.TryGetPaintWork(child, out var old));
        nodes.SetStyle(root, UiStyle.Default with { Visible = false }); nodes.Layout();
        var scheduler = new UiPaintScheduler(nodes); var painter = new Painter { Nodes = nodes };
        Assert.Equal(new UiPaintMetrics(1, 0, 0), scheduler.Paint(ref painter));
        Assert.Equal(default, scheduler.Paint(ref painter));
        Assert.NotEqual(UiInvalidation.None, nodes.GetInvalidation(child) & UiInvalidation.Paint);
        nodes.SetStyle(root, UiStyle.Default); nodes.Layout();
        Assert.Equal(2, scheduler.Paint(ref painter).PaintedNodes);
        nodes.Remove(child); var replacement = nodes.Create(root); nodes.Layout();
        Assert.False(nodes.AcknowledgePaint(old));
        Assert.NotEqual(UiInvalidation.None, nodes.GetInvalidation(replacement) & UiInvalidation.Paint);
    }

    [Fact]
    public void LayoutMutationDuringCaptureIsRejectedAndRemainsRetryable()
    {
        using var nodes = new UiNodeStore(); var root = nodes.Create(); nodes.SetRootBounds(root, new(0, 0, 100, 100)); nodes.Layout();
        var scheduler = new UiPaintScheduler(nodes); var painter = new Painter { Nodes = nodes, MutateLayout = true };
        Assert.Throws<InvalidOperationException>(() => scheduler.Paint(ref painter));
        nodes.Layout(); painter.MutateLayout = false;
        Assert.Equal(1, scheduler.Paint(ref painter).PaintedNodes);
    }
}
