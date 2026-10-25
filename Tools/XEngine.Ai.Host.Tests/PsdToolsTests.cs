using System;
using System.IO;
using System.Linq;

using XEngine.Ai.Protocol;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// The offline <c>psd_inspect</c> / <c>psd_render</c> tools, against builder-made fixtures.
/// </summary>
public sealed class PsdToolsTests : IDisposable
{
    private readonly string _root;
    private readonly PsdService _service;

    public PsdToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-psd-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Art"));
        var project = new ProjectDescriptor("psd-test-project", _root, null, null, null, "fingerprint");
        _service = new PsdService(project);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteFixture(string name, byte[] psd)
    {
        string path = Path.Combine(_root, "Assets", "Art", name);
        File.WriteAllBytes(path, psd);
        return "Assets/Art/" + name;
    }

    private static byte[] ButtonsLikePsd()
        => new PsdFixtureBuilder(300, 100)
            .Group("Header")
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "标题", X = 20, Y = 10, Width = 100, Height = 24, LayerId = 7 })
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "icon", X = 130, Y = 12, Width = 20, Height = 20, Fill = (10, 200, 30, 255) })
            .EndGroup()
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "bg", Width = 300, Height = 100, Visible = false, Opacity = 128 })
            .Build();

    // ------------------------------------------------------------------
    //  Inspect
    // ------------------------------------------------------------------

    [Fact]
    public void InspectReportsTheTreeTheUiPipelineNeeds()
    {
        string doc = WriteFixture("shop.psd", ButtonsLikePsd());

        PsdInspectResult result = _service.Inspect(doc);

        Assert.Equal((300, 100, 8, "RGB"), (result.Width, result.Height, result.BitDepth, result.ColorMode));
        Assert.Equal(4, result.LayerCount);
        Assert.Equal(64, result.ContentHash.Length);

        PsdLayerInfo header = result.Layers[0];
        Assert.True(header.IsGroup);
        Assert.Equal("Header", header.Name);
        Assert.Equal(new[] { "标题", "icon" }, header.Children!.Select(c => c.Name));

        PsdLayerInfo title = header.Children![0];
        Assert.Equal((20, 10, 100, 24), (title.X, title.Y, title.Width, title.Height));
        Assert.Equal(7, title.LayerId);
        Assert.Null(title.Children);

        PsdLayerInfo bg = result.Layers[1];
        Assert.False(bg.Visible);
        Assert.Equal(128, bg.Opacity);
    }

    [Fact]
    public void InspectHashChangesWithTheFile()
    {
        string a = WriteFixture("a.psd", ButtonsLikePsd());
        string b = WriteFixture("b.psd", new PsdFixtureBuilder(300, 100)
            .Layer(new PsdFixtureBuilder.LayerSpec { Name = "bg", Width = 300, Height = 100 })
            .Build());

        Assert.NotEqual(_service.Inspect(a).ContentHash, _service.Inspect(b).ContentHash);
    }

    [Fact]
    public void ANonPsdPathIsRefusedByName()
        => Assert.Throws<ArgumentException>(() => _service.Inspect("Assets/Art/shop.png"));

    [Fact]
    public void AJunkFileFailsWithAClearMessage()
    {
        string doc = WriteFixture("junk.psd", new byte[128]);
        Assert.ThrowsAny<Exception>(() => _service.Inspect(doc));
    }

    // ------------------------------------------------------------------
    //  Render
    // ------------------------------------------------------------------

    [Fact]
    public void TheCompositeRenderRoundTripsThroughPng()
    {
        string doc = WriteFixture("shop.psd", ButtonsLikePsd());

        PsdRenderResult result = _service.Render(doc, layerPath: null, outputFolder: null);

        Assert.Equal((300, 100, 0, 0), (result.Width, result.Height, result.DocumentX, result.DocumentY));
        string absolute = Path.Combine(_root, result.OutputPath);
        Assert.True(File.Exists(absolute));

        byte[] rgba = PngRgba.Decode(File.ReadAllBytes(absolute), out int width, out int height);
        Assert.Equal((300, 100), (width, height));

        // The icon layer fills (130,12)-(150,32) with solid green; the composite must show it.
        int pixel = ((20 * 300) + 140) * 4;
        Assert.Equal((10, 200, 30), (rgba[pixel], rgba[pixel + 1], rgba[pixel + 2]));
    }

    [Fact]
    public void ALayerRenderCarriesItsDocumentPosition()
    {
        string doc = WriteFixture("shop.psd", ButtonsLikePsd());

        PsdRenderResult result = _service.Render(doc, "Header/icon", outputFolder: null);

        // The offsets are what lets a per-layer render be placed back where it came from —
        // xui_from_psd turns exactly these numbers into an element's pos.
        Assert.Equal((20, 20, 130, 12), (result.Width, result.Height, result.DocumentX, result.DocumentY));

        byte[] rgba = PngRgba.Decode(File.ReadAllBytes(Path.Combine(_root, result.OutputPath)), out int w, out int h);
        Assert.Equal((20, 20), (w, h));
        Assert.Equal((10, 200, 30, 255), (rgba[0], rgba[1], rgba[2], rgba[3]));
    }

    [Fact]
    public void RenderingAMissingLayerNamesTheFix()
    {
        string doc = WriteFixture("shop.psd", ButtonsLikePsd());

        var error = Assert.Throws<System.Collections.Generic.KeyNotFoundException>(
            () => _service.Render(doc, "Header/nope", outputFolder: null));

        Assert.Contains("psd_inspect", error.Message, StringComparison.Ordinal);
    }
}
