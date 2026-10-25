using System;
using System.IO;
using System.Linq;

using PaintDotNet.Data.PhotoshopFileType;
using PDNWrapper;
using PhotoshopFile;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// The vendored PSD parser, fed files built byte-by-byte by <see cref="PsdFixtureBuilder"/>.
/// </summary>
/// <remarks>
/// What is pinned here is the contract the PSD→UI pipeline stands on: layer names arrive in
/// unicode, rects are document-space, groups become a tree, pixels decode to the exact RGBA that
/// was authored, and hidden layers still arrive (hiding is the pipeline's decision, not the
/// parser's).
/// </remarks>
public sealed class PsdParserTests
{
    private static Document Load(byte[] psd)
    {
        using var stream = new MemoryStream(psd);
        return PsdLoad.Load(stream);
    }

    private static PsdFile LoadRaw(byte[] psd)
    {
        using var stream = new MemoryStream(psd);
        return PsdLoad.Load(stream, ELoadFlag.All);
    }

    // ------------------------------------------------------------------
    //  Header and structure
    // ------------------------------------------------------------------

    [Fact]
    public void HeaderDimensionsAndModeAreRead()
    {
        byte[] psd = new PsdFixtureBuilder(640, 360)
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "bg", Width = 640, Height = 360 })
            .Build();

        PsdFile file = LoadRaw(psd);

        Assert.Equal(640, file.ColumnCount);
        Assert.Equal(360, file.RowCount);
        Assert.Equal(8, file.BitDepth);
        Assert.Equal(PsdColorMode.RGB, file.ColorMode);
        Assert.Single(file.Layers);
    }

    [Fact]
    public void ANonPsdStreamIsRejectedUpFront()
    {
        var junk = new byte[64];
        Assert.Throws<PsdInvalidException>(() => Load(junk));
    }

    [Fact]
    public void LayerNamesComeFromTheUnicodeBlock()
    {
        // The pascal-string name is ASCII-lossy; luni carries the real one. A CJK layer name is the
        // common case for this pipeline, not the edge case.
        byte[] psd = new PsdFixtureBuilder(64, 64)
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "登录按钮", Width = 32, Height = 16 })
            .Build();

        Document document = Load(psd);

        Assert.Equal("登录按钮", document.Layers.Single().Name);
    }

    [Fact]
    public void LayerRectIsDocumentSpace()
    {
        byte[] psd = new PsdFixtureBuilder(320, 200)
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "panel", X = 40, Y = 30, Width = 100, Height = 60 })
            .Build();

        BitmapLayer layer = Load(psd).Layers.Single();

        Assert.Equal(new Rectangle(40, 30, 100, 60), layer.documentRect);
        Assert.Equal(new Rectangle(0, 0, 100, 60), layer.localRect);
    }

    [Fact]
    public void LayerIdOpacityAndVisibilityAreCarried()
    {
        byte[] psd = new PsdFixtureBuilder(64, 64)
            .Layer(new PsdFixtureBuilder.LayerSpec
            {
                Name = "ghost",
                Width = 8,
                Height = 8,
                Visible = false,
                Opacity = 128,
                LayerId = 42,
            })
            .Build();

        BitmapLayer layer = Load(psd).Layers.Single();

        // A hidden layer still arrives — dropping it is the importer's call to make, with the flag
        // in hand, not the parser's to make silently.
        Assert.False(layer.Visible);
        Assert.Equal(128, layer.Opacity);
        Assert.Equal(42, layer.LayerID);
    }

    // ------------------------------------------------------------------
    //  Groups
    // ------------------------------------------------------------------

    [Fact]
    public void GroupsBecomeATree()
    {
        byte[] psd = new PsdFixtureBuilder(200, 200)
            .Group("header")
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "title", X = 10, Y = 10, Width = 80, Height = 20 })
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "icon", X = 100, Y = 5, Width = 30, Height = 30 })
            .EndGroup()
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "bg", Width = 200, Height = 200 })
            .Build();

        Document document = Load(psd);

        Assert.Equal(2, document.Layers.Count);

        // The plugin walks Photoshop's bottom-to-top storage in reverse, so the document lists
        // roots (and children) top-most first — the same order a layers panel shows.
        BitmapLayer header = document.Layers[0];
        BitmapLayer bg = document.Layers[1];
        Assert.Equal("header", header.Name);
        Assert.Equal("bg", bg.Name);
        Assert.True(header.IsGroup);
        Assert.Equal(new[] { "title", "icon" }, header.ChildLayer.Select(c => c.Name));
        Assert.All(header.ChildLayer, c => Assert.Same(header, c.ParentLayer));
    }

    [Fact]
    public void AGroupsBoundsAreTheUnionOfItsChildren()
    {
        byte[] psd = new PsdFixtureBuilder(200, 200)
            .Group("box")
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "a", X = 10, Y = 10, Width = 20, Height = 20 })
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "b", X = 50, Y = 40, Width = 30, Height = 10 })
            .EndGroup()
            .Build();

        BitmapLayer group = Load(psd).Layers.Single();

        // The folder record itself has an empty rect; its meaningful bounds enclose the children —
        // that union is what the UI pipeline turns into a container's rect.
        Assert.Equal(Rectangle.FromLTRB(10, 10, 80, 50), group.documentRect);
    }

    [Fact]
    public void NestedGroupsNestTheTree()
    {
        byte[] psd = new PsdFixtureBuilder(100, 100)
            .Group("outer")
            .Group("inner")
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "leaf", X = 1, Y = 2, Width = 3, Height = 4 })
            .EndGroup()
            .EndGroup()
            .Build();

        BitmapLayer outer = Load(psd).Layers.Single();
        BitmapLayer inner = outer.ChildLayer.Single();
        BitmapLayer leaf = inner.ChildLayer.Single();

        Assert.True(outer.IsGroup);
        Assert.True(inner.IsGroup);
        Assert.False(leaf.IsGroup);
        Assert.Equal("leaf", leaf.Name);
        Assert.Same(inner, leaf.ParentLayer);
        Assert.Same(outer, inner.ParentLayer);
    }

    // ------------------------------------------------------------------
    //  Pixels
    // ------------------------------------------------------------------

    [Fact]
    public void RawPixelsDecodeExactly()
    {
        var pixels = new byte[4 * 2 * 4];
        var random = new Random(7);
        random.NextBytes(pixels);

        byte[] psd = new PsdFixtureBuilder(16, 16)
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "noise", Width = 4, Height = 2, Pixels = pixels })
            .Build();

        BitmapLayer layer = Load(psd).Layers.Single();

        AssertPixelsEqual(pixels, layer, 4, 2);
    }

    [Fact]
    public void RlePixelsDecodeExactly()
    {
        // A gradient stresses the row-length table; the always-literal encoding in the builder is
        // still real PackBits, so this walks the reader's actual RLE path.
        var pixels = new byte[8 * 3 * 4];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);

        byte[] psd = new PsdFixtureBuilder(16, 16)
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "grad", Width = 8, Height = 3, Pixels = pixels, Rle = true })
            .Build();

        BitmapLayer layer = Load(psd).Layers.Single();

        AssertPixelsEqual(pixels, layer, 8, 3);
    }

    [Fact]
    public void AlphaSurvivesDecoding()
    {
        byte[] psd = new PsdFixtureBuilder(32, 32)
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "veil", Width = 4, Height = 4, Fill = (10, 20, 30, 77) })
            .Build();

        BitmapLayer layer = Load(psd).Layers.Single();

        Color32 pixel = layer.Surface.color[0];
        Assert.Equal((10, 20, 30, 77), (pixel.r, pixel.g, pixel.b, pixel.a));
    }

    [Fact]
    public void ALayerlessFileFallsBackToTheComposite()
    {
        // Flattened exports have zero layer records; the composite becomes one background layer.
        byte[] psd = new PsdFixtureBuilder(6, 5).Build();

        Document document = Load(psd);

        BitmapLayer background = document.Layers.Single();
        Assert.Equal(new Rectangle(0, 0, 6, 5), background.documentRect);
        Assert.False(background.IsGroup);
    }

    [Fact]
    public void TheCompositeReflectsTheLayerContent()
    {
        byte[] psd = new PsdFixtureBuilder(4, 4)
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "red", Width = 4, Height = 4, Fill = (200, 0, 0, 255) })
            .Build();

        PsdFile file = LoadRaw(psd);

        // BaseLayer is the flattened composite — the pixels psd_render will hand to image diffing.
        Channel redChannel = file.BaseLayer.Channels.First(c => c.ID == 0);
        Assert.Equal(200, redChannel.ImageData[0]);
    }

    private static void AssertPixelsEqual(byte[] expectedRgba, BitmapLayer layer, int width, int height)
    {
        Assert.Equal(width * height, layer.Surface.color.Length);
        for (int i = 0; i < width * height; i++)
        {
            Color32 actual = layer.Surface.color[i];
            Assert.Equal(
                (expectedRgba[i * 4], expectedRgba[i * 4 + 1], expectedRgba[i * 4 + 2], expectedRgba[i * 4 + 3]),
                (actual.r, actual.g, actual.b, actual.a));
        }
    }
}
