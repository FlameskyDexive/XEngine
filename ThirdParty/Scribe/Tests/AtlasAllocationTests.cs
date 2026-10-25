using XEngine.Scribe;

namespace Tests;

public sealed class AtlasAllocationTests
{
    [Fact]
    public void LargeAndUnevenAtlasesClearEveryRowWithBoundedUploads()
    {
        foreach (var size in new[] { (4096, 4096), (1023, 2051) })
        {
            var renderer = new ClearCheckingRenderer();
            _ = new FontSystem(renderer, size.Item1, size.Item2);
            Assert.Equal(1, renderer.Clears);
            Assert.True(renderer.ClearUploads > 1);
            Assert.InRange(renderer.MaxClearUpload, 1, 4 * 1024 * 1024);
            Assert.Equal((long)size.Item1 * size.Item2 * 4, renderer.ClearBytes);
        }
    }
    [Fact]
    public void EveryAtlasGenerationIsClearedBeforePartialGlyphUploads()
    {
        var renderer = new ClearCheckingRenderer();
        var system = new FontSystem(renderer, 32, 32);
        Assert.Equal(1, renderer.Clears);
        system.CreateLayout("W", new TextLayoutSettings
        {
            Font = new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf")),
            PixelSize = 128, Quality = FontQuality.Ultra
        });
        Assert.True(renderer.Clears > 1);
        Assert.Equal(renderer.Creates, renderer.Clears);
    }

    private sealed class ClearCheckingRenderer : IFontRenderer
    {
        private sealed class Texture(int width, int height)
        { public int Width = width, Height = height, NextRow; public bool Initialized; }
        public int Creates, Clears;
        public int ClearUploads, MaxClearUpload;
        public long ClearBytes;
        public object CreateTexture(int width, int height) { Creates++; return new Texture(width, height); }
        public void UpdateTextureRegion(object texture, AtlasRect bounds, byte[] data)
        {
            var image = (Texture)texture;
            if (image.Initialized) return;
            Assert.Equal(0, bounds.X); Assert.Equal(image.NextRow, bounds.Y);
            Assert.Equal(image.Width, bounds.Width);
            Assert.InRange(bounds.Height, 1, image.Height - image.NextRow);
            Assert.Equal(image.Width * bounds.Height * 4, data.Length);
            Assert.True(data.AsSpan().IndexOfAnyExcept((byte)0) < 0);
            image.NextRow += bounds.Height;
            ClearUploads++; ClearBytes += data.Length; MaxClearUpload = Math.Max(MaxClearUpload, data.Length);
            if (image.NextRow == image.Height) { image.Initialized = true; Clears++; }
        }
        public void DrawQuads(object texture, ReadOnlySpan<IFontRenderer.Vertex> vertices, ReadOnlySpan<int> indices) { }
    }

    [Fact]
    public void RejectedExpansionPreservesAtlasDimensionsAndCanRetry()
    {
        var renderer = new RejectingRenderer();
        var system = new FontSystem(renderer, 32, 32);
        var texture = system.Texture;
        int version = system.AtlasVersion;
        var settings = new TextLayoutSettings
        {
            Font = new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf")),
            PixelSize = 128,
            Quality = FontQuality.Ultra
        };
        renderer.Reject = true;
        Assert.Throws<InvalidOperationException>(() => system.CreateLayout("W", settings));
        Assert.Equal(32, system.Width);
        Assert.Equal(32, system.Height);
        Assert.Same(texture, system.Texture);
        Assert.Equal(version, system.AtlasVersion);
        renderer.Reject = false;
        var layout = system.CreateLayout("W", settings);
        Assert.NotEmpty(layout.Lines[0].Glyphs);
        Assert.NotSame(texture, system.Texture);
        Assert.True(system.Width > 32);
        Assert.True(system.AtlasVersion > version);
    }

    private sealed class RejectingRenderer : IFontRenderer
    {
        public bool Reject;
        public object CreateTexture(int width, int height)
            => Reject ? throw new InvalidOperationException("Atlas admission rejected.") : new byte[width * height * 4];
        public void UpdateTextureRegion(object texture, AtlasRect bounds, byte[] data) { }
        public void DrawQuads(object texture, ReadOnlySpan<IFontRenderer.Vertex> vertices, ReadOnlySpan<int> indices) { }
    }
}
