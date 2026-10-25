using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public class IndexedScrollExtentTests
{
    [Fact]
    public void CurrentExtentAllowsRevealAndClampsAnimatedOffsetOnCollapse()
    {
        var paper = new Paper(new Renderer(), 300, 200, new FontAtlasSettings { AtlasSize = 32 });
        string id = "extent_" + Guid.NewGuid();
        try
        {
            Assert.Equal(0, Draw(50).ScrollY);
            Origami.ScrollTo(id, new Float2(0, 700));
            ScrollViewport expanded = Draw(2000);
            Assert.True(expanded.ScrollY > 0, "New extent must accept reveal before its first layout");
            for (int i = 0; i < 90; i++) expanded = Draw(2000);
            Assert.InRange(expanded.ScrollY, 699, 701);
            Assert.Equal(0, Draw(50).ScrollY);
            Assert.Equal(0, Draw(50).ScrollY);
        }
        finally { paper.Canvas.Dispose(); }

        ScrollViewport Draw(float height)
        {
            ScrollViewport result = default;
            paper.BeginFrame(1f / 60);
            Origami.BeginFrame(paper, 1f / 60);
            Origami.ScrollView(paper, id, 300, 200).ContentHeight(height).Body(view =>
            {
                result = view;
                paper.Box(id + "_body").Height(height);
            });
            Origami.EndFrame(paper);
            paper.EndFrame();
            return result;
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
