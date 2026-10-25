using XEngine.Editor.GUI.Panels;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>Indexed (virtualized) tree fold transitions must reproduce the legacy wrapper
/// behaviour: the child block lerps its height over 0.15 s, vanishing rows stay built while
/// they shrink, and the index is restored before Show returns.</summary>
public sealed class IndexedFoldTransitionTests
{
    private const float Step = 1f / 60;

    [Fact]
    public void CollapseKeepsGhostRowsWhileShrinkingThenSettlesToIndexedRows()
    {
        var paper = NewPaper();
        var (nodes, store, resolved) = Model(childCount: 5);
        var index = new HierarchyRowIndex(nodes, store, resolved);
        using var tree = Origami.Tree(paper, "fold", 300, 400).Nodes(nodes).RowIndex(index).ExpandStateStore(store).OnSelect(static _ => { });
        float rh = RowHeight(paper, tree);
        try
        {
            Draw(paper, tree);
            Assert.Equal(-1, tree.AnimatingFoldNode);
            Assert.Equal(7, CountRows(paper));
            float full = ContentHeight(paper);

            store["a"] = false; index.SetExpanded("a", false);
            Assert.Equal(2, index.Count);
            Draw(paper, tree);
            Assert.Equal(0, tree.AnimatingFoldNode);
            float expected = 1f - Step / 0.15f;
            Assert.InRange(tree.FoldProgress, expected - 0.01f, expected + 0.01f);
            Assert.Equal(7, CountRows(paper)); // ghost rows are still built inside the wrapper
            Assert.Equal(2, index.Count); // transient preview restored before Show returned
            float mid = ContentHeight(paper);
            Assert.InRange(full - mid, 5 * rh * (1f - expected) - 0.5f, 5 * rh * (1f - expected) + 0.5f);
            // The trailing root row moved up by the same shrink.
            Assert.InRange(RowY(paper, "d"), rh * 6 - 5 * rh * (1f - expected) - 0.5f, rh * 6 - 5 * rh * (1f - expected) + 8f);

            for (int i = 0; i < 12; i++) Draw(paper, tree);
            Assert.Equal(-1, tree.AnimatingFoldNode);
            Assert.Equal(2, CountRows(paper));
            Assert.InRange(ContentHeight(paper), full - 5 * rh - 0.5f, full - 5 * rh + 0.5f);
        }
        finally { paper.Canvas.Dispose(); }
    }

    [Fact]
    public void ExpandGrowsBlockFromZeroAndReToggleReversesFromCurrentProgress()
    {
        var paper = NewPaper();
        var (nodes, store, resolved) = Model(childCount: 4);
        store["a"] = false;
        var index = new HierarchyRowIndex(nodes, store, resolved);
        using var tree = Origami.Tree(paper, "fold", 300, 400).Nodes(nodes).RowIndex(index).ExpandStateStore(store).OnSelect(static _ => { });
        float rh = RowHeight(paper, tree);
        try
        {
            Draw(paper, tree);
            Assert.Equal(-1, tree.AnimatingFoldNode);
            Assert.Equal(2, CountRows(paper));
            float collapsed = ContentHeight(paper);

            store["a"] = true; index.SetExpanded("a", true);
            Draw(paper, tree);
            Assert.Equal(0, tree.AnimatingFoldNode);
            float progress = Step / 0.15f;
            Assert.InRange(tree.FoldProgress, progress - 0.01f, progress + 0.01f);
            Assert.Equal(6, CountRows(paper));
            Assert.InRange(ContentHeight(paper) - collapsed, 4 * rh * progress - 0.5f, 4 * rh * progress + 0.5f);
            Draw(paper, tree); Draw(paper, tree);
            float before = tree.FoldProgress;

            // Collapse again mid-animation: progress continues downward from where it was.
            store["a"] = false; index.SetExpanded("a", false);
            Draw(paper, tree);
            Assert.Equal(0, tree.AnimatingFoldNode);
            Assert.InRange(tree.FoldProgress, before - progress - 0.01f, before - progress + 0.01f);
            Assert.Equal(6, CountRows(paper));
            for (int i = 0; i < 12; i++) Draw(paper, tree);
            Assert.Equal(-1, tree.AnimatingFoldNode);
            Assert.Equal(2, CountRows(paper));
            Assert.InRange(ContentHeight(paper), collapsed - 0.5f, collapsed + 0.5f);
        }
        finally { paper.Canvas.Dispose(); }
    }

    [Fact]
    public void OversizedBlocksSnapAndRebuiltIndexDoesNotReplayHistory()
    {
        var paper = NewPaper();
        var (nodes, store, resolved) = Model(childCount: 200);
        var index = new HierarchyRowIndex(nodes, store, resolved);
        using var tree = Origami.Tree(paper, "fold", 300, 120).Nodes(nodes).RowIndex(index).ExpandStateStore(store).OnSelect(static _ => { });
        try
        {
            Draw(paper, tree);
            store["a"] = false; index.SetExpanded("a", false);
            Draw(paper, tree);
            Assert.Equal(-1, tree.AnimatingFoldNode); // 200 rows exceed four viewports: snap
            Assert.Equal(2, CountRows(paper));
            Assert.Equal(2, index.Count);

            // A rebuilt index (model refresh) records the stored collapse as a fold while it is
            // constructed; adopting it must not replay that history as an animation.
            store["a"] = true; index.SetExpanded("a", true);
            for (int i = 0; i < 12; i++) Draw(paper, tree);
            Assert.InRange(CountRows(paper), 3, 20); // expanded, but only viewport rows are built
            store["a"] = false;
            var rebuilt = new HierarchyRowIndex(nodes, store, resolved);
            Assert.Equal(1, rebuilt.FoldRevision);
            Assert.Equal(0, rebuilt.LastFoldNodeIndex);
            tree.RowIndex(rebuilt);
            Draw(paper, tree);
            Assert.Equal(-1, tree.AnimatingFoldNode);
            Assert.Equal(2, CountRows(paper));
            Assert.Equal(2, rebuilt.Count);
        }
        finally { paper.Canvas.Dispose(); }
    }

