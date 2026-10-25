using XEngine.UiPrototype;

namespace XEngine.UiPrototype.Tests;

public sealed class TreeAndReplayTests
{
    [Fact]
    public void NestedHiddenTogglesMatchFullVisibilityReference()
    {
        int[] parents = [-1, 0, 1, 1, 0, 4, -1, 6];
        string[] names = ["root", "branch", "a", "b", "branch2", "c", "root2", "d"];
        var tree = new TreeDocument(names, parents);
        var expanded = new bool[names.Length];
        Array.Fill(expanded, true);
        var random = new Random(7654);
        for (int step = 0; step < 500; step++)
        {
            int node = random.Next(names.Length);
            bool next = random.Next(2) == 0;
            tree.SetExpanded(node, next);
            if (tree.HasChildren(node)) expanded[node] = next;
            var visible = new bool[names.Length];
            var expected = new List<int>();
            for (int i = 0; i < names.Length; i++)
            {
                visible[i] = parents[i] < 0 || (visible[parents[i]] && expanded[parents[i]]);
                if (visible[i]) expected.Add(i);
            }
            Assert.Equal(expected.Count, tree.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i], tree.GetRow(i).Identity);
                Assert.Equal(names[expected[i]], tree.GetRow(i).Text);
            }
        }
    }

    [Fact]
    public void LargeSubtreeToggleIsBoundedAndDoesNotAllocate()
    {
        var labels = new string[100000];
        var parents = new int[labels.Length];
        Array.Fill(labels, "node");
        for (int i = 0; i < parents.Length; i++) parents[i] = i - 1;
        var tree = new TreeDocument(labels, parents);
        for (int i = 0; i < 1000; i++) tree.SetExpanded(0, i % 2 == 0);
        long start = GC.GetAllocatedBytesForCurrentThread();
        int visits = 0;
        for (int i = 0; i < 5000; i++)
        {
            tree.SetExpanded(0, i % 2 == 0);
            visits = Math.Max(visits, tree.LastUpdatedSegments);
            _ = tree.GetRow(tree.Count - 1);
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(0, bytes);
        Assert.InRange(visits, 1, 70);
        Assert.Equal(1, tree.Count);
        tree.SetExpanded(0, true);
        Assert.Equal(100000, tree.Count);
        Assert.Equal(99999, tree.GetRow(99999).Identity);
    }

    [Fact]
    public void IdentityAndSelectionSurviveVisibleRankChanges()
    {
        var tree = new TreeDocument(["root", "a", "b", "root2"], [-1, 0, 0, -1]);
        tree.Select(3);
        using var view = new RetainedRowViewport(tree, 8);
        view.Prepare(0, 200, 100, 20);
        Assert.True(view.GetVisibleRow(3).Selected);
        tree.SetExpanded(0, false);
        view.Prepare(0, 200, 100, 20);
        Assert.Equal(2, view.VisibleCount);
        Assert.Equal(3, view.GetVisibleRow(1).Identity);
        Assert.True(view.GetVisibleRow(1).Selected);
        Assert.False(view.GetVisibleRow(0).Expanded);
        tree.SetText(3, "edited");
        Assert.Equal(1, view.Prepare(0, 200, 100, 20).RebuiltRows);
    }

    [Fact]
    public void InvalidTopologyIsRejectedAndEmptyTreeHasNoRows()
    {
        Assert.Throws<ArgumentException>(() => new TreeDocument(["a", "b"], [-1]));
        Assert.Throws<ArgumentException>(() => new TreeDocument(["a", "b"], [-1, 1]));
        Assert.Throws<ArgumentException>(() => new TreeDocument(["a", "b", "c"], [-1, -1, 0]));
        var empty = new TreeDocument([], []);
        Assert.Equal(0, empty.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.GetRow(0));
    }

    [Fact]
    public void ReplayProducesSameStateWithSkippedPresentationTicks()
    {
        ReplayEvent[] events = [new(0, ReplayAction.SelectNode, 1), new(2, ReplayAction.ToggleBranch, 0),
            new(2, ReplayAction.SetProperty, 0, Text: "value: 5"), new(3, ReplayAction.SetInputText, Text: "abc测试"),
            new(4, ReplayAction.ScrollTree, Value: 50), new(5, ReplayAction.ScrollProperties, Value: 25),
            new(7, ReplayAction.DragSplit, Value: .7), new(9, ReplayAction.UpdateViewport)];
        static PrototypeWorkload New() => new(new TreeDocument(["root", "child"], [-1, 0]), new RowDocument(["value: 0"]));
        var a = New(); var b = New();
        var oneAtATime = new ReplayTimeline(events); var skipped = new ReplayTimeline(events);
        for (int tick = 0; tick <= 10; tick++) oneAtATime.AdvanceTo(tick, a);
        skipped.AdvanceTo(4, b); skipped.AdvanceTo(10, b);
        Assert.Equal(events.Length, oneAtATime.AppliedCount);
        Assert.Equal(events.Length, skipped.AppliedCount);
        Assert.Equal(a.Tree.Count, b.Tree.Count);
        Assert.Equal(a.Tree.SelectedIdentity, b.Tree.SelectedIdentity);
        Assert.Equal(a.Properties.GetText(0), b.Properties.GetText(0));
        Assert.Equal(a.InputText, b.InputText);
        Assert.Equal(a.TreeScroll, b.TreeScroll);
        Assert.Equal(a.PropertyScroll, b.PropertyScroll);
        Assert.Equal(a.SplitRatio, b.SplitRatio);
        Assert.Equal(a.ViewportVersion, b.ViewportVersion);
        Assert.Equal(0, skipped.AdvanceTo(10, b));
        Assert.Throws<ArgumentOutOfRangeException>(() => skipped.AdvanceTo(9, b));
    }
}
