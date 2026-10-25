using XEngine.Scribe.Internal;
using XEngine.Scribe.Sdf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XEngine.Vector;
using System.Runtime.InteropServices;
using System.Text;

namespace XEngine.Scribe
{
    /// <summary>
    /// Atlas rasterization quality. The value is the per-glyph em pixel height the distance field is
    /// generated at; the shader scales it to any display size, so higher mainly helps very small text
    /// and very sharp corners, at the cost of atlas memory and one-time generation time.
    /// </summary>
    public enum FontQuality
    {
        Low = 16,
        Normal = 32,
        High = 64,
        Ultra = 128
    }

    /// <summary>SDF raster-bucket helpers shared by every text front end.</summary>
    public static class FontQualityExtensions
    {
        /// <summary>
        /// The rasterization bucket that keeps the distance-field sampling ratio near 1 when a
        /// glyph's on-screen size is known. Scribe bakes the field at the quality's em height
        /// regardless of display size, so text drawn far below its raster size (a scaled-down
        /// canvas, a small Game view / device preview) bilinearly downsamples the field ~3x and
        /// turns to mush. The smallest bucket that still covers <paramref name="effectivePixelSize"/>
        /// is picked, capped by <paramref name="authored"/> (adaptation only ever lowers — an
        /// explicit High/Ultra choice for crisp upscaled text is respected).
        /// </summary>
        public static FontQuality PickBucket(this FontQuality authored, float effectivePixelSize)
        {
            int cap = (int)authored;
            if (cap <= 0) cap = (int)FontQuality.Normal;

            int bucket = effectivePixelSize <= (int)FontQuality.Low ? (int)FontQuality.Low
                       : effectivePixelSize <= (int)FontQuality.Normal ? (int)FontQuality.Normal
                       : effectivePixelSize <= (int)FontQuality.High ? (int)FontQuality.High
                       : (int)FontQuality.Ultra;
            return (FontQuality)Math.Min(cap, bucket);
        }
    }

    public class FontSystem
    {
        private readonly IFontRenderer renderer;
        private readonly BinPacker binPacker;
        private readonly List<FontFile> fallbackFonts;
        private readonly Dictionary<AtlasGlyph.CacheKey, AtlasGlyph> glyphCache;

        readonly LruCache<LayoutCacheKey, TextLayout> layoutCache;

        private object atlasTexture;
        private int atlasWidth;
        private int atlasHeight;

        // Reusable scratch buffers for DrawLayout - avoid per-frame List/array allocations.
        private readonly List<IFontRenderer.Vertex> drawVertices = new List<IFontRenderer.Vertex>(1024);
        private readonly List<int> drawIndices = new List<int>(1536);

        private bool useWhiteRect;
        private float whiteU0, whiteV0, whiteU1, whiteV1;

        // Settings
        public bool AllowExpansion { get; set; } = true;
        public float ExpansionFactor { get; set; } = 2f;
        public int MaxAtlasSize { get; set; } = 4096;
        public int Padding { get; set; } = 1;

        /// <summary>
        /// Width of the signed-distance range in atlas pixels. Must match the value the text shader
        /// uses for its screen-pixel-range calculation. Larger ranges allow softer/larger effects.
        /// </summary>
        public float DistanceRange { get; set; } = 4f;


        int _maxLayout = 256;
        public int MaxLayoutCacheSize {
            get => _maxLayout;
            set { _maxLayout = Math.Max(1, value); layoutCache.Capacity = _maxLayout; }
        }
        bool _cacheLayouts;
        public bool CacheLayouts
        {
            get => _cacheLayouts;
            set { _cacheLayouts = value; if (!value) layoutCache.Clear(); }
        }
        /// <summary>Estimated layout-cache budget; shared fonts/atlases and caller-held layouts are excluded.</summary>
        public long MaxLayoutCacheBytes
        {
            get => layoutCache.MaximumWeight;
            set => layoutCache.MaximumWeight = value;
        }
        public long CachedLayoutEstimatedBytes => layoutCache.Weight;
        public void ClearLayoutCache() => layoutCache.Clear();
        /// <summary>Completed CreateLayout allocations, excluding in-place atlas refreshes.</summary>
        public long LayoutCreationCount { get; private set; }
        public long LayoutCacheHitCount { get; private set; }
        public int CachedLayoutCount => layoutCache.Count;

