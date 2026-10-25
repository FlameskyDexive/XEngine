using XEngine.Quill;
using XEngine.Scribe;
using XEngine.Vector;

namespace XEngine.Editor.UI.Tests;

public sealed class SharedFontCanvasTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void TextAndLayoutUseDestinationGeometryAndOneAtlas(float scale)
    {
        var hostRenderer = new Renderer(); var childRenderer = new Renderer();
        var host = new Canvas(hostRenderer, new FontAtlasSettings());
        var child = Canvas.CreateWithSharedFonts(childRenderer, host);
        try
        {
            host.BeginFrame(400, 300, scale); child.BeginFrame(400, 300, scale);
            var settings = TextLayoutSettings.Default; settings.Font = Font();
            child.DrawText("Shared geometry", 4, 7, Color32.FromArgb(255, 255, 255, 255), settings);
            Assert.Equal(0, host.VertexCount);
            Assert.True(child.VertexCount > 0);
            host.DrawText("Shared geometry", 4, 7, Color32.FromArgb(255, 255, 255, 255), settings);
            Assert.Equal(host.Vertices.ToArray(), child.Vertices.ToArray());
            Assert.Equal(host.Indices.ToArray(), child.Indices.ToArray());
            Assert.Same(host.FontAtlasTexture, child.FontAtlasTexture);
            Assert.Same(host.Text.FontEngine, child.Text.FontEngine);
            host.BeginFrame(400, 300, scale); child.BeginFrame(400, 300, scale);
            var layout = host.CreateLayout("A layout", settings);
            child.DrawLayout(layout, 8, 11, Color32.FromArgb(255, 255, 255, 255));
            Assert.Equal(0, host.VertexCount);
            host.DrawLayout(layout, 8, 11, Color32.FromArgb(255, 255, 255, 255));
            Assert.Equal(host.Vertices.ToArray(), child.Vertices.ToArray());
            Assert.Equal(0, childRenderer.Creates);
            Assert.Equal(0, childRenderer.Uploads);
        }
        finally { child.Dispose(); host.Dispose(); }
        Assert.Equal(1, hostRenderer.Disposals); Assert.Equal(1, childRenderer.Disposals);
    }

    [Fact]
    public void MarkdownAndRichTextStayInTheBorrowingCanvas()
    {
        var host = new Canvas(new Renderer(), new FontAtlasSettings());
        var childRenderer = new Renderer();
        var child = Canvas.CreateWithSharedFonts(childRenderer, host);
        try
        {
            host.BeginFrame(400, 300, 1.5f); child.BeginFrame(400, 300, 1.5f);
            var font = Font();
            var markdown = child.CreateMarkdown("**Bold** and [link](https://example.test)", MarkdownLayoutSettings.Default(font, 300));
            child.DrawMarkdown(markdown, new Float2(4, 5));
            Assert.Equal(0, host.VertexCount); Assert.True(child.VertexCount > 0);
            host.DrawMarkdown(markdown, new Float2(4, 5));
            Assert.Equal(host.Vertices.ToArray(), child.Vertices.ToArray());
            Assert.Equal(host.Indices.ToArray(), child.Indices.ToArray());
            host.BeginFrame(400, 300, 1.5f); child.BeginFrame(400, 300, 1.5f);
            var rich = child.CreateRichText("<b>Rich</b> text", new RichTextLayoutSettings { RegularFont = font, BoldFont = font });
            child.DrawRichText(rich, new Float2(4, 5), 0);
            Assert.Equal(0, host.VertexCount); Assert.True(child.VertexCount > 0);
            host.DrawRichText(rich, new Float2(4, 5), 0);
            Assert.Equal(host.Vertices.ToArray(), child.Vertices.ToArray());
            Assert.Equal(host.Indices.ToArray(), child.Indices.ToArray());
            Assert.Equal(0, childRenderer.Creates); Assert.Equal(0, childRenderer.Uploads);
        }
        finally { child.Dispose(); host.Dispose(); }
    }

    [Fact]
    public void SharedFontMutationInvalidatesBothFrameRevisions()
    {
        var host = new Canvas(new Renderer(), new FontAtlasSettings());
        var child = Canvas.CreateWithSharedFonts(new Renderer(), host);
        try
        {
            host.BeginFrame(400, 300); child.BeginFrame(400, 300);
            host.AddFallbackFont(Font());
            Assert.False(host.IsFontAtlasStableForFrame);
            Assert.False(child.IsFontAtlasStableForFrame);
            Assert.Equal(host.FontAtlasRevision, child.FontAtlasRevision);
            host.BeginFrame(400, 300); child.BeginFrame(400, 300);
            Assert.True(host.IsFontAtlasStableForFrame); Assert.True(child.IsFontAtlasStableForFrame);
        }
        finally { child.Dispose(); host.Dispose(); }
    }

    [Fact]
    public void ChildDisposalDoesNotReleaseHostAndHostDisposalRejectsFurtherBorrowing()
    {
        var renderer = new Renderer();
        var host = new Canvas(renderer, new FontAtlasSettings());
        var child = Canvas.CreateWithSharedFonts(new Renderer(), host);
        child.Dispose(); child.Dispose();
        Assert.Equal(0, renderer.Disposals);
        host.BeginFrame(100, 100);
        var survivor = Canvas.CreateWithSharedFonts(new Renderer(), host);
        host.Dispose();
        Assert.Throws<ObjectDisposedException>(() => survivor.BeginFrame(100, 100));
        Assert.Throws<ObjectDisposedException>(() => Canvas.CreateWithSharedFonts(new Renderer(), host));
        survivor.Dispose();
    }

    [Fact]
    public void OnlySuccessfulStableOwnerSubmissionRetiresGenerations()
    {
        var renderer = new Renderer();
        var host = new Canvas(renderer, new FontAtlasSettings { AtlasSize = 64 });
        host.EnableFontAtlasRetirement();
        var child = Canvas.CreateWithSharedFonts(new Renderer(), host);
        try
        {
            Assert.Throws<InvalidOperationException>(child.EnableFontAtlasRetirement);
            host.BeginFrame(400, 300); child.BeginFrame(400, 300);
            child.DrawText("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 0, 0, default, 16, Font());
            Assert.False(host.IsFontAtlasStableForFrame);
            host.Render();
            Assert.Empty(renderer.Retired);
            host.BeginFrame(400, 300);
            renderer.FailRender = true;
            Assert.Throws<InvalidOperationException>(host.Render);
            Assert.Empty(renderer.Retired);
            renderer.FailRender = false;
            host.Render();
            Assert.NotEmpty(renderer.Retired);
            Assert.DoesNotContain(renderer.Retired, texture => ReferenceEquals(texture, host.FontAtlasTexture));
            Assert.Equal("retire", renderer.Events[^1]);
            int retired = renderer.Retired.Count;
            child.Dispose();
            Assert.Equal(retired, renderer.Retired.Count);
            host.Dispose();
            Assert.Equal(renderer.Creates, renderer.Retired.Count);
            Assert.Equal("dispose", renderer.Events[^1]);
        }
        finally { child.Dispose(); host.Dispose(); }
    }

    [Fact]
    public void GrowthThroughBorrowerInvalidatesAlreadyRecordedHostGeometry()
    {
        var host = new Canvas(new Renderer(), new FontAtlasSettings { AtlasSize = 64 });
        var childRenderer = new Renderer();
        var child = Canvas.CreateWithSharedFonts(childRenderer, host);
        try
        {
            var settings = TextLayoutSettings.Default; settings.Font = Font();
            host.DrawText("A", 0, 0, default, settings);
            host.BeginFrame(400, 300); child.BeginFrame(400, 300);
            host.DrawText("A", 0, 0, default, settings);
            int revision = host.FontAtlasRevision;
            child.DrawText("BCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 0, 0, default, settings);
            Assert.True(host.FontAtlasRevision > revision);
            Assert.False(host.IsFontAtlasStableForFrame);
            Assert.False(child.IsFontAtlasStableForFrame);
            Assert.Equal(0, childRenderer.Creates); Assert.Equal(0, childRenderer.Uploads);
            Assert.Same(host.FontAtlasTexture, child.FontAtlasTexture);
        }
        finally { child.Dispose(); host.Dispose(); }
    }

    [Fact]
    public void SameRendererCannotAcquireTwoCanvasOwners()
    {
        var renderer = new Renderer(); var host = new Canvas(renderer, new FontAtlasSettings());
        var child = Canvas.CreateWithSharedFonts(new Renderer(), host);
        try
        {
            Assert.Throws<ArgumentException>(() => Canvas.CreateWithSharedFonts(renderer, host));
            Assert.Throws<ArgumentException>(() => Canvas.CreateWithSharedFonts(renderer, child));
        }
        finally { child.Dispose(); host.Dispose(); }
    }

    private static FontFile Font() => new(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
    private sealed class Renderer : ICanvasRenderer
    {
        public int Creates, Uploads, Disposals;
        public bool FailRender;
        public List<object> Retired = new();
        public List<string> Events = new();
        public bool SupportsFontAtlasRetirement => true;
        public void RetireFontAtlas(object texture) { Retired.Add(texture); Events.Add("retire"); }
        public object CreateTexture(uint width, uint height) { Creates++; return new Int2((int)width, (int)height); }
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { Uploads++; }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls)
        { if (FailRender) throw new InvalidOperationException("Injected submission failure"); Events.Add("submit"); }
        public void Dispose() { Disposals++; Events.Add("dispose"); }
    }
}
