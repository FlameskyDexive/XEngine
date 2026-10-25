using XEngine.Editor.GUI.Panels;
using XEngine.OrigamiUI;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class HierarchyRowIndexTests
{
    [Fact]
    public void HundredThousandRowDropSwitchesAllocateNothingAndTouchTwoRows()
    {
        var nodes = new List<TreeNode>(100_000);
        for (int i = 0; i < 100_000; i++) nodes.Add(new TreeNode { Id=i.ToString(), Depth=0 });
        var index = new HierarchyRowIndex(nodes, new(), new());
        string first = nodes[0].Id, last = nodes[^1].Id;
        for (int i = 0; i < 100; i++) index.UpdateDropIndicator((i & 1) == 0 ? first : last, .5f);
        long before = GC.GetAllocatedBytesForCurrentThread(); int touched = 0;
        for (int i = 0; i < 1000; i++)
        { index.UpdateDropIndicator((i & 1) == 0 ? first : last, .5f); touched += index.LastDropRowsTouched; }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, bytes); Assert.Equal(2000, touched);
        Assert.Null(nodes[0].DropIndicator); Assert.Equal(TreeDropPosition.Into, nodes[^1].DropIndicator);
    }

    [Fact]
    public void IndexAcceptsAHundredThousandObjectSceneWithItsExistingRows()
    {
        // A 100k-object scene is never exactly 100k rows: the plan target plus the objects already in
        // the scene must fit (the old 100,000 cap threw from OnGUI every frame for 100,763 rows).
        const int fan = 10; int count = 100_763;
        var nodes = new List<TreeNode>(count);
        void Add(int depth, ref int made)
        {
            if (made >= count) return;
            nodes.Add(new TreeNode { Id = made.ToString(), Depth = depth, HasChildren = depth < 4, DefaultExpanded = true }); made++;
            if (depth < 4) for (int i = 0; i < fan && made < count; i++) Add(depth + 1, ref made);
        }
        int made = 0; while (made < count) Add(0, ref made);
        var index = new HierarchyRowIndex(nodes, new(), new());
        Assert.Equal(count, index.Count);
        Assert.Equal(count - 1, index.GetVisibleRank(nodes[^1].Id));
        Assert.Equal(1_000_000, HierarchyRowIndex.MaxNodes);
        // Beyond the bound the panel falls back to the whole-list tree; the index itself refuses.
        var parents = new int[HierarchyRowIndex.MaxNodes + 1];
        Array.Fill(parents, -1);
        Assert.Throws<ArgumentException>(() => new XEngine.Editor.UI.UiTreeVisibilityIndex(parents, HierarchyRowIndex.MaxNodes));
    }

    [Fact]
    public void DropUpdatesTouchOnlyPreviousAndNewRowsAndClearAtEnd()
    {
        var nodes = new List<TreeNode> {
            new() { Id="a", Depth=0, HasChildren=true, DefaultExpanded=true },
            new() { Id="b", Depth=1 }, new() { Id="c", Depth=0 }
        };
        var index = new HierarchyRowIndex(nodes, new(), new());
        index.UpdateDropIndicator("a", .9f);
        Assert.Equal(TreeDropPosition.IntoFirst, nodes[0].DropIndicator);
        Assert.Equal(1, index.LastDropRowsTouched);
        index.UpdateDropIndicator("a", .9f); Assert.Equal(0, index.LastDropRowsTouched);
        index.SetExpanded("a", false); index.UpdateDropIndicator("a", .9f);
        Assert.Equal(TreeDropPosition.Below, nodes[0].DropIndicator);
        index.UpdateDropIndicator("c", .1f);
        Assert.Null(nodes[0].DropIndicator); Assert.Equal(TreeDropPosition.Above, nodes[2].DropIndicator);
        Assert.Equal(2, index.LastDropRowsTouched);
        index.UpdateDropIndicator("missing", .5f); Assert.Null(nodes[2].DropIndicator);
        index.UpdateDropIndicator("c", .5f); Assert.Equal(TreeDropPosition.Into, nodes[2].DropIndicator);
        index.UpdateDropIndicator(null, 0); Assert.Null(nodes[2].DropIndicator);
        index.UpdateDropIndicator(null, 0); Assert.Equal(0, index.LastDropRowsTouched);
    }

    [Fact]
    public void RebuiltIndexClearsAnExistingDropIndicatorAndMapsParents()
    {
        var nodes = new List<TreeNode> {
            new() { Id="a", Depth=0, HasChildren=true, DefaultExpanded=true },
            new() { Id="b", Depth=1, DropIndicator=TreeDropPosition.Into }
        };
        var index = new HierarchyRowIndex(nodes, new(), new());
        Assert.Equal(0, index.GetParentIndex(1)); Assert.Equal(-1, index.GetParentIndex(0));
        Assert.Equal(1, index.GetVisibleRank("b"));
        index.SetExpanded("a", false); Assert.Equal(-1, index.GetVisibleRank("b"));
        index.UpdateDropIndicator(null, 0); Assert.Null(nodes[1].DropIndicator);
    }

    [Fact]
    public void FoldRecordTracksOnlyEffectiveVisibilityChanges()
    {
        var nodes = new List<TreeNode> {
            new() { Id="a", Depth=0, HasChildren=true, DefaultExpanded=true },
            new() { Id="b", Depth=1, HasChildren=true, DefaultExpanded=true },
            new() { Id="c", Depth=2 }, new() { Id="d", Depth=0 }
        };
        var index = new HierarchyRowIndex(nodes, new(), new());
        long baseline = index.FoldRevision;
        index.SetExpanded("a", true); // already expanded: no visibility change
        Assert.Equal(baseline, index.FoldRevision);
        index.SetExpanded("d", false); // leaf: nothing to fold
        Assert.Equal(baseline, index.FoldRevision);
        index.SetExpanded("missing", false);
        Assert.Equal(baseline, index.FoldRevision);

        index.SetExpanded("b", false);
        Assert.Equal(baseline + 1, index.FoldRevision);
        Assert.Equal(1, index.LastFoldNodeIndex); Assert.False(index.LastFoldExpanded);
        Assert.Equal(0, index.GetVisibleDescendantCount(1)); Assert.Equal(1, index.GetVisibleDescendantCount(0));

        index.SetTransientVisible(1, true);
        Assert.Equal(baseline + 1, index.FoldRevision);
        Assert.False(index.IsExpanded(1));
        Assert.Equal(1, index.GetVisibleDescendantCount(1)); Assert.Equal(2, index.GetVisibleDescendantCount(0));
        Assert.Equal(4, index.Count);
        index.SetTransientVisible(1, false);
        Assert.Equal(3, index.Count);

        index.SetExpanded("a", false);
        Assert.Equal(baseline + 2, index.FoldRevision);
        Assert.Equal(0, index.LastFoldNodeIndex); Assert.False(index.LastFoldExpanded);
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void IndexedRowsPreserveRawExpansionAndSearchOverride()
    {
        var nodes = new List<TreeNode> {
            new() { Id="a", Depth=0, HasChildren=true, DefaultExpanded=true },
            new() { Id="b", Depth=1, HasChildren=true, DefaultExpanded=true },
            new() { Id="c", Depth=2 }, new() { Id="d", Depth=0 }
        };
        var stored = new Dictionary<string, bool> { ["b"] = false };
        var resolved = new Dictionary<string, bool>();
        var index = new HierarchyRowIndex(nodes, stored, resolved);
        Assert.Equal(3, index.Count); Assert.Equal(3, index.GetNodeIndex(2));
        index.SetExpanded("a", false); Assert.Equal(2, index.Count);
        index.SetExpanded("b", true); Assert.Equal(2, index.Count);
        index.SetExpanded("a", true); Assert.Equal(4, index.Count);
        nodes[1].OverrideExpanded = true;
        index = new HierarchyRowIndex(nodes, stored, resolved);
        Assert.True(stored["b"]); Assert.True(resolved["b"]);
        index.SetExpanded("b", false); Assert.Equal(4, index.Count);
    }
}
