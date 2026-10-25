using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class PaperSubtreePaintTests
{
    [Fact]
    public void CaptureFailureRestoresHostCanvasAndFollowingFrameCanRender()
    {
        var renderer = new Renderer();
        var paper = new Paper(renderer, 100, 100, new FontAtlasSettings { AtlasSize = 32 });
        var host = paper.Canvas;
        var destination = new Canvas(renderer, new FontAtlasSettings { AtlasSize = 32 });
        var painter = new Painter(destination);
        bool fail = true;
        try
        {
            Build();
            Assert.Throws<InvalidOperationException>(paper.EndFrame);
            Assert.Same(host, paper.Canvas);
            fail = false;
            Build(); paper.EndFrame();
            Assert.Same(host, paper.Canvas);
            Assert.Equal(2, painter.Calls);
            Assert.True(destination.VertexCount > 0);
        }
        finally { destination.Dispose(); host.Dispose(); }

        void Build()
        {
            paper.BeginFrame(.016f);
            using (paper.Box("root").Size(80, 80).Enter())
            {
                paper.CurrentParent.Data.SubtreePainter = painter;
                paper.Draw((canvas, rect) =>
                {
                    Assert.Same(destination, canvas);
                    if (fail) throw new InvalidOperationException("Injected");
                    canvas.RectFilled(0, 0, 10, 10, new Color32(255, 0, 0, 255));
                });
            }
        }
    }

    [Fact]
    public void DeferredChildBypassesSubtreePainterAndHookDoesNotSurviveNextFrame()
    {
        var renderer = new Renderer();
        var paper = new Paper(renderer, 100, 100, new FontAtlasSettings { AtlasSize = 32 });
        var destination = new Canvas(renderer, new FontAtlasSettings { AtlasSize = 32 });
        var painter = new Painter(destination);
        int callbacks = 0;
        try
        {
            paper.BeginFrame(.016f);
            using (paper.Box("root").Size(80, 80).Enter())
            {
                paper.CurrentParent.Data.SubtreePainter = painter;
                using (paper.Box("popup").Size(20, 20).Layer(10).Enter())
                    paper.Draw((canvas, _) => { Assert.Same(paper.Canvas, canvas); callbacks++; });
            }
            paper.EndFrame();
            Assert.Equal(0, painter.Calls); Assert.Equal(1, callbacks);
            paper.BeginFrame(.016f);
            using (paper.Box("root").Size(80, 80).Enter())
                Assert.Null(paper.CurrentParent.Data.SubtreePainter);
            paper.EndFrame();
        }
        finally { destination.Dispose(); paper.Canvas.Dispose(); }
    }

    private sealed class Painter(Canvas destination) : IPaperSubtreePainter
    {
        public int Calls;
        public bool TryPaint(Paper paper, ElementHandle root, Canvas host)
        {
            Calls++;
            destination.BeginFrame(host.Width, host.Height, host.FramebufferScale);
            destination.ApplyLayerContext(host.GetLayerContext());
            paper.PaintSubtreeTo(root, destination);
            return true;
        }
    }

    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
    }
}
