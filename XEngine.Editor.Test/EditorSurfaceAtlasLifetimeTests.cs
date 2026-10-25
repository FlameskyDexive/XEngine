using XEngine.Editor.Core;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime.GUI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorSurfaceAtlasLifetimeTests
{
    [Fact]
    public void ReleasingAndReopeningHostRetiresCanvasFontsAndInvalidatesBorrowers()
    {
        var host = new EditorSurfaceHost(1, new DockSpace(DockNode.Leaf()), new Rect(0, 0, 100, 100));
        for (int iteration = 0; iteration < 3; iteration++)
        {
            var renderer = new Renderer();
            var paper = new Paper(renderer, 100, 100, new FontAtlasSettings { AtlasSize = 32 });
            paper.Canvas.EnableFontAtlasRetirement();
            var child = Canvas.CreateWithSharedFonts(new Renderer(), paper.Canvas);
            // The uninitialized runtime renderer is only the host's typed rendering token;
            // this test exercises Canvas lifecycle without a graphics device/native window.
            host.AttachPaper(new PaperRenderer(), paper);
            host.ReleasePaper(); host.ReleasePaper();
            Assert.Null(host.Paper); Assert.Null(host.PaperRenderer);
            Assert.True(paper.Canvas.IsDisposed);
            Assert.Equal(1, renderer.Retirements); Assert.Equal(1, renderer.Disposals);
            Assert.Throws<ObjectDisposedException>(() => child.BeginFrame(100, 100));
            child.Dispose();
        }
    }

    [Fact]
    public void FailedFontRetirementStillDisposesRendererAndDetachesHost()
    {
        var host = new EditorSurfaceHost(1, new DockSpace(DockNode.Leaf()), new Rect(0, 0, 100, 100));
        var renderer = new Renderer { ThrowOnRetire = true };
        var paper = new Paper(renderer, 100, 100, new FontAtlasSettings { AtlasSize = 32 });
        paper.Canvas.EnableFontAtlasRetirement();
        host.AttachPaper(new PaperRenderer(), paper);
        Assert.Throws<InvalidOperationException>(host.ReleasePaper);
        Assert.Null(host.Paper); Assert.Null(host.PaperRenderer);
        Assert.Equal(1, renderer.Disposals);
        host.ReleasePaper();
    }

    private sealed class Renderer : ICanvasRenderer
    {
        public int Retirements, Disposals;
        public bool ThrowOnRetire;
        public bool SupportsFontAtlasRetirement => true;
        public void RetireFontAtlas(object texture)
        { if (ThrowOnRetire) throw new InvalidOperationException("Injected retirement failure"); Retirements++; }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
        public void Dispose() { Disposals++; }
    }
}
