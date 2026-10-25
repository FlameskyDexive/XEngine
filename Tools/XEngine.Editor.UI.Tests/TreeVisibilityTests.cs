namespace XEngine.Editor.UI.Tests;

public sealed class TreeVisibilityTests
{
    [Fact]
    public void RandomNestedFoldsMatchIndependentAncestorWalk()
    {
        var parents = new List<int>();
        void Add(int parent, int depth)
        { int index = parents.Count; parents.Add(parent); if (depth > 0) { Add(index, depth - 1); Add(index, depth - 1); } }
        Add(-1, 7); Add(-1, 5);
        var data = parents.ToArray(); var index = new UiTreeVisibilityIndex(data);
        var expanded = new bool[data.Length]; Array.Fill(expanded, true);
        var random = new Random(72413);
        for (int operation = 0; operation < 1200; operation++)
        {
            int node = random.Next(data.Length); bool state = random.Next(2) != 0;
            expanded[node] = state; index.SetExpanded(node, state);
            int rank = 0;
            for (int i = 0; i < data.Length; i++)
            {
                bool visible = true;
                for (int p = data[i]; p >= 0; p = data[p]) if (!expanded[p]) { visible = false; break; }
                if (visible) { Assert.Equal(rank, index.GetVisibleRank(i)); Assert.Equal(i, index.GetNodeIndex(rank++)); }
                else Assert.Equal(-1, index.GetVisibleRank(i));
            }
            Assert.Equal(rank, index.VisibleCount);
        }
    }

    [Fact]
    public void HiddenDescendantStateSurvivesAncestorReopen()
    {
        var index = new UiTreeVisibilityIndex([-1, 0, 1, 0, -1]);
        index.SetExpanded(0, false); index.SetExpanded(1, false);
        Assert.Equal(2, index.VisibleCount);
        index.SetExpanded(0, true);
        Assert.Equal(4, index.VisibleCount); Assert.Equal(3, index.GetNodeIndex(2));
        index.SetExpanded(1, true);
        Assert.Equal(5, index.VisibleCount);
    }

    [Fact]
    public void HundredThousandNodesWarmQueriesAndFoldUpdatesAllocateNothing()
    {
        var parents = new int[100_000]; Array.Fill(parents, 0); parents[0] = -1;
        var index = new UiTreeVisibilityIndex(parents);
        for (int i = 0; i < 1000; i++) { index.SetExpanded(0, false); index.SetExpanded(0, true); _ = index.GetNodeIndex(i); _ = index.GetVisibleRank(i); }
        long before = GC.GetAllocatedBytesForCurrentThread(); long sum = 0;
        for (int i = 0; i < 1000; i++) { index.SetExpanded(0, false); index.SetExpanded(0, true); sum += index.GetNodeIndex(index.GetVisibleRank(i * 97)); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, bytes); Assert.Equal(48_451_500, sum);
    }

    [Fact]
    public void VisibleDescendantCountsMatchAncestorWalkUnderRandomFolds()
    {
        var parents = new List<int>();
        void Add(int parent, int depth)
        { int index = parents.Count; parents.Add(parent); if (depth > 0) { Add(index, depth - 1); Add(index, depth - 1); Add(index, depth - 1); } }
        Add(-1, 5); Add(-1, 3);
        var data = parents.ToArray(); var index = new UiTreeVisibilityIndex(data);
        var expanded = new bool[data.Length]; Array.Fill(expanded, true);
        var random = new Random(9151);
        for (int operation = 0; operation < 600; operation++)
        {
            int node = random.Next(data.Length); bool state = random.Next(2) != 0;
            expanded[node] = state; index.SetExpanded(node, state);
            for (int probe = 0; probe < 8; probe++)
            {
                int target = random.Next(data.Length), oracle = 0;
                bool targetShown = true;
                for (int p = data[target]; p >= 0; p = data[p]) if (!expanded[p]) { targetShown = false; break; }
                // Counts rows actually on screen: a node hidden by an ancestor shows nothing beneath it.
                if (expanded[target] && targetShown)
                    for (int i = target + 1; i < data.Length; i++)
                    {
                        bool descendant = false, visible = true;
                        for (int p = data[i]; p >= 0; p = data[p])
                        {
                            if (p == target) { descendant = true; break; }
                            if (!expanded[p]) { visible = false; }
                        }
                        if (descendant && visible) oracle++;
                    }
                Assert.Equal(oracle, index.GetVisibleDescendantCount(target));
            }
        }
    }

    [Fact]
    public void TransientVisibilityShowsCollapsedRowsWithoutChangingStateAndRestoresExactly()
    {
        var index = new UiTreeVisibilityIndex([-1, 0, 1, 1, 0, -1, 5]);
        index.SetExpanded(1, false); index.SetExpanded(0, false);
        long revision = index.Revision;
        Assert.Equal(3, index.VisibleCount); Assert.Equal(0, index.GetVisibleDescendantCount(0));

        index.SetTransientVisible(0, true);
        Assert.Equal(0, index.TransientVisibleNode);
        Assert.False(index.IsExpanded(0));
        Assert.Equal(revision, index.Revision);
        Assert.Equal(5, index.VisibleCount); // node 1 stays collapsed inside the transient block
        Assert.Equal(2, index.GetVisibleDescendantCount(0));
        Assert.Equal(1, index.GetVisibleRank(1)); Assert.Equal(-1, index.GetVisibleRank(2)); Assert.Equal(4, index.GetNodeIndex(2));
        Assert.Throws<InvalidOperationException>(() => index.SetTransientVisible(5, true));
        index.SetTransientVisible(0, true); // idempotent

        index.SetTransientVisible(0, false);
        Assert.Equal(-1, index.TransientVisibleNode);
        Assert.Equal(3, index.VisibleCount); Assert.Equal(-1, index.GetVisibleRank(1));
        index.SetTransientVisible(0, false); // idempotent
        index.SetTransientVisible(5, true); // expanded node: nothing to show, no ownership taken
        Assert.Equal(-1, index.TransientVisibleNode);

        // A real fold of the transient node clears the preview first so counts never go negative.
        index.SetTransientVisible(0, true);
        index.SetExpanded(0, true);
        Assert.Equal(-1, index.TransientVisibleNode);
        Assert.Equal(5, index.VisibleCount); Assert.Equal(2, index.GetVisibleDescendantCount(0));
        index.SetExpanded(0, false);
        Assert.Equal(3, index.VisibleCount);
    }

    [Fact]
    public void InvalidTopologyAndBoundsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new UiTreeVisibilityIndex([-1, 0, -1, 0]));
        Assert.Throws<ArgumentException>(() => new UiTreeVisibilityIndex([0]));
        Assert.Throws<ArgumentException>(() => new UiTreeVisibilityIndex([-1], 0));
        var empty = new UiTreeVisibilityIndex([]);
        Assert.Equal(0, empty.VisibleCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.GetNodeIndex(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.SetExpanded(0, true));
    }
}