        public IEnumerable<FontFile> FallbackFonts => fallbackFonts;
        public int Width => atlasWidth;
        public int Height => atlasHeight;
        public object Texture => atlasTexture;
        public int FontCount => fallbackFonts.Count;

        /// <summary>
        /// Monotonically-increasing counter bumped every time the atlas is rebuilt/resized.
        /// <para>
        /// When the atlas grows, the backing texture is recreated and every cached
        /// <see cref="AtlasGlyph"/> is invalidated - their UVs and atlas positions belong to the
        /// previous texture. Any <see cref="TextLayout"/> created before the resize still holds
        /// references to those stale <see cref="AtlasGlyph"/> objects.
        /// </para>
        /// <para>
        /// Each <see cref="TextLayout"/> stamps <see cref="TextLayout.AtlasVersion"/> when it's
        /// built; consumers compare against this value (or call
        /// <see cref="TextLayout.EnsureUpToDate"/>) to detect staleness and re-layout.
        /// <see cref="DrawLayout"/> does the check automatically.
        /// </para>
        /// </summary>
        public int AtlasVersion { get; private set; }

        public FontSystem(IFontRenderer renderer, int initialWidth = 512, int initialHeight = 512, bool includeWhiteRect = true)
        {
            this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

            atlasWidth = initialWidth;
            atlasHeight = initialHeight;

            this.useWhiteRect = includeWhiteRect;

            atlasTexture = CreateEmptyAtlas(atlasWidth, atlasHeight);
            binPacker = new BinPacker(atlasWidth, atlasHeight);
            fallbackFonts = new List<FontFile>();
            glyphCache = new Dictionary<AtlasGlyph.CacheKey, AtlasGlyph>();
            layoutCache = new LruCache<LayoutCacheKey, TextLayout>(_maxLayout) { MaximumWeight = 16 * 1024 * 1024 };

            // Add a small white rectangle for rendering
            if (useWhiteRect)
                AddWhiteRect();
        }

        public void AddWhiteRect()
        {
            if (binPacker.TryPack(4 + Padding * 2, 4 + Padding * 2, out int x, out int y))
            {
                // RGBA, fully opaque white. In the SDF text shader the median of (1,1,1) reads as
                // fully inside, so this rect still renders as a solid fill.
                byte[] whiteData = new byte[4 * 4 * 4];
                Array.Fill<byte>(whiteData, 255);

                renderer.UpdateTextureRegion(atlasTexture,
                    new AtlasRect(x, y, 4, 4), whiteData);

                whiteU0 = (float)x / atlasWidth;
                whiteV0 = (float)y / atlasHeight;
                whiteU1 = (float)(x + 1) / atlasWidth;
                whiteV1 = (float)(y + 1) / atlasHeight;
            }
        }

        public void AddFallbackFont(FontFile font)
        {
            fallbackFonts.Add(font);

            // Fallback list changed -> cached glyphs may resolve to different fonts now.
            glyphCache.Clear();
            layoutCache.Clear();
            AtlasVersion++;
        }

        /// <summary>Adopt another font system's ordered fallback configuration without
        /// sharing its atlas. Font files remain borrowed, as with AddFallbackFont.</summary>
        public void SynchronizeFallbackFonts(FontSystem source)
        {
#if NETSTANDARD2_1
            if (source is null) throw new ArgumentNullException(nameof(source));
#else
            ArgumentNullException.ThrowIfNull(source);
#endif
            if (ReferenceEquals(this, source)) return;
            bool equal = fallbackFonts.Count == source.fallbackFonts.Count;
            for (int i = 0; equal && i < fallbackFonts.Count; i++)
                equal = ReferenceEquals(fallbackFonts[i], source.fallbackFonts[i]);
            if (equal) return;
            fallbackFonts.Clear();
            fallbackFonts.AddRange(source.fallbackFonts);
            glyphCache.Clear(); layoutCache.Clear(); AtlasVersion++;
        }

