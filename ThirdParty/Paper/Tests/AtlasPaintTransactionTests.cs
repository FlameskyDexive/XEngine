using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Scribe;
using XEngine.Vector;

namespace Tests;

public sealed class AtlasPaintTransactionTests
{
    [Fact]
    public void LateLayerGrowthRepaintsGeometryButRunsLayoutOnceAndSubmitsOnce()
    {
        var renderer = new Renderer();
        var paper = new Paper(renderer, 400, 300, new FontAtlasSettings { AtlasSize = 64 })
        { StabilizeFontAtlasBeforeRender = true };
        var font = Font();
        int layouts = 0, firstPaint = 0, lastPaint = 0;
        paper.OnEndOfFramePreLayout = () => layouts++;
        try
        {
            paper.BeginFrame(.016f);
            using (paper.Box("root").Size(400, 300).Enter())
            {
                paper.Draw((canvas, _) => { firstPaint++; canvas.DrawText("A", 0, 0, default, 16, font); });
                using (paper.Box("late").Size(400, 100).Layer(20).Enter())
                    paper.Draw((canvas, _) =>
                    {
                        lastPaint++;
                        canvas.DrawText("BCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 0, 30, default, 16, font);
                    });
            }
            paper.EndFrame();
            Assert.Equal(2, paper.LastPaintAttempts);
            Assert.Equal(2, firstPaint); Assert.Equal(2, lastPaint);
            Assert.Equal(1, layouts); Assert.Equal(1, renderer.Submissions);
            Assert.True(renderer.Stable);
            foreach (var call in paper.Canvas.DrawCalls)
                if (call.FontAtlas != null) Assert.Same(paper.Canvas.FontAtlasTexture, call.FontAtlas);
        }
        finally { paper.Canvas.Dispose(); }
    }

    [Fact]
    public void SharedSubtreePaintIsRecapturedAfterLaterHostAtlasGrowth()
    {
        var renderer = new Renderer();
        var paper = new Paper(renderer, 400, 300, new FontAtlasSettings { AtlasSize = 64 })
        { StabilizeFontAtlasBeforeRender = true };
        var child = Canvas.CreateWithSharedFonts(new Renderer(), paper.Canvas);
        var painter = new SharedPainter(child);
        var font = Font();
        try
        {
            paper.BeginFrame(.016f);
            using (paper.Box("root").Size(400, 300).Enter())
            {
                using (paper.Box("cached").Size(200, 40).Enter())
                {
                    paper.CurrentParent.Data.SubtreePainter = painter;
                    paper.Draw((canvas, _) => canvas.DrawText("A", 0, 0, default, 16, font));
                }
                using (paper.Box("late").Size(400, 100).Layer(20).Enter())
                    paper.Draw((canvas, _) => canvas.DrawText("BCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 0, 30, default, 16, font));
            }
            paper.EndFrame();
            Assert.Equal(2, painter.Calls);
            Assert.Equal(2, paper.LastPaintAttempts);
            Assert.True(child.IsFontAtlasStableForFrame);
            Assert.True(paper.Canvas.IsFontAtlasStableForFrame);
            Assert.True(child.VertexCount > 0);
            Assert.Same(paper.Canvas.FontAtlasTexture, child.FontAtlasTexture);
            Assert.Equal(1, renderer.Submissions);
        }
        finally { child.Dispose(); paper.Canvas.Dispose(); }
    }

    private sealed class SharedPainter(Canvas child) : IPaperSubtreePainter
    {
        public int Calls;
        public bool TryPaint(Paper paper, ElementHandle root, Canvas host)
        {
            Calls++;
            child.BeginFrame(host.Width, host.Height, host.FramebufferScale);
            child.ApplyLayerContext(host.GetLayerContext());
            paper.PaintSubtreeTo(root, child);
            return true;
        }
    }

    [Fact]
    public void PaintRetryDoesNotDispatchClickTwice()
    {
        var paper = new Paper(new Renderer(), 100, 100, new FontAtlasSettings { AtlasSize = 64 })
        { StabilizeFontAtlasBeforeRender = true };
        int clicks = 0, paints = 0;
        bool invalidate = false;
        var font = Font();
        try
        {
            Build();
            paper.SetPointerState(PaperMouseBtn.Unknown, 15, 15, false, true);
            paper.SetPointerState(PaperMouseBtn.Left, 15, 15, true, false);
            paper.EndFrame();
            invalidate = true;
            Build();
            paper.SetPointerState(PaperMouseBtn.Left, 15, 15, false, false);
            paper.EndFrame();
            Assert.Equal(1, clicks);
            Assert.Equal(2, paper.LastPaintAttempts);
            Assert.Equal(3, paints);
        }
        finally { paper.Canvas.Dispose(); }

        void Build()
        {
            paper.BeginFrame(.016f);
            using (paper.Box("button").Size(80, 80).OnClick(_ => clicks++).Enter())
                paper.Draw((canvas, _) =>
                {
                    paints++;
                    if (invalidate) { invalidate = false; canvas.AddFallbackFont(font); }
                });
        }
    }

    [Fact]
    public void UnstablePaintIsBoundedAndNeverSubmittedThenNextFrameRecovers()
    {
        var renderer = new Renderer();
        var paper = new Paper(renderer, 100, 100, new FontAtlasSettings { AtlasSize = 64 })
        { StabilizeFontAtlasBeforeRender = true };
        var font = Font();
        int paints = 0;
        try
        {
            paper.BeginFrame(.016f);
            using (paper.Box("root").Size(100, 100).Layer(10).Enter())
                paper.Draw((canvas, _) => { paints++; canvas.AddFallbackFont(font); });
            Assert.Throws<InvalidOperationException>(paper.EndFrame);
            Assert.Equal(3, paints); Assert.Equal(0, renderer.Submissions);
            paper.BeginFrame(.016f);
            using (paper.Box("root").Size(100, 100).Layer(10).Enter())
                paper.Draw((canvas, _) => canvas.RectFilled(0, 0, 10, 10, default));
            paper.EndFrame();
            Assert.Equal(1, paper.LastPaintAttempts); Assert.Equal(1, renderer.Submissions);
            Assert.Equal(3, paints);
        }
        finally { paper.Canvas.Dispose(); }
    }

    private static FontFile Font() => new(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
    private sealed class Renderer : ICanvasRenderer
    {
        public int Submissions;
        public bool Stable;
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls)
        { Submissions++; Stable = canvas.IsFontAtlasStableForFrame; }
    }
}
