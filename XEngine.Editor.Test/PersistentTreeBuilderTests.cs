using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.Events;
using XEngine.Quill;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class PersistentTreeBuilderTests
{
    [Fact]
    public void RepeatedFramesReuseRowBindingAndDisposeMakesSavedCallbackInert()
    {
        var paper = NewPaper();
        var nodes = new List<TreeNode> { new() { Id = "first" } };
        using var tree = Origami.Tree(paper, "persistent", 300, 200).Nodes(nodes);
        string? selected = null;
        tree.OnSelect(e => selected = e.Node.Id);
        try
        {
            Draw(paper, tree);
            Action<ClickEvent> first = FindRowClick(paper);
            for (int i = 0; i < 100; i++) Draw(paper, tree);
            Assert.Equal(1, tree.BindingCreations);
            Assert.Same(first, FindRowClick(paper));
            Assert.Equal(1, tree.CachedRowIdCount);
            nodes[0] = new TreeNode { Id = "replacement" };
            Draw(paper, tree);
            first(new ClickEvent(default, new Rect(0, 0, 10, 10), default, PaperMouseBtn.Left));
            Assert.Equal("replacement", selected);
            tree.Dispose();
            selected = null;
            first(new ClickEvent(default, default, default, PaperMouseBtn.Left));
            Assert.Null(selected);
            Assert.Null(first.Target!.GetType().GetField("Node")!.GetValue(first.Target));
            Assert.Null(first.Target.GetType().GetField("AllNodes")!.GetValue(first.Target));
            Assert.Equal(0, tree.CachedRowIdCount);
            Assert.Equal(0, tree.RetainedBindingCount);
            Assert.Throws<ObjectDisposedException>(tree.Show);
        }
        finally { paper.Canvas.Dispose(); }
    }

    [Fact]
    public void TraversingManyDifferentRowsKeepsIdCacheBoundedAndPoolsAreNotShared()
    {
        var paper = NewPaper();
        var nodes = new List<TreeNode> { new() { Id = "start" } };
        using var tree = Origami.Tree(paper, "bounded", 300, 200).Nodes(nodes).OnSelect(static _ => { });
        try
        {
            for (int i = 0; i < 700; i++)
            {
                nodes[0] = new TreeNode { Id = i.ToString() };
                Draw(paper, tree);
            }
            Assert.Equal(512, tree.CachedRowIdCount);
            Assert.Equal(1, tree.BindingCreations);
            Assert.Equal(1, tree.RetainedBindingCount);
            var callback = FindRowClick(paper);
            tree.Dispose();
            using var replacement = Origami.Tree(paper, "bounded", 300, 200).Nodes(nodes).OnSelect(static _ => { });
            Draw(paper, replacement);
            Assert.NotSame(callback.Target, FindRowClick(paper).Target);
        }
        finally { paper.Canvas.Dispose(); }
    }

    private static Action<ClickEvent> FindRowClick(Paper paper)
    {
        for (int i = 0; i < paper.ElementCount; i++)
        {
            var click = paper.GetElementData(i).OnClick;
            if (click?.Target?.GetType().GetField("Node") != null) return click;
        }
        throw new InvalidOperationException("No actual row callback");
    }

    private static void Draw(Paper paper, TreeBuilder tree)
    {
        paper.BeginFrame(1f / 60); Origami.BeginFrame(paper, 1f / 60);
        tree.Show(); Origami.EndFrame(paper); paper.EndFrame();
    }

    private static Paper NewPaper() => new(new Renderer(), 300, 200, new FontAtlasSettings { AtlasSize = 32 });
    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
    }
}
