using XEngine.Scribe;
using XEngine.Vector;

namespace Tests;

public sealed class BorrowedDrawDestinationTests
{
    [Fact]
    public void BorrowedDestinationReceivesIdenticalGeometryWithoutOwningAtlas()
    {
        var owner = new Recorder();
        var destination = new Recorder();
        var system = new FontSystem(owner);
        var layout = CreateLayout(system);
        system.DrawLayout(layout, new Float2(3, 7), default);
        system.DrawLayout(layout, new Float2(3, 7), default, destination);
        Assert.Equal(1, owner.Draws);
        Assert.Equal(1, destination.Draws);
        Assert.Same(owner.Texture, destination.Texture);
        Assert.Equal(owner.Vertices, destination.Vertices);
        Assert.Equal(owner.Indices, destination.Indices);
        Assert.Equal(0, destination.Creates);
        Assert.Equal(0, destination.Uploads);
        Assert.True(owner.Creates > 0);
        Assert.True(owner.Uploads > 0);
        Assert.False(layout.IsStale(system));
    }

    [Fact]
    public void StaleLayoutUploadsToOwnerWhenDrawnToBorrowedDestination()
    {
        var owner = new Recorder();
        var destination = new Recorder();
        var system = new FontSystem(owner);
        var layout = CreateLayout(system);
        system.AddFallbackFont(new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf")));
        Assert.True(layout.IsStale(system));
        int uploads = owner.Uploads;
        system.DrawLayout(layout, default, default, destination);
        Assert.True(owner.Uploads > uploads);
        Assert.Equal(0, owner.Draws);
        Assert.Equal(1, destination.Draws);
        Assert.Equal(0, destination.Creates);
        Assert.Equal(0, destination.Uploads);
        Assert.False(layout.IsStale(system));
        Assert.Same(system.Texture, destination.Texture);
    }

    [Fact]
    public void ThrowingDestinationDoesNotChangeDefaultRenderer()
    {
        var owner = new Recorder();
        var destination = new Recorder { ThrowOnDraw = true };
        var system = new FontSystem(owner);
        var layout = CreateLayout(system);
        Assert.Throws<InvalidOperationException>(() => system.DrawLayout(layout, default, default, destination));
        system.DrawLayout(layout, default, default);
        Assert.Equal(1, owner.Draws);
        Assert.Equal(1, destination.Draws);
        Assert.Same(system.Texture, owner.Texture);
    }

    [Fact]
    public void WarmBorrowedDrawsAllocateNoManagedBytes()
    {
        var owner = new Recorder { Capture = false };
        var destination = new Recorder { Capture = false };
        var system = new FontSystem(owner);
        var layout = CreateLayout(system);
        for (int i = 0; i < 10000; i++) system.DrawLayout(layout, default, default, destination);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) system.DrawLayout(layout, default, default, destination);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(20000, destination.Draws);
        Assert.Equal(0, owner.Draws);
    }

    private static TextLayout CreateLayout(FontSystem system)
    {
        var settings = TextLayoutSettings.Default;
        settings.Font = new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
        return system.CreateLayout("Shared atlas", settings);
    }

    private sealed class Recorder : IFontRenderer
    {
        public int Creates, Uploads, Draws;
        public bool Capture = true, ThrowOnDraw;
        public object? Texture;
        public IFontRenderer.Vertex[] Vertices = [];
        public int[] Indices = [];
        public object CreateTexture(int width, int height) { Creates++; return new object(); }
        public void UpdateTextureRegion(object texture, AtlasRect bounds, byte[] data) { Uploads++; }
        public void DrawQuads(object texture, ReadOnlySpan<IFontRenderer.Vertex> vertices, ReadOnlySpan<int> indices)
        {
            Draws++;
            if (ThrowOnDraw) throw new InvalidOperationException("Injected destination failure");
            Texture = texture;
            if (Capture) { Vertices = vertices.ToArray(); Indices = indices.ToArray(); }
        }
    }
}
