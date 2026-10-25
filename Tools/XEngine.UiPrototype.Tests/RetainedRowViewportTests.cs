using XEngine.UiPrototype;

namespace XEngine.UiPrototype.Tests;

public sealed class RetainedRowViewportTests
{
    private static RowDocument Document(int count)
    {
        var text = new string[count];
        for (int i = 0; i < count; i++) text[i] = "Row " + i;
        return new RowDocument(text);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(1000)] [InlineData(100000)]
    public void VisibleRowsAndHitTestsMatchFullReference(int count)
    {
        var model = Document(count);
        using var view = new RetainedRowViewport(model, 32);
        foreach (double requested in new[] { -2, 0, .5, 13.5, 9999999 })
        {
            view.Prepare(requested, 200, 100, 10);
            var expected = new List<int>();
            double offset = Math.Clamp(requested, 0, Math.Max(0, count * 10.0 - 100));
            for (int i = 0; i < count; i++)
                if ((i + 1) * 10 > offset && i * 10 < offset + 100) expected.Add(i);
            Assert.Equal(expected.Count, view.VisibleCount);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i], view.GetVisibleRow(i).SourceIndex);
                Assert.Equal(expected[i] * 10 - offset, view.GetRowTop(i));
            }
            for (double y = -1; y <= 101; y += .5)
            {
                int expectedRow = (int)Math.Floor((offset + y) / 10);
                bool hit = y >= 0 && y < 100 && expectedRow >= 0 && expectedRow < count;
                Assert.Equal(hit, view.TryHitTest(y, out var row));
                if (hit) Assert.Equal(expectedRow, row);
            }
        }
    }

    [Fact]
    public void IdleAndHiddenEditsDoNotRebuildVisibleRows()
    {
        var model = Document(100000);
        using var view = new RetainedRowViewport(model, 128);
        Assert.Equal(100, view.Prepare(0, 200, 1000, 10).RebuiltRows);
        Assert.Equal(default, view.Prepare(0, 200, 1000, 10));
        model.SetText(99999, "hidden edit");
        var hidden = view.Prepare(0, 200, 1000, 10);
        Assert.Equal(100, hidden.InspectedRows);
        Assert.Equal(0, hidden.RebuiltRows);
        model.SetText(5, "visible edit");
        Assert.Equal(1, view.Prepare(0, 200, 1000, 10).RebuiltRows);
        Assert.Equal("visible edit", view.GetVisibleRow(5).Text);
    }

    [Fact]
    public void ScrollReusesRowsAndSelectionInvalidatesOnlyChangedVisuals()
    {
        var model = Document(1000);
        using var view = new RetainedRowViewport(model, 16);
        view.Prepare(0, 200, 100, 10);
        Assert.Equal(1, view.Prepare(10, 200, 100, 10).RebuiltRows);
        model.Select(2);
        Assert.Equal(1, view.Prepare(10, 200, 100, 10).RebuiltRows);
        model.Select(3);
        Assert.Equal(2, view.Prepare(10, 200, 100, 10).RebuiltRows);
        Assert.True(view.GetVisibleRow(2).Selected);
        Assert.Equal(10, view.Prepare(10, 220, 100, 10).RebuiltRows);
    }

    [Fact]
    public void BoundedCacheRejectsOversizedViewportAndDisposedUse()
    {
        var model = Document(1000);
        var view = new RetainedRowViewport(model, 10);
        view.Prepare(0, 100, 100, 10);
        Assert.Throws<InvalidOperationException>(() => view.Prepare(.5, 100, 100, 10));
        Assert.Equal(10, view.VisibleCount);
        view.Dispose();
        Assert.Throws<ObjectDisposedException>(() => view.Prepare(0, 100, 100, 10));
        Assert.Throws<ObjectDisposedException>(() => view.GetVisibleRow(0));
    }

    [Fact]
    public void WarmScrollAndSelectionPathDoesNotAllocateManagedMemory()
    {
        var model = Document(100000);
        using var view = new RetainedRowViewport(model, 128);
        for (int i = 0; i < 2000; i++) { model.Select(i % 1000); view.Prepare(i % 1000, 200, 1000, 10); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        int inspected = 0;
        for (int i = 0; i < 5000; i++)
        {
            model.Select(i % 1000);
            inspected += view.Prepare(i % 1000, 200, 1000, 10).InspectedRows;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.InRange(inspected, 1, 505000);
    }
}
