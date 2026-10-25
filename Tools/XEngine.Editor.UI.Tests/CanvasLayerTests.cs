using XEngine.Quill;
using XEngine.Scribe;
using XEngine.Vector;
using XEngine.Vector.Geometry;
using XEngine.Vector.Spatial;

namespace XEngine.Editor.UI.Tests;

public sealed class CanvasLayerTests
{
    [Fact]
    public void UnsupportedRendererRejectsMarkerWithoutChangingDraws()
    {
        var canvas = Make(false);
        try
        {
            canvas.BeginFrame(100, 100, 1);
            Assert.Throws<NotSupportedException>(() => canvas.AppendRenderLayer(new Layer()));
            Assert.Empty(canvas.DrawCalls);
        }
        finally { canvas.Dispose(); }
    }

    [Fact]
    public void MarkersKeepOrderingAndIndexCountsAcrossEmptyAndGeometrySegments()
    {
        var canvas = Make(true);
        try
        {
            canvas.BeginFrame(100, 100, 1);
            var first = new Layer(); var second = new Layer();
            canvas.AppendRenderLayer(first);
            canvas.AppendRenderLayer(second);
            canvas.RectFilled(0, 0, 10, 10, new Color32(255, 255, 255, 255));
            var last = new Layer(); canvas.AppendRenderLayer(last);
            Assert.Equal(4, canvas.DrawCalls.Count);
            Assert.Same(first, canvas.DrawCalls[0].RenderLayer);
            Assert.Same(second, canvas.DrawCalls[1].RenderLayer);
            Assert.Equal(canvas.IndexCount, canvas.DrawCalls[2].ElementCount);
            Assert.Null(canvas.DrawCalls[2].RenderLayer);
            Assert.Same(last, canvas.DrawCalls[3].RenderLayer);
            Assert.Throws<InvalidOperationException>(() => canvas.AppendRenderLayer(first));
            Assert.Equal(4, canvas.DrawCalls.Count);
            canvas.BeginFrame(100, 100, 1);
            canvas.AppendRenderLayer(first);
            Assert.Single(canvas.DrawCalls);
            Assert.Equal(0, canvas.IndexCount);
        }
        finally { canvas.Dispose(); }
    }

    [Fact]
    public void ContextTransfersExactTransformAndIntersectedClipAndRejectsWrongDpi()
    {
        var source = Make(true); var child = Make(true);
        try
        {
            source.BeginFrame(100, 100, 1.5f);
            source.CurrentTransform(Transform2D.CreateTranslation(2.25f, 7.5f));
            source.Scissor(4, 5, 80, 70); source.IntersectScissor(6, 7, 50, 40);
            source.SetGlobalAlpha(0.375f);
            var expected = source.GetLayerContext();
            child.BeginFrame(100, 100, 1);
            var before = child.GetLayerContext();
            Assert.Throws<ArgumentException>(() => child.ApplyLayerContext(expected));
            Assert.Equal(before, child.GetLayerContext());
            child.BeginFrame(100, 100, 1.5f); child.ApplyLayerContext(expected);
            Assert.Equal(expected, child.GetLayerContext());
            Assert.Equal(0.375f, child.GetLayerContext().GlobalAlpha);
            child.AppendRenderLayer(new Layer());
            Assert.Equal(expected, child.DrawCalls[0].LayerContext);
            child.CurrentTransform(Transform2D.CreateTranslation(2.25001f, 7.5f));
            Assert.NotEqual(expected, child.GetLayerContext());
        }
        finally { source.Dispose(); child.Dispose(); }
    }

    private static Canvas Make(bool supported) => new(new Renderer(supported), new FontAtlasSettings { AtlasSize = 32 });
    private sealed class Layer : ICanvasRenderLayer { }
    private sealed class Renderer(bool supported) : ICanvasRenderer
    {
        public bool SupportsRenderLayers => supported;
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
        public void Dispose() { }
    }
}
