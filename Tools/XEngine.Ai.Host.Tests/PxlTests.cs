using System;
using System.IO;
using System.Linq;

using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.XUI.Core;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// The <c>.pxl</c> pixel-grid format. The properties that matter: every error names its line (a grid is
/// a wall of characters, so "row 7 is 11 wide" is the difference between a one-character fix and
/// re-reading the file), and the rasterizer never smooths anything — a wrong pixel has to stay visible.
/// </summary>
public sealed class PxlTests : IDisposable
{
    private const string DocumentPath = "UI/art/button.pxl";

    private const string Button = """
        # 6x4 button skin
        ppu: 16
        chars:
          K: #1b1b26
          H: #6f7ea8
          M: #3b4a72

        [normal]
        border: 1,1,1,1
        grid:
          .KKKK.
          KHHHHK
          KMMMMK
          .KKKK.

        [pressed]
        border: 1,1,1,1
        grid:
          .KKKK.
          KMMMMK
          KHHHHK
          .KKKK.
        """;

    private readonly string _root;
    private readonly PxlService _service;

    public PxlTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-pxl-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "UI", "art"));
        Write(Button);
        _service = new PxlService(new ProjectDescriptor("pxl-test", _root, null, null, null, "fingerprint"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* test cleanup */ }
    }

    private void Write(string text)
        => File.WriteAllText(Path.Combine(_root, "Assets", "UI", "art", "button.pxl"), text);

    private static PxlDocument Parse(string text) => PxlParser.Parse(text, "t.pxl");

    // ------------------------------------------------------------------
    //  Parser
    // ------------------------------------------------------------------

    [Fact]
    public void ParsesDirectivesSectionsAndBorders()
    {
        PxlDocument document = Parse(Button);

        Assert.Equal(16f, document.PixelsPerUnit);
        Assert.Equal(2, document.Sections.Count);

        PxlSection normal = document.FindSection("normal")!;
        Assert.Equal(6, normal.Width);
        Assert.Equal(4, normal.Height);
        Assert.True(normal.HasBorder);
        Assert.Equal(1, normal.BorderTop);
    }

    [Fact]
    public void DotIsTransparentAndCannotBeRedefined()
    {
        Assert.Equal(0f, Parse(Button).ColorOf('.').A);

        var ex = Assert.Throws<PxlParseException>(() => Parse("""
            chars:
              .: #ffffff
            grid:
              ..
            """));
        Assert.Contains("cannot be redefined", ex.RawMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void RaggedRowNamesItsLineAndBothWidths()
    {
        var ex = Assert.Throws<PxlParseException>(() => Parse("""
            chars:
              K: #000000
            grid:
              KKKK
              KKK
            """));

        Assert.Contains("3 characters wide", ex.RawMessage, StringComparison.Ordinal);
        Assert.Contains("grid is 4", ex.RawMessage, StringComparison.Ordinal);
        Assert.Equal(5, ex.Line);
    }

    [Fact]
    public void BorderLeavingNoStretchableMiddleIsRejected()
    {
        var ex = Assert.Throws<PxlParseException>(() => Parse("""
            chars:
              K: #000000
            [frame]
            border: 0,3,0,3
            grid:
              KKKKK
              KKKKK
            """));

        Assert.Contains("no stretchable middle", ex.RawMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ppu: 0", "positive number")]
    [InlineData("ppu: -4", "positive number")]
    [InlineData("palette: ui", "@name")]
    [InlineData("wobble: 3", "Unknown directive")]
    public void MalformedDirectivesAreRejected(string directive, string expected)
    {
        var ex = Assert.Throws<PxlParseException>(
            () => Parse(directive + "\nchars:\n  K: #000\ngrid:\n  K\n"));
        Assert.Contains(expected, ex.RawMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateSectionIsRejected()
    {
        var ex = Assert.Throws<PxlParseException>(() => Parse("""
            chars:
              K: #000000
            [a]
            grid:
              K
            [a]
            grid:
              K
            """));
        Assert.Contains("declared twice", ex.RawMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithNoSectionHeaderStillHasOneGrid()
    {
        PxlDocument document = Parse("chars:\n  K: #000000\ngrid:\n  KK\n  KK\n");

        PxlSection section = Assert.Single(document.Sections);
        Assert.Equal("default", section.Name);
        Assert.Equal(2, section.Width);
    }

    [Fact]
    public void CommentsAreWholeLinesOnly()
    {
        // A trailing comment would make the colour name literally "#000000  # outline", so the parser
        // keeps it a name and lint reports it rather than guessing.
        PxlDocument document = Parse("# header\nchars:\n  K: #000000\n# between\ngrid:\n  K\n");
        Assert.Single(document.Chars);
    }

    // ------------------------------------------------------------------
    //  Rasterizer
    // ------------------------------------------------------------------

    [Fact]
    public void RasterizesTopRowFirstWithExactColours()
    {
        PxlDocument document = Parse(Button);
        PxlSection normal = document.FindSection("normal")!;

        byte[] rgba = PxlRasterizer.Rasterize(document, normal, scale: 1);

        Assert.Equal(6 * 4 * 4, rgba.Length);
        // Top-left is '.', transparent.
        Assert.Equal(0, rgba[3]);
        // Second pixel of the top row is 'K' = #1b1b26.
        Assert.Equal(0x1b, rgba[4]);
        Assert.Equal(0x1b, rgba[5]);
        Assert.Equal(0x26, rgba[6]);
        Assert.Equal(255, rgba[7]);
    }

    [Fact]
    public void UpscaleIsNearestNeighbourSoASinglePixelStaysReadable()
    {
        PxlDocument document = Parse("chars:\n  K: #ff0000\ngrid:\n  K.\n  .K\n");
        PxlSection section = document.Sections[0];

        byte[] rgba = PxlRasterizer.Rasterize(document, section, scale: 4);

        Assert.Equal(8 * 8 * 4, rgba.Length);
        // Every pixel of the top-left 4x4 block is the same solid red — no blending at the edge.
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int i = (y * 8 + x) * 4;
                Assert.Equal(255, rgba[i]);
                Assert.Equal(0, rgba[i + 1]);
                Assert.Equal(255, rgba[i + 3]);
            }
        }
        // And its neighbour is fully transparent, not a half-blended edge.
        Assert.Equal(0, rgba[(0 * 8 + 4) * 4 + 3]);
    }

    [Fact]
    public void UndeclaredCharactersRasterizeTransparentRatherThanThrowing()
    {
        PxlDocument document = Parse("chars:\n  K: #ffffff\ngrid:\n  KZ\n");

        byte[] rgba = PxlRasterizer.Rasterize(document, document.Sections[0], scale: 1);

        Assert.Equal(255, rgba[3]);
        Assert.Equal(0, rgba[7]);
    }

    [Fact]
    public void SheetStacksSectionsWithAGapAndReportsWhereEachLanded()
    {
        PxlDocument document = Parse(Button);

        PxlRasterizer.RasterizeSheet(document, 1, out int width, out int height, out var entries);

        Assert.Equal(6, width);
        Assert.Equal(4 + 1 + 4, height);
        Assert.Equal(2, entries.Count);
        Assert.Equal(0, entries[0].Y);
        Assert.Equal(5, entries[1].Y);
    }

    // ------------------------------------------------------------------
    //  Tools
    // ------------------------------------------------------------------

    [Fact]
    public void ValidateReportsSectionsAndAcceptsAGoodFile()
    {
        PxlValidateResult result = _service.Validate(DocumentPath);

        Assert.True(result.Valid, string.Join("; ", result.Issues.Select(i => i.Message)));
        Assert.Equal(2, result.SectionCount);
        Assert.Equal(16f, result.PixelsPerUnit);
        Assert.Equal("1,1,1,1", result.Sections[0].Border);
    }

    [Fact]
    public void ValidateFlagsACharacterTheGridUsesButNeverDeclares()
    {
        Write("chars:\n  K: #000000\ngrid:\n  KZ\n");

        PxlValidateResult result = _service.Validate(DocumentPath);

        Assert.False(result.Valid);
        PxlIssueInfo issue = Assert.Single(result.Issues);
        Assert.Equal("PXL-CHAR-UNDECLARED", issue.Code);
        Assert.Contains("renders transparent", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateFlagsAColourNameNothingResolved()
    {
        Write("chars:\n  K: midnight\ngrid:\n  K\n");

        PxlValidateResult result = _service.Validate(DocumentPath);

        Assert.False(result.Valid);
        Assert.Contains(result.Issues, i => i.Code == "PXL-COLOR-UNRESOLVED");
    }

    [Fact]
    public void ValidateReportsADeclaredCharacterNothingUses()
    {
        Write("chars:\n  K: #000000\n  Q: #ffffff\ngrid:\n  K\n");

        PxlValidateResult result = _service.Validate(DocumentPath);

        Assert.True(result.Valid);
        Assert.Equal("Q", Assert.Single(result.UnusedChars));
    }

    [Fact]
    public void ValidateTurnsAParseFailureIntoOneIssueWithItsLine()
    {
        Write("chars:\n  K: #000000\ngrid:\n  KKK\n  KK\n");

        PxlValidateResult result = _service.Validate(DocumentPath);

        Assert.False(result.Valid);
        PxlIssueInfo issue = Assert.Single(result.Issues);
        Assert.Equal("PXL-PARSE", issue.Code);
        Assert.Equal(5, issue.Line);
    }

    [Fact]
    public void RenderWritesOnePngPerSectionAtTheRequestedScale()
    {
        PxlRenderResult result = _service.Render(DocumentPath, null, scale: 4, outputFolder: null);

        Assert.Equal(4, result.Scale);
        Assert.Equal(2, result.Images.Count);
        foreach (PxlRenderedImage image in result.Images)
        {
            Assert.Equal(24, image.Width);
            Assert.Equal(16, image.Height);
            string path = Path.Combine(_root, image.Path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "missing " + path);
            // A real PNG signature, not an empty file.
            byte[] header = File.ReadAllBytes(path)[..8];
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header);
        }
    }

    [Fact]
    public void RenderCanTargetOneSection()
    {
        PxlRenderResult result = _service.Render(DocumentPath, "pressed", scale: 2, outputFolder: null);

        Assert.Equal("pressed", Assert.Single(result.Images).Section);
    }

    [Fact]
    public void UnknownSectionNamesTheToolThatListsThem()
    {
        var ex = Assert.Throws<KeyNotFoundException>(
            () => _service.Render(DocumentPath, "nope", 2, null));

        Assert.Contains("pxl_validate", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsurdScaleIsRefusedRatherThanAllocatingGigabytes()
    {
        Write("chars:\n  K: #000000\ngrid:\n" + string.Join("\n", Enumerable.Repeat("  " + new string('K', 200), 200)));

        var ex = Assert.Throws<InvalidOperationException>(() => _service.Render(DocumentPath, null, 32, null));

        Assert.Contains("over the", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsRejectANonPxlPath()
        => Assert.Throws<ArgumentException>(() => _service.Validate("UI/art/button.png"));
}
