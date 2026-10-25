// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Linq;

using XEngine.Scribe;

namespace Tests;

/// <summary>
/// Characters no font maps must lay out as the font's .notdef ("tofu") instead of silently
/// vanishing - an invisible string looks like a renderer bug and is undebuggable, a row of
/// boxes says "missing glyphs" at a glance. Default-ignorable codepoints (ZWSP, ZWJ, ...)
/// stay invisible by design.
/// </summary>
public class MissingGlyphTests
{
    class TestFontRenderer : IFontRenderer
    {
        public object CreateTexture(int width, int height) => new byte[width * height];
        public void UpdateTextureRegion(object texture, AtlasRect bounds, byte[] data) { }
        public void DrawQuads(object texture, ReadOnlySpan<IFontRenderer.Vertex> vertices, ReadOnlySpan<int> indices) { }
    }

    private const int CjkCodepoint = 0x767B; // 登

    // A regular system font that covers basic Latin but not CJK - the coverage gap under test.
    static (FontSystem fs, FontFile font)? CreateSystemWithoutCjk()
    {
        var fs = new FontSystem(new TestFontRenderer());
        foreach (var font in fs.EnumerateSystemFonts())
        {
            if (font.Style != FontStyle.Regular) continue;
            if (font.FindGlyphIndex('A') <= 0 || font.FindGlyphIndex('b') <= 0) continue;
            if (font.FindGlyphIndex(CjkCodepoint) != 0) continue;
            return (fs, font);
        }
        return null; // every installed font covers CJK - nothing to exercise on this machine
    }

    [Fact]
    public void DifferentStyleFallback_PreservesCjkCoverage()
    {
        var fs = new FontSystem(new TestFontRenderer());
        var primary = new FontFile(System.IO.Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
        var fallback = new FontFile(System.IO.Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
        // Make the primary lack 'A' through its existing lookup cache, avoiding an OS-font
        // dependency while exercising the same missing-codepoint path used for Chinese.
        var cache = (System.Collections.Generic.Dictionary<int, int>)typeof(FontFile)
            .GetField("unicodeMapCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(primary)!;
        cache['A'] = 0;
        typeof(FontFile).GetProperty(nameof(FontFile.Style))!.SetValue(primary, FontStyle.Bold);
        fs.AddFallbackFont(fallback);
        var glyph = fs.GetOrCreateGlyph('A', primary, FontQuality.Normal);
        Assert.NotNull(glyph);
        Assert.Same(fallback, glyph.Font);
        Assert.NotEqual(0, glyph.GlyphIndex);
    }

    [Fact]
    public void MissingGlyphsLayoutAsNotdefInsteadOfVanishing()
    {
        var env = CreateSystemWithoutCjk();
        if (env == null) return;
        var (fs, font) = env.Value;

        var settings = TextLayoutSettings.Default;
        settings.Font = font;
        settings.PixelSize = 32;

        var layout = fs.CreateLayout("登录", settings);
        var glyphs = layout.Lines.SelectMany(l => l.Glyphs).ToList();

        Assert.Equal(2, glyphs.Count);
        Assert.All(glyphs, g => Assert.Equal(0, g.Glyph.GlyphIndex));
        Assert.All(glyphs, g => Assert.True(g.AdvanceWidth > 0f, "notdef must advance the pen"));
        Assert.True(layout.Size.X > 0f, "a string of missing glyphs still occupies space");
    }

    [Fact]
    public void DefaultIgnorableCodepointsDoNotBecomeTofu()
    {
        var env = CreateSystemWithoutCjk();
        if (env == null) return;
        var (fs, font) = env.Value;

        var settings = TextLayoutSettings.Default;
        settings.Font = font;
        settings.PixelSize = 32;

        // ZWSP between two letters: some fonts map it (zero-width glyph), none should tofu it.
        var layout = fs.CreateLayout("a​b", settings);
        var glyphs = layout.Lines.SelectMany(l => l.Glyphs).ToList();

        Assert.DoesNotContain(glyphs, g => g.Glyph.GlyphIndex == 0);
        Assert.True(FontSystem.IsDefaultIgnorable(0x200B));
        Assert.True(FontSystem.IsDefaultIgnorable(0xFE0F));
        Assert.False(FontSystem.IsDefaultIgnorable(CjkCodepoint));
        Assert.False(FontSystem.IsDefaultIgnorable('A'));
    }

    [Fact]
    public void RichTextMissingGlyphsLayoutAsNotdef()
    {
        var env = CreateSystemWithoutCjk();
        if (env == null) return;
        var (fs, font) = env.Value;

        var settings = new RichTextLayoutSettings
        {
            RegularFont = font,
            BoldFont = font,
            ItalicFont = font,
            BoldItalicFont = font,
            MonoFont = font,
            PixelSize = 32,
            Quality = FontQuality.Normal,
            WrapMode = TextWrapMode.NoWrap,
            Alignment = TextAlignment.Left,
            DefaultColor = new FontColor(255, 255, 255, 255),
        };
        var layout = new RichTextLayout("登录", settings);
        layout.Update(fs);

        Assert.Equal(2, layout.Glyphs.Count);
        Assert.All(layout.Glyphs, g => Assert.Equal(0, g.Glyph.GlyphIndex));
        Assert.All(layout.Glyphs, g => Assert.True(g.Advance > 0f));
        Assert.True(layout.Size.X > 0f);
    }

    [Fact]
    public void NotdefGlyphResolvesWithMetrics()
    {
        var env = CreateSystemWithoutCjk();
        if (env == null) return;
        var (fs, font) = env.Value;

        var notdef = fs.GetOrCreateGlyphByIndex(0, font, FontQuality.Normal);
        Assert.NotNull(notdef);
        Assert.Equal(0, notdef.GlyphIndex);

        var metrics = fs.GetGlyphMetricsByIndex(font, 0, 32f);
        Assert.NotNull(metrics);
        Assert.True(metrics.Value.AdvanceWidth > 0f);
    }
}
