using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Vector;
using Xunit;
using XEngine.Editor.GUI.Panels;
using XEngine.Scribe;

namespace XEngine.Editor.Test;

public sealed class TreeHorizontalExtentTests
{
    [Fact]
    public void LongNameRevealKeepsItsBeginningBeyondTheFixedColumns()
    {
        var paper = new Paper(new Renderer(), 200, 120, new FontAtlasSettings { AtlasSize = 32 });
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var font = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
        var layout = new HierarchyHorizontalLayout();
        var nodes = new List<TreeNode> { new() { Id = "long", Label = new string('W', 100), Depth = 16 } };
        try
        {
            layout.Update(paper.Canvas, font, 12, nodes, 1);
            float measuredLeft = HierarchyTableLayout.LeadingWidth + HierarchyTableLayout.ObjectIndent
                + 16 * HierarchyTableLayout.IndentSize + HierarchyTableLayout.CaretWidth
                + HierarchyTableLayout.IconWidth + HierarchyTableLayout.NameInset;
            float revealedLeft = measuredLeft - layout.RevealX(0, 200);
            Assert.InRange(revealedLeft, HierarchyTableLayout.LeadingWidth, 200 - 24);
        }
        finally { paper.Canvas.Dispose(); }
    }

    [Fact]
    public void MeasurementsReuseAndInvalidateForModelFontAndDpi()
    {
        var paper = new Paper(new Renderer(), 200, 120, new FontAtlasSettings { AtlasSize = 32 });
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var font = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
        var otherFont = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
        var layout = new HierarchyHorizontalLayout();
        var nodes = new List<TreeNode> { new() { Id = "a", Label = "DeepName", Depth = 16 } };
        try
        {
            layout.Update(paper.Canvas, font, 12, nodes, 1);
            float initialWidth = layout.ContentWidth;
            Assert.True(layout.RevealX(0, 200) > 0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) layout.Update(paper.Canvas, font, 12, nodes, 1);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(1, layout.MeasurementBuilds);
            nodes[0].Label = "A much longer renamed object";
            layout.Update(paper.Canvas, font, 12, nodes, 2);
            Assert.True(layout.ContentWidth > initialWidth);
            layout.Update(paper.Canvas, otherFont, 12, nodes, 2);
            layout.Update(paper.Canvas, otherFont, 16, nodes, 2);
            paper.Canvas.BeginFrame(400, 240, 2);
            layout.Update(paper.Canvas, otherFont, 16, nodes, 2);
            paper.Canvas.Text.FontEngine.AddFallbackFont(font);
            layout.Update(paper.Canvas, otherFont, 16, nodes, 2);
            Assert.Equal(6, layout.MeasurementBuilds);
            nodes.Clear();
            layout.Update(paper.Canvas, otherFont, 16, nodes, 3);
            Assert.Equal(0, layout.ContentWidth);
            Assert.Equal(0, layout.RevealX(0, 200));
            layout.Clear();
            Assert.Equal(0, layout.ContentWidth);
        }
        finally { layout.Clear(); paper.Canvas.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownExtentClampsOnTheFirstFrameAfterResize(bool smooth)
    {
        var paper = new Paper(new Renderer(), 200, 120, new FontAtlasSettings { AtlasSize = 32 });
        try
        {
            Origami.ScrollTo("extent_test", new Float2(1000, 0));
            var wide = Frame(500);
            if (!smooth) Assert.Equal(300f, wide.ScrollX);
            // Shrinking must also clamp a previously eased position immediately.
            var narrow = Frame(100);
            Assert.Equal(0f, narrow.ScrollX);
            Assert.Equal(200f, narrow.Width);
            Origami.ScrollTo("extent_test", new Float2(1000, 0));
            var grown = Frame(600);
            if (!smooth) Assert.Equal(400f, grown.ScrollX);
        }
        finally { paper.Canvas.Dispose(); }

        ScrollViewport Frame(float width)
        {
            paper.BeginFrame(1f / 60);
            Origami.BeginFrame(paper, 1f / 60);
            ScrollViewport viewport = default;
            float actualWidth = 0;
            Origami.ScrollView(paper, "extent_test", 200, 100)
                .Horizontal().ContentWidth(width).SmoothScroll(smooth)
                .Body(v =>
                {
                    viewport = v;
                    paper.Box("extent_row").Height(20)
                        .OnPostLayout((_, rect) => actualWidth = (float)rect.Size.X);
                });
            Origami.EndFrame(paper);
            paper.EndFrame();
            Assert.Equal(MathF.Max(200, width), actualWidth);
            return viewport;
        }
    }

    [Fact]
    public void TreePassesKnownExtentToItsRows()
    {
        var paper = new Paper(new Renderer(), 200, 120, new FontAtlasSettings { AtlasSize = 32 });
        try
        {
            paper.BeginFrame(1f / 60);
            Origami.BeginFrame(paper, 1f / 60);
            float contentWidth = 0;
            using var tree = Origami.Tree(paper, "wide_tree", 200, 100);
            tree.ContentWidth(500).Nodes(new List<TreeNode> { new() { Id = "deep", Depth = 16 } })
                .CustomRowContent((p, _, _, _) => p.Box("deep_content").Height(18)
                    .OnPostLayout((_, rect) => contentWidth = (float)rect.Size.X))
                .Show();
            Origami.EndFrame(paper);
            paper.EndFrame();
            Assert.True(contentWidth > 200, $"Deep row content width was {contentWidth}");
        }
        finally { paper.Canvas.Dispose(); }
    }

    private sealed class Renderer : ICanvasRenderer
    {
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
        public void Dispose() { }
    }
}