        public IEnumerable<FontFile> EnumerateSystemFonts()
        {
            var paths = GetSystemFontPaths();
            foreach (var path in paths)
            {
                FontFile font = null;
                try
                {
                    font = new FontFile(path);
                }
                catch
                {
                    continue; // Silently skip problematic fonts
                }
                if (font != null)
                    yield return font;
            }
        }

        private IEnumerable<string> GetSystemFontPaths()
        {
            // De-dupe final results
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Safe enumerator that handles permissions and missing dirs
            IEnumerable<string> EnumerateFontsUnder(string root)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    yield break;

                var stack = new Stack<string>();
                stack.Push(root);

                while (stack.Count > 0)
                {
                    string dir = stack.Pop();

                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(dir); }
                    catch { files = Array.Empty<string>(); }

                    foreach (var f in files)
                    {
                        string ext;
                        try { ext = Path.GetExtension(f); }
                        catch { continue; }

                        // .ttc (TrueType Collection) is how Windows ships its CJK fonts (msyh.ttc /
                        // simsun.ttc); FontFile resolves the requested face via GetFontOffsetForIndex.
                        if ((ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
                             || ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)
                             || ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase)) && yielded.Add(f))
                            yield return f;
                    }

                    IEnumerable<string> subdirs;
                    try { subdirs = Directory.EnumerateDirectories(dir); }
                    catch { subdirs = Array.Empty<string>(); }