    [Fact]
    public void ProgrammaticScrollToSnapsFoldSoRevealReachesSettledOffset()
    {
        var paper = NewPaper();
        var (nodes, store, resolved) = Model(childCount: 12);
        store["a"] = false;
        var index = new HierarchyRowIndex(nodes, store, resolved);
        using var tree = Origami.Tree(paper, "fold", 300, 120).Nodes(nodes).RowIndex(index).ExpandStateStore(store).OnSelect(static _ => { });
        float rh = RowHeight(paper, tree);
        try
        {
            Draw(paper, tree);
            Assert.Equal(2, CountRows(paper));
            float collapsed = ContentHeight(paper);

            // Reveal-on-selection: expand the ancestor and scroll to the settled row position in
            // the same frame. The clamp against a mid-animation content height would otherwise
            // leave the scroll far short of the target, so the fold snaps.
            float target = 10 * rh - 60f;
            store["a"] = true; index.SetExpanded("a", true);
            Origami.ScrollTo("fold_scroll", new Float2(0f, target));
            Draw(paper, tree);
            Assert.Equal(-1, tree.AnimatingFoldNode);
            Assert.InRange(ScrollY(paper), target - 0.01f, target + 0.01f);
            Assert.InRange(ContentHeight(paper), collapsed + 12 * rh - 0.5f, collapsed + 12 * rh + 0.5f); // settled, no shrink
            Draw(paper, tree);
            Assert.Equal(-1, tree.AnimatingFoldNode);
            Assert.InRange(ScrollY(paper), target - 0.01f, target + 0.01f);

            // An interactive toggle without a pending ScrollTo still animates.
            store["a"] = false; index.SetExpanded("a", false);
            Draw(paper, tree);
            Assert.Equal(0, tree.AnimatingFoldNode);
        }
        finally { paper.Canvas.Dispose(); }
    }

    private static float ScrollY(Paper paper)
    {
        for (int i = 0; i < paper.ElementCount; i++)
            if (paper.GetElementData(i).OnScroll != null)
                return paper.GetElementStorage(new ElementHandle(paper, i), "scrollY", -1f);
        throw new InvalidOperationException("No scroll view");
    }

    private static (List<TreeNode> nodes, Dictionary<string, bool> store, Dictionary<string, bool> resolved) Model(int childCount)
    {
        var nodes = new List<TreeNode> { new() { Id = "a", Label = "a", Depth = 0, HasChildren = true, DefaultExpanded = true } };
        for (int i = 0; i < childCount; i++) nodes.Add(new TreeNode { Id = "c" + i, Label = "c" + i, Depth = 1 });
        nodes.Add(new TreeNode { Id = "d", Label = "d", Depth = 0 });
        return (nodes, new Dictionary<string, bool>(), new Dictionary<string, bool>());
    }

    private static float RowHeight(Paper paper, TreeBuilder tree)
    {
        Draw(paper, tree);
        return Origami.Current.Metrics.RowHeight;
    }

    private static int CountRows(Paper paper)
    {
        var targets = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < paper.ElementCount; i++)
        {
            var click = paper.GetElementData(i).OnClick;
            if (click?.Target?.GetType().GetField("Node") != null) targets.Add(click.Target);
        }
        return targets.Count;
    }

    private static float RowY(Paper paper, string nodeId)
    {
        float scrollTop = float.NaN;
        for (int i = 0; i < paper.ElementCount; i++)
            if (paper.GetElementData(i).OnScroll != null) { scrollTop = paper.GetElementData(i).Y; break; }
        for (int i = 0; i < paper.ElementCount; i++)
        {
            var data = paper.GetElementData(i);
            var node = data.OnClick?.Target?.GetType().GetField("Node")?.GetValue(data.OnClick.Target) as TreeNode;
            if (node?.Id == nodeId) return data.Y - scrollTop;
        }
        throw new InvalidOperationException("Row not built: " + nodeId);
    }

    private static float ContentHeight(Paper paper)
    {
        for (int i = 0; i < paper.ElementCount; i++)
            if (paper.GetElementData(i).OnScroll != null)
                return paper.GetElementStorage(new ElementHandle(paper, i), "contentH", -1f);
        throw new InvalidOperationException("No scroll view");
    }

    private static void Draw(Paper paper, TreeBuilder tree)
    {
        paper.BeginFrame(Step); Origami.BeginFrame(paper, Step);
        tree.Show(); Origami.EndFrame(paper); paper.EndFrame();
    }

    private static Paper NewPaper() => new(new Renderer(), 300, 400, new FontAtlasSettings { AtlasSize = 32 });
    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
    }
}