                    foreach (var d in subdirs)
                        stack.Push(d);
                }
            }

            // Build OS-specific search roots
            var roots = new List<string>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // System fonts
                roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));

                // Per-user fonts (Windows 10+)
                var userFonts = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "Fonts");
                roots.Add(userFonts);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // System & local fonts
                roots.Add("/usr/share/fonts");
                roots.Add("/usr/local/share/fonts");

                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                roots.Add(Path.Combine(home, ".fonts"));                  // legacy
                roots.Add(Path.Combine(home, ".local", "share", "fonts"));// modern
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // System & local fonts
                roots.Add("/System/Library/Fonts");
                roots.Add("/System/Library/Fonts/Supplemental");
                roots.Add("/Library/Fonts");

                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                roots.Add(Path.Combine(home, "Library", "Fonts"));
            }

            foreach (var r in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var f in EnumerateFontsUnder(r))
                    yield return f;
        }

        public AtlasGlyph GetOrCreateGlyph(int codepoint, FontFile font, FontQuality quality)
        {
            if(font == null) throw new ArgumentNullException(nameof(font));

            // An unset/default FontQuality (0) is not a valid rasterization size, so callers that
            // build TextLayoutSettings without specifying Quality would otherwise get no glyphs.
            if ((int)quality <= 0) quality = FontQuality.Normal;

            int gi = font.FindGlyphIndex(codepoint);
            if (gi > 0)
                return GetOrAddGlyph(font, gi, quality);

            // Check fallback fonts (must match the requested style).
            foreach (var f in fallbackFonts)
            {
                if (f == font) continue;
                if (f.Style != font.Style) continue;
                int fgi = f.FindGlyphIndex(codepoint);
                if (fgi > 0)
                    return GetOrAddGlyph(f, fgi, quality);
            }

            // Coverage takes precedence over weight/style when no matching face exists.
            // CJK fallback collections often expose only Regular; bold headings must still
            // render readable characters instead of .notdef boxes.
            foreach (var f in fallbackFonts)
            {
                if (f == font || f.Style == font.Style) continue;
                int fgi = f.FindGlyphIndex(codepoint);
                if (fgi > 0)
                    return GetOrAddGlyph(f, fgi, quality);
            }

            return null; // Glyph not found in any font
        }

        /// <summary>
        /// Resolves a codepoint like <see cref="GetOrCreateGlyph"/>, but returns the requested
        /// font's .notdef glyph (index 0 - "tofu") instead of null when no font maps the
        /// codepoint. A visible missing-glyph box makes a coverage gap debuggable; a silently
        /// dropped character just looks like the text never rendered. Default-ignorable
        /// codepoints (ZWSP, ZWJ, variation selectors, ...) still return null - they are
        /// invisible by design.
        /// </summary>
        public AtlasGlyph GetOrCreateGlyphOrNotdef(int codepoint, FontFile font, FontQuality quality)
        {
            var glyph = GetOrCreateGlyph(codepoint, font, quality);
            if (glyph != null) return glyph;
            if (font == null || IsDefaultIgnorable(codepoint)) return null;
            if ((int)quality <= 0) quality = FontQuality.Normal;
            return GetOrAddGlyph(font, 0, quality);
        }

        /// <summary>Codepoints that are invisible by design (Default_Ignorable_Code_Point) and
        /// must not render a tofu box when the font has no glyph for them.</summary>
        public static bool IsDefaultIgnorable(int cp)
            => cp == 0x00AD                        // soft hyphen
            || cp == 0x034F                        // combining grapheme joiner
            || cp == 0x061C                        // Arabic letter mark
            || (cp >= 0x180B && cp <= 0x180E)      // Mongolian variation selectors / vowel separator
            || (cp >= 0x200B && cp <= 0x200F)      // ZWSP, ZWNJ, ZWJ, LRM, RLM
            || (cp >= 0x202A && cp <= 0x202E)      // bidi embedding controls
            || (cp >= 0x2060 && cp <= 0x206F)      // word joiner, invisible operators, deprecated controls
            || (cp >= 0xFE00 && cp <= 0xFE0F)      // variation selectors
            || cp == 0xFEFF                        // zero width no-break space / BOM
            || (cp >= 0xFFF9 && cp <= 0xFFFB)      // interlinear annotation controls
            || (cp >= 0xE0000 && cp <= 0xE0FFF);   // tags / variation selector supplement

        /// <summary>
        /// Returns the atlas glyph for a specific glyph index in a specific font (no codepoint
        /// lookup, no fallback). Used by the shaper for substituted glyphs such as ligatures.
        /// Index 0 is the font's .notdef ("tofu") and is a valid, renderable glyph.
        /// </summary>
        public AtlasGlyph GetOrCreateGlyphByIndex(int glyphIndex, FontFile font, FontQuality quality)
        {
            if (font == null) throw new ArgumentNullException(nameof(font));
            if ((int)quality <= 0) quality = FontQuality.Normal;
            if (glyphIndex < 0) return null;
            return GetOrAddGlyph(font, glyphIndex, quality);
        }

        private AtlasGlyph GetOrAddGlyph(FontFile font, int glyphIndex, FontQuality quality)
        {
            var key = new AtlasGlyph.CacheKey(glyphIndex, quality, font);
            if (glyphCache.TryGetValue(key, out var cachedGlyph))
                return cachedGlyph;

            var glyph = AtlasGlyph.FromGlyphIndex(font, glyphIndex, quality);

            if (TryAddGlyphToAtlas(glyph))
            {
                glyphCache[key] = glyph;
                return glyph;
            }

            if (AllowExpansion && TryExpandAtlas(glyph) && TryAddGlyphToAtlas(glyph))
            {
                glyphCache[key] = glyph;
                return glyph;
            }

            glyphCache[key] = glyph;
            return glyph;
        }

        // Rasterizes a glyph's distance field into the atlas once per quality. The field is
        // resolution independent, so the single entry serves every requested display size.
        private bool TryAddGlyphToAtlas(AtlasGlyph glyph)
        {
            if (!SdfScanlineGenerator.TryGenerate(glyph.Font, glyph.GlyphIndex, (int)glyph.Quality, DistanceRange, out var result))
                return true; // Empty glyph (e.g. space), nothing to pack

            int packWidth = result.Width + Padding * 2;
            int packHeight = result.Height + Padding * 2;

            if (binPacker.TryPack(packWidth, packHeight, out int x, out int y))
            {
                glyph.AtlasX = x + Padding;
                glyph.AtlasY = y + Padding;
                glyph.AtlasWidth = result.Width;
                glyph.AtlasHeight = result.Height;

                // Calculate texture coordinates
                glyph.U0 = (float)glyph.AtlasX / atlasWidth;
                glyph.V0 = (float)glyph.AtlasY / atlasHeight;
                glyph.U1 = (float)(glyph.AtlasX + glyph.AtlasWidth) / atlasWidth;
                glyph.V1 = (float)(glyph.AtlasY + glyph.AtlasHeight) / atlasHeight;

                // Padded glyph region in font units (Y up) - scaled per display size at draw time.
                glyph.RegionX0 = result.Rx0;
                glyph.RegionY0 = result.Ry0;
                glyph.RegionX1 = result.Rx1;
                glyph.RegionY1 = result.Ry1;

                // Upload distance field to atlas
                renderer.UpdateTextureRegion(atlasTexture,
                    new AtlasRect(glyph.AtlasX, glyph.AtlasY, glyph.AtlasWidth, glyph.AtlasHeight),
                    result.Rgba);

                return true;
            }

            return false;
        }

        private bool TryExpandAtlas(AtlasGlyph glyph)
        {
            if (!SdfScanlineGenerator.TryGenerate(glyph.Font, glyph.GlyphIndex, (int)glyph.Quality, DistanceRange, out var result))
                return true;

            int requiredWidth = result.Width + Padding * 2;
            int requiredHeight = result.Height + Padding * 2;

            int newWidth = Math.Max(atlasWidth, (int)(atlasWidth * ExpansionFactor));
            int newHeight = Math.Max(atlasHeight, (int)(atlasHeight * ExpansionFactor));

            // Ensure we can fit the glyph
            newWidth = Math.Max(newWidth, atlasWidth + requiredWidth);
            newHeight = Math.Max(newHeight, atlasHeight + requiredHeight);

            // Respect max size
            if (newWidth > MaxAtlasSize || newHeight > MaxAtlasSize)
                return false;

            // Admission or backend allocation may fail. Keep the old dimensions, texture
            // and cache version intact until a replacement exists so callers can retry.
            object replacement = CreateEmptyAtlas(newWidth, newHeight);
            atlasWidth = newWidth;
            atlasHeight = newHeight;
            atlasTexture = replacement;

            // Clear bin packer and glyph cache
            binPacker.Clear(atlasWidth, atlasHeight);
            glyphCache.Clear();

            // Clear the Layout Cache
            layoutCache.Clear();

            // Bump version so any externally-held TextLayout knows it's stale.
            AtlasVersion++;

            // Re-add white rect
            if (useWhiteRect)
                AddWhiteRect();

            return true;
        }

        #region Metrics and Getters

        private object CreateEmptyAtlas(int width, int height)
        {
            object texture = renderer.CreateTexture(width, height);
            // Glyph uploads leave packing gaps and padding untouched. Linear filtering can
            // sample those texels at glyph edges; freshly allocated GPU memory is undefined.
            // Initialize all RGBA texels, including packing gaps, before publishing the atlas.
            // Keep each upload small: TransientStore adds headroom to a 64 MiB payload,
            // which can rent a 128 MiB page retained by ArrayPool after atlas disposal.
            // The zero array is immutable, so renderers may consume queued stripes later.
            int rowBytes = checked(width * 4);
            const int MaximumClearUploadBytes = 4 * 1024 * 1024;
            int stripeRows = Math.Min(height, Math.Max(1, MaximumClearUploadBytes / rowBytes));
            byte[] zeros = new byte[checked(rowBytes * stripeRows)];
            for (int y = 0; y < height; y += stripeRows)
            {
                int rows = Math.Min(stripeRows, height - y);
                // Non-power-of-two atlas heights can end with a smaller stripe.
                byte[] payload = rows == stripeRows ? zeros : new byte[checked(rowBytes * rows)];
                renderer.UpdateTextureRegion(texture, new AtlasRect(0, y, width, rows), payload);
            }
            return texture;
        }

        public GlyphMetrics? GetGlyphMetrics(FontFile fontInfo, int codepoint, float pixelSize)
        {
            int glyphIndex = fontInfo.FindGlyphIndex(codepoint);
            if (glyphIndex == 0) return null;
            return GetGlyphMetricsByIndex(fontInfo, glyphIndex, pixelSize);
        }

        /// <summary>Per-glyph horizontal metrics by glyph index (used by the shaper / substituted
        /// glyphs). Index 0 is the font's .notdef and returns its real metrics.</summary>
        public GlyphMetrics? GetGlyphMetricsByIndex(FontFile fontInfo, int glyphIndex, float pixelSize)
        {
            if (glyphIndex < 0) return null;

            float scale = fontInfo.ScaleForPixelHeight(pixelSize);

            // Get advance and bearing
            int advance = 0, leftSideBearing = 0;
            fontInfo.GetGlyphHorizontalMetrics(glyphIndex, ref advance, ref leftSideBearing);

            // Get bounding box
            int x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            fontInfo.GetGlyphBitmapBoundingBox(glyphIndex, scale, scale, ref x0, ref y0, ref x1, ref y1);

            return new GlyphMetrics {
                AdvanceWidth = advance * scale,
                LeftSideBearing = leftSideBearing * scale,
                Width = x1 - x0,
                Height = y1 - y0,
                OffsetX = x0,
                OffsetY = y0
            };
        }

        public void GetScaledVMetrics(FontFile font, float pixelSize, out float ascent, out float descent, out float lineGap)
        {
            float s = font.ScaleForPixelHeight(pixelSize);
            ascent = font.Ascent * s;
            descent = font.Descent * s; // stb returns negative descent; caller may convert to positive if desired
            lineGap = font.Linegap * s;
        }

        public float GetKerning(FontFile fontInfo, int leftCodepoint, int rightCodepoint, float pixelSize)
        {
            int leftGlyph = fontInfo.FindGlyphIndex(leftCodepoint);
            int rightGlyph = fontInfo.FindGlyphIndex(rightCodepoint);

            return GetKerningByGlyph(fontInfo, leftGlyph, rightGlyph, pixelSize);
        }

        public float GetKerningByGlyph(FontFile fontInfo, int leftGlyph, int rightGlyph, float pixelSize)
        {
            float scale = fontInfo.ScaleForPixelHeight(pixelSize);
            int kernAdvance = fontInfo.GetGlyphKerningAdvance(leftGlyph, rightGlyph);

            return kernAdvance * scale;
        }

        // Reusable GSUB shaping buffer (single-threaded, like the other caches).
        private List<GsubGlyph> _shapeBuf;

        /// <summary>
        /// Shapes a character range [<paramref name="start"/>, <paramref name="end"/>) into a
        /// positioned glyph run: codepoint mapping (surrogate-aware), GSUB substitution (ccmp/liga/
        /// rlig) and GPOS pair kerning folded into each glyph's advance. The run is split into
        /// maximal same-font segments (per fallback/selector resolution); shaping and kerning apply
        /// within a segment. Results are appended to <paramref name="output"/>.
        /// </summary>
        internal void ShapeRun(string text, int start, int end, FontFile requestedFont,
            Func<int, FontFile> selector, float pixelSize, FontQuality quality, List<ShapedGlyph> output)
        {
            output.Clear();
            var buf = _shapeBuf ??= new List<GsubGlyph>();
            buf.Clear();
            FontFile runFont = null;

            int i = start;
            while (i < end)
            {
                char c = text[i];
                int codepoint, charCount;
                if (char.IsHighSurrogate(c) && i + 1 < end && char.IsLowSurrogate(text[i + 1]))
                {
                    codepoint = char.ConvertToUtf32(c, text[i + 1]);
                    charCount = 2;
                }
                else
                {
                    codepoint = c;
                    charCount = 1;
                }

                FontFile reqFont = selector != null ? (selector(i) ?? requestedFont) : requestedFont;
                // Missing in every font renders the requested font's .notdef (tofu) instead of
                // silently vanishing; only default-ignorable codepoints resolve to null here.
                var ag = reqFont != null ? GetOrCreateGlyphOrNotdef(codepoint, reqFont, quality) : null;

                if (ag == null)
                {
                    // Invisible-by-design codepoint: flush so shaping/kerning doesn't cross the gap, then skip.
                    FlushSubRun(runFont, buf, pixelSize, quality, output);
                    buf.Clear();
                    runFont = null;
                    i += charCount;
                    continue;
                }

                if (runFont != null && !ReferenceEquals(ag.Font, runFont))
                {
                    FlushSubRun(runFont, buf, pixelSize, quality, output);
                    buf.Clear();
                }

                runFont = ag.Font;
                buf.Add(new GsubGlyph(ag.GlyphIndex, i, charCount));
                i += charCount;
            }

            FlushSubRun(runFont, buf, pixelSize, quality, output);
            buf.Clear();
        }

        private void FlushSubRun(FontFile font, List<GsubGlyph> buf, float pixelSize, FontQuality quality, List<ShapedGlyph> output)
        {
            if (font == null || buf.Count == 0)
                return;

            font.ApplyGsub(buf);
            float scale = font.ScaleForPixelHeight(pixelSize);

            for (int k = 0; k < buf.Count; k++)
            {
                var gg = buf[k];
                var atlas = GetOrCreateGlyphByIndex(gg.Glyph, font, quality);

                int adv = 0, lsb = 0;
                font.GetGlyphHorizontalMetrics(gg.Glyph, ref adv, ref lsb);
                float advance = adv * scale;
                if (k + 1 < buf.Count)
                    advance += font.GetGlyphKerningAdvance(gg.Glyph, buf[k + 1].Glyph) * scale;

                output.Add(new ShapedGlyph {
                    Glyph = atlas,
                    Advance = advance,
                    Cluster = gg.Cluster,
                    CharCount = gg.CharCount
                });
            }
        }

        #endregion

        #region Layout Methods

        public TextLayout CreateLayout(string text, TextLayoutSettings settings)
        {
            text ??= string.Empty;

            // Selectors can capture mutable state; delegate identity is not a content version.
            // Without an explicit selector revision, reusing that layout can choose stale fonts.
            if (!CacheLayouts || (settings.FontSelector != null && !settings.FontSelectorRevision.HasValue))
            {
                var direct = new TextLayout();
                direct.UpdateLayout(text, settings, this);
                LayoutCreationCount++;
                return direct;
            }

            var key = GenerateLayoutCacheKey(text, settings);

            if (layoutCache.TryGetValue(key, out var cached))
            {
                LayoutCacheHitCount++;
                return cached;
            }

            var layout = new TextLayout();
            layout.UpdateLayout(text, settings, this);
            LayoutCreationCount++;

            layoutCache.Add(key, layout, layout.EstimateCacheBytes());
            return layout;
        }

        internal void RefreshLayoutCacheCharge(TextLayout layout)
            => layoutCache.UpdateWeight(GenerateLayoutCacheKey(layout.Text, layout.Settings), layout, layout.EstimateCacheBytes());

        LayoutCacheKey GenerateLayoutCacheKey(string text, TextLayoutSettings s)
            => new LayoutCacheKey(text, s.PixelSize, s.LetterSpacing, s.WordSpacing, s.LineHeight,
                   s.TabSize, s.WrapMode, s.Alignment, s.MaxWidth, s.Font, s.Quality,
                   s.FontSelector, s.FontSelectorRevision);

        #endregion

        #region Updated API Methods

        public Float2 MeasureText(string text, float pixelSize, FontFile font, float letterSpacing = 0)
        {
            var settings = TextLayoutSettings.Default;
            settings.PixelSize = pixelSize;
            settings.Font = font;
            settings.LetterSpacing = letterSpacing;

            var layout = CreateLayout(text, settings);
            return layout.Size;
        }

        public Float2 MeasureText(string text, TextLayoutSettings settings)
        {
            var layout = CreateLayout(text, settings);
            return layout.Size;
        }

        public void DrawText(string text, Float2 position, FontColor color, float pixelSize, FontFile font, float letterSpacing = 0)
        {
            var settings = TextLayoutSettings.Default;
            settings.PixelSize = pixelSize;
            settings.Font = font;
            settings.LetterSpacing = letterSpacing;

            DrawText(text, position, color, settings);
        }


        public void DrawText(string text, Float2 position, FontColor color, TextLayoutSettings settings)
        {
            if (string.IsNullOrEmpty(text)) return;

            var layout = CreateLayout(text, settings);
            DrawLayout(layout, position, color);
        }

        public void DrawLayout(TextLayout layout, Float2 position, FontColor color)
            => DrawLayout(layout, position, color, renderer);

        /// <summary>
        /// Emits geometry to a borrowed destination while this system retains atlas creation,
        /// uploads and layout ownership. The destination must consume the spans synchronously;
        /// drawing is not thread-safe or reentrant. This does not pin an atlas across growth.
        /// </summary>
        public void DrawLayout(TextLayout layout, Float2 position, FontColor color, IFontRenderer destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (layout.Lines.Count == 0) return;

            // Atlas may have grown/rebuilt since the layout was created - UVs and glyph refs
            // would be stale. Re-layout in place so glyphs repopulate against the current atlas.
            // (This clears the quad cache below whenever it re-lays-out.)
            layout.EnsureUpToDate(this);

            // The per-glyph quad geometry (relative to the draw origin) and UVs depend only on the
            // layout + atlas, not on this call's position or colour. Build it once and reuse it every
            // frame; only the origin offset and colour are applied per draw.
            if (!layout._drawQuadsBuilt)
                BuildDrawQuads(layout);

            var quads = layout._drawQuads;
            if (quads.Count == 0) return;

            var vertices = drawVertices;
            var indices = drawIndices;
            vertices.Clear();
            indices.Clear();
            int vertexCount = 0;

            for (int i = 0; i < quads.Count; i++)
            {
                var q = quads[i];
                float x0 = position.X + q.X0, y0 = position.Y + q.Y0;
                float x1 = position.X + q.X1, y1 = position.Y + q.Y1;

                vertices.Add(new IFontRenderer.Vertex(new Float3(x0, y0, 0), color, new Float2(q.U0, q.V0)));
                vertices.Add(new IFontRenderer.Vertex(new Float3(x1, y0, 0), color, new Float2(q.U1, q.V0)));
                vertices.Add(new IFontRenderer.Vertex(new Float3(x0, y1, 0), color, new Float2(q.U0, q.V1)));
                vertices.Add(new IFontRenderer.Vertex(new Float3(x1, y1, 0), color, new Float2(q.U1, q.V1)));

                // Six Add calls - no per-quad array allocation.
                indices.Add(vertexCount);
                indices.Add(vertexCount + 1);
                indices.Add(vertexCount + 2);
                indices.Add(vertexCount + 1);
                indices.Add(vertexCount + 3);
                indices.Add(vertexCount + 2);
                vertexCount += 4;
            }

            if (vertices.Count > 0)
            {
#if NET5_0_OR_GREATER
                destination.DrawQuads(atlasTexture,
                    CollectionsMarshal.AsSpan(vertices),
                    CollectionsMarshal.AsSpan(indices));
#else
                destination.DrawQuads(atlasTexture, vertices.ToArray(), indices.ToArray());
#endif
            }
        }

        // Builds the position-independent glyph quads for a layout (see TextLayout._drawQuads). The
        // corner offsets are relative to the draw origin so the same list serves any draw position.
        private void BuildDrawQuads(TextLayout layout)
        {
            var quads = layout._drawQuads;
            quads.Clear();

            foreach (var line in layout.Lines)
            {
                foreach (var glyphInstance in line.Glyphs)
                {
                    var glyph = glyphInstance.Glyph;

                    // Only render if glyph is in atlas
                    if (!glyph.IsInAtlas || glyph.AtlasWidth <= 0 || glyph.AtlasHeight <= 0)
                        continue;

                    // Recover the pen origin (x) and baseline (y) from the glyph instance, then place
                    // the padded distance-field quad relative to them. The quad includes the
                    // distance-field margin, so it is larger than the glyph's ink bounds. The region
                    // is in font units; scale it to this instance's pixel size.
                    float ps = glyphInstance.PixelSize;
                    var gm = GetGlyphMetricsByIndex(glyph.Font, glyph.GlyphIndex, ps) ?? default;
                    float sc = glyph.Font.ScaleForPixelHeight(ps);

                    float penX = line.Position.X + glyphInstance.Position.X - gm.OffsetX;
                    float baselineY = line.Position.Y + glyphInstance.Position.Y - gm.OffsetY;

                    quads.Add(new TextLayout.DrawQuad
                    {
                        X0 = penX + (float)(glyph.RegionX0 * sc),
                        Y0 = baselineY + (float)(-glyph.RegionY1 * sc),
                        X1 = penX + (float)(glyph.RegionX1 * sc),
                        Y1 = baselineY + (float)(-glyph.RegionY0 * sc),
                        U0 = glyph.U0, V0 = glyph.V0, U1 = glyph.U1, V1 = glyph.V1,
                    });
                }
            }

            layout._drawQuadsBuilt = true;
            RefreshLayoutCacheCharge(layout);
        }

        #endregion
    }
}
