// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Scribe;
using XEngine.Vector;
using XEngine.Vector.Geometry;

using ScribeAlign = XEngine.Scribe.TextAlignment;
using TAlignment = XEngine.Runtime.UI.TextAlignment;

namespace XEngine.Runtime;

/// <summary>
/// TMP-style link span over rich text. Character indices refer to Scribe's visible text
/// (tags stripped), matching <see cref="RichStyleSpan"/> ranges.
/// </summary>
public readonly struct TextLinkInfo
{
    public readonly string LinkId;
    public readonly int LinkTextFirstCharacterIndex;
    public readonly int LinkTextLength;

    public TextLinkInfo(string linkId, int firstCharacterIndex, int length)
    {
        LinkId = linkId ?? string.Empty;
        LinkTextFirstCharacterIndex = firstCharacterIndex;
        LinkTextLength = length;
    }

    public string GetLinkID() => LinkId;
}

/// <summary>Minimal TMP-like character info for rich/plain laid-out glyphs.</summary>
public readonly struct TextCharacterInfo
{
    public readonly char Character;
    public readonly int Index;
    public readonly int LineIndex;
    public readonly Float2 BottomLeft;
    public readonly Float2 TopRight;
    public readonly string? LinkId;

    public TextCharacterInfo(char character, int index, int lineIndex, Float2 bottomLeft, Float2 topRight, string? linkId)
    {
        Character = character;
        Index = index;
        LineIndex = lineIndex;
        BottomLeft = bottomLeft;
        TopRight = topRight;
        LinkId = linkId;
    }
}

/// <summary>Subset of TMP textInfo: character + link tables for the current rich layout.</summary>
public sealed class TextInfo
{
    public TextCharacterInfo[] characterInfo = Array.Empty<TextCharacterInfo>();
    public TextLinkInfo[] linkInfo = Array.Empty<TextLinkInfo>();
    public int characterCount;
    public int linkCount;
}

/// <summary>
/// Retained-mode text element. Produces one textured quad per glyph using Scribe's
/// <see cref="FontSystem"/> for shaping/atlas allocation; the resulting mesh is drawn
/// through the standard UI pipeline (the <c>Default/DefaultText</c> shader sampling the
/// shared glyph atlas owned by <see cref="UIFontSystem"/>).
/// </summary>
[AddComponentMenu("UI/Text")]
[ComponentIcon("T")] // Text
public class TextComponent : UIBehaviour, IUILayoutElement, IPointerClickHandler
{
    public override bool IsGraphic => true;

    [SerializeField] private AssetRef<FontAsset> _font;
    public AssetRef<FontAsset> Font
    {
        get => _font;
        set => SetField(ref _font, value, UIDirtyFlags.Vertices | UIDirtyFlags.Material);
    }

    [SerializeField] private AssetRef<FontAsset> _boldFont;
    public AssetRef<FontAsset> BoldFont
    {
        get => _boldFont;
        set => SetField(ref _boldFont, value, UIDirtyFlags.Vertices | UIDirtyFlags.Material);
    }

    [SerializeField] private AssetRef<FontAsset> _italicFont;
    public AssetRef<FontAsset> ItalicFont
    {
        get => _italicFont;
        set => SetField(ref _italicFont, value, UIDirtyFlags.Vertices | UIDirtyFlags.Material);
    }

    [SerializeField] private AssetRef<FontAsset> _boldItalicFont;
    public AssetRef<FontAsset> BoldItalicFont
    {
        get => _boldItalicFont;
        set => SetField(ref _boldItalicFont, value, UIDirtyFlags.Vertices | UIDirtyFlags.Material);
    }

    [SerializeField] private AssetRef<FontAsset> _monoFont;
    public AssetRef<FontAsset> MonoFont
    {
        get => _monoFont;
        set => SetField(ref _monoFont, value, UIDirtyFlags.Vertices | UIDirtyFlags.Material);
    }

    /// <summary>The assigned font, or the engine's embedded default font when none is set.</summary>
    public FontAsset? ResolvedFont => _font.Res ?? FontAsset.LoadDefault();

    [SerializeField] private Color _textColor = Color.White;
    public Color TextColor
    {
        get => _textColor;
        set => SetField(ref _textColor, value, UIDirtyFlags.Vertices);
    }

    [SerializeField] private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set => SetField(ref _text, value ?? string.Empty, UIDirtyFlags.Vertices | UIDirtyFlags.Layout);
    }

    [SerializeField] private TAlignment _alignment = TAlignment.CenterMiddle;
    public TAlignment Alignment
    {
        get => _alignment;
        set => SetField(ref _alignment, value, UIDirtyFlags.Vertices);
    }

    [SerializeField] private int _size = 20;
    public int Size
    {
        get => _size;
        set => SetField(ref _size, Maths.Max(1, value), UIDirtyFlags.Vertices | UIDirtyFlags.Layout);
    }

    [SerializeField] private HorizontalWrapMode _horizontalOverflow = HorizontalWrapMode.Wrap;
    public HorizontalWrapMode HorizontalOverflow
    {
        get => _horizontalOverflow;
        set => SetField(ref _horizontalOverflow, value, UIDirtyFlags.Vertices | UIDirtyFlags.Layout);
    }

    [SerializeField] private VerticalWrapMode _verticalOverflow = VerticalWrapMode.Truncate;
    public VerticalWrapMode VerticalOverflow
    {
        get => _verticalOverflow;
        set => SetField(ref _verticalOverflow, value, UIDirtyFlags.Vertices | UIDirtyFlags.Layout);
    }

    [SerializeField] private bool _bestFit;
    public bool BestFit
    {
        get => _bestFit;
        set => SetField(ref _bestFit, value, UIDirtyFlags.Vertices | UIDirtyFlags.Layout);
    }

    [SerializeField] private int _bestFitMinSize = 10;
    public int BestFitMinSize
    {
        get => _bestFitMinSize;
        set => SetField(ref _bestFitMinSize, Maths.Max(1, value), UIDirtyFlags.Vertices | UIDirtyFlags.Layout);
    }

    [SerializeField] private int _bestFitMaxSize = 40;
    public int BestFitMaxSize
    {
        get => _bestFitMaxSize;
        set => SetField(ref _bestFitMaxSize, Maths.Max(1, value), UIDirtyFlags.Vertices | UIDirtyFlags.Layout);
    }

    /// <summary>Resolution the glyph SDF is rasterized at (see <see cref="FontQuality"/>). Higher
    /// quality keeps large text crisp at the cost of atlas space; the default is fine for body text.</summary>
    [SerializeField] private FontQuality _quality = FontQuality.Normal;
    public FontQuality Quality
    {
        get => _quality;
        set => SetField(ref _quality, value, UIDirtyFlags.Vertices | UIDirtyFlags.Material);
    }

    /// <summary>When true, <see cref="Text"/> is parsed for rich-text tags (color, size, bold/italic,
    /// etc.). The assigned <see cref="Font"/> is used for every style variant.</summary>
    [SerializeField] private bool _richText;
    public bool RichTextEnabled
    {
        get => _richText;
        set => SetField(ref _richText, value, UIDirtyFlags.Vertices);
    }

    /// <summary>Sprite table for rich-text <c>&lt;sprite name=...&gt;</c> / <c>&lt;sprite index=...&gt;</c>.</summary>
    [SerializeField] private AssetRef<TextSpriteAsset> _spriteAsset;
    public AssetRef<TextSpriteAsset> SpriteAsset
    {
        get => _spriteAsset;
        set => SetField(ref _spriteAsset, value, UIDirtyFlags.Vertices | UIDirtyFlags.Material);
    }

    // ---- Material override ----
    [SerializeField] private AssetRef<Material> _material;
    public AssetRef<Material> Material
    {
        get => _material;
        set => SetField(ref _material, value, UIDirtyFlags.Material);
    }

    public override Material GetMaterial() => _material.Res ?? GameCanvas.SharedTextMaterial;

    public override Material? GetSecondaryMaterial()
        => _spriteAtlasTexture != null ? GameCanvas.SharedUIMaterial : null;

    public override void PopulateSecondaryProperties(PropertyState p, in UIContext context)
    {
        Texture2D atlas = _spriteAtlasTexture ?? Texture2D.LoadDefault(DefaultTexture.White);
        p.SetTexture("_MainTex", atlas);
        p.SetColor("_MainColor", new Color(1f, 1f, 1f, context.Alpha));
        p.SetVector("_Tiling", new Float2(1, 1));
        p.SetVector("_Offset", Float2.Zero);
    }

    public override void GenerateSecondaryMesh(UIMeshBuilder builder, in UIContext context)
    {
        _spriteAtlasTexture = null;
        if (!_richText || _richLayout == null) return;

        Texture2D? atlas = null;
        Float2 pivot = GameObject.RectTransform?.Pivot ?? new Float2(0.5f, 0.5f);
        float w = GameObject.RectTransform?.ComputedRect.Size.X ?? 0f;
        float h = GameObject.RectTransform?.ComputedRect.Size.Y ?? 0f;
        float minX = -pivot.X * w;
        float maxX = (1f - pivot.X) * w;
        float minY = -pivot.Y * h;
        float maxY = (1f - pivot.Y) * h;
        bool clipX = _horizontalOverflow == HorizontalWrapMode.Wrap;
        bool clipY = _verticalOverflow == VerticalWrapMode.Truncate
            || _verticalOverflow == VerticalWrapMode.Ellipsis;

        var glyphs = _richLayout.Glyphs;
        for (int i = 0; i < glyphs.Count; i++)
        {
            RichGlyph g = glyphs[i];
            if (!g.IsSprite || g.SpriteTextureKey is not Texture2D tex) continue;
            if (atlas == null) atlas = tex;
            else if (!ReferenceEquals(atlas, tex)) continue; // v1: single atlas page

            float x0 = _captureOriginX + g.Position.X;
            float yTopLayout = g.Position.Y;
            float yBotLayout = g.Position.Y + g.SpriteSize.Y;
            float y0 = _captureBaseY - yBotLayout;
            float y1 = _captureBaseY - yTopLayout;
            if (clipX && (x0 > maxX || x0 + g.SpriteSize.X < minX)) continue;
            if (clipY && (y1 < minY || y0 > maxY)) continue;

            Color tint = new Color(
                g.Color.R / 255f, g.Color.G / 255f, g.Color.B / 255f, g.Color.A / 255f * context.Alpha);
            builder.AddQuad(
                new Rect(x0, y0, x0 + g.SpriteSize.X, y1),
                tint,
                g.SpriteUvMin,
                g.SpriteUvMax);
        }

        _spriteAtlasTexture = atlas;
    }

    /// <summary>
    /// Atlas version recorded at the last successful bake. When Scribe grows the atlas
    /// (e.g. a new glyph or pixel-size variant is introduced) all existing AtlasGlyph
    /// UVs are recomputed against the new texture dimensions, so any cached glyph mesh
    /// is now pointing at the wrong region. We catch that in <see cref="Update"/> and
    /// force a rebake.
    /// </summary>
    [SerializeIgnore] private int _lastAtlasVersion = -1;

    // Resolved font instances observed by the last Update() poll. An AssetRef resolves to null
    // while its asset streams in (async loading), so the bake that runs meanwhile falls back to
    // the embedded default font and clears its dirty flag - with a CJK string that shows as
    // blank text, because the fallback has no CJK glyphs. The ref's value (the GUID) never
    // changes again, so no property setter fires when the real font arrives; these fields are
    // how Update() notices the arrival (or a reimport swap) and rebakes.
    [SerializeIgnore] private FontAsset? _seenFont;
    [SerializeIgnore] private FontAsset? _seenBoldFont;
    [SerializeIgnore] private FontAsset? _seenItalicFont;
    [SerializeIgnore] private FontAsset? _seenBoldItalicFont;
    [SerializeIgnore] private FontAsset? _seenMonoFont;

    private bool RefreshResolvedFonts()
    {
        FontAsset? f = _font.Res, b = _boldFont.Res, i = _italicFont.Res,
            bi = _boldItalicFont.Res, m = _monoFont.Res;
        if (ReferenceEquals(f, _seenFont) && ReferenceEquals(b, _seenBoldFont)
            && ReferenceEquals(i, _seenItalicFont) && ReferenceEquals(bi, _seenBoldItalicFont)
            && ReferenceEquals(m, _seenMonoFont))
            return false;
        _seenFont = f;
        _seenBoldFont = b;
        _seenItalicFont = i;
        _seenBoldItalicFont = bi;
        _seenMonoFont = m;
        return true;
    }

    // Cached rich-text layout. Reused across frames so its animation start-time survives (a fresh
    // layout each rebuild would re-anchor to "now" and freeze animated effects). Rebuilt only when the
    // text or layout-affecting settings change.
    [SerializeIgnore] private RichTextLayout? _richLayout;
    [SerializeIgnore] private int _richSig;
    [SerializeIgnore] private bool _richAnimated;
    [SerializeIgnore] private float _richVerticalOffset;
    [SerializeIgnore] private TextLinkInfo[] _linkInfos = Array.Empty<TextLinkInfo>();
    [SerializeIgnore] private float _captureOriginX;
    [SerializeIgnore] private float _captureBaseY;
    [SerializeIgnore] private Texture2D? _spriteAtlasTexture;
    [SerializeIgnore] private readonly SpriteProviderAdapter _spriteProvider = new();

    /// <summary>Fires when a rich-text link is clicked. Argument is the link id / href.</summary>
    public event Action<string>? OnLinkClicked;

    /// <summary>Rich-text link spans for the current text (empty when rich text is off or there are none).</summary>
    public TextLinkInfo[] linkInfo
    {
        get
        {
            TryEnsureRichLayout();
            return _linkInfos;
        }
    }

    /// <summary>TMP-style textInfo subset (characterInfo + linkInfo) for the current rich layout.</summary>
    public TextInfo textInfo
    {
        get
        {
            TryEnsureRichLayout();
            return BuildTextInfo();
        }
    }

    public override void Update()
    {
        int v = UIFontSystem.Default.System.AtlasVersion;
        if (v != _lastAtlasVersion)
        {
            _lastAtlasVersion = v;
            MarkDirty(UIDirtyFlags.Vertices);
        }

        // Rebake (and relayout: preferred size depends on font metrics) when any font slot's
        // resolved instance changed - typically an async-loaded font streaming in after the
        // first bake used the embedded fallback. Polling .Res also touches the assets, keeping
        // in-use fonts out of the idle eviction sweep.
        if (RefreshResolvedFonts())
            MarkDirty(UIDirtyFlags.Vertices | UIDirtyFlags.Layout | UIDirtyFlags.Material);

        // Animated rich-text effects (wave, shake, rainbow, typewriter, ...) are time-driven, so the
        // mesh has to be rebuilt every frame to advance them.
        if (_richText && _richAnimated)
            MarkDirty(UIDirtyFlags.Vertices);
    }

    // ============================================================
    // Mesh generation
    // ============================================================

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context)
    {
        FontAsset? font = ResolvedFont;
        if (font is null || font.FontFile is null || string.IsNullOrEmpty(Text)) return;

        var rt = GameObject.RectTransform;
        if (rt is null) return;
        Rect r = rt.ComputedRect;
        if (r.Size.X <= 0 || r.Size.Y <= 0) return;

        UIFontSystem fs = UIFontSystem.Default;
        FontFile fontFile = font.FontFile;
        float w = r.Size.X;
        float h = r.Size.Y;
        float pixelSize = ResolvePixelSize(Text, w, h, fontFile);

        // Element-local pivot-centered space (matches GameCanvas.BuildItemModel), +Y up. The rect's
        // TOP-LEFT is at (-pivot.X * w, (1 - pivot.Y) * h); Scribe's layout Y grows downward, so the
        // capture bridge maps it as y -> baseY - y.
        Float2 pivot = rt.Pivot;
        float originX = -pivot.X * w;
        float originY = (1f - pivot.Y) * h;

        // CanvasGroup alpha is a material property; keep shaped glyph geometry and authored colors.
        Color tinted = TextColor;
        FontColor color = new FontColor(tinted.R, tinted.G, tinted.B, tinted.A);
        FontQuality rasterQuality = _quality; // raster-bucket adaptation parked; see Canvas.ScaleSettings note

        // Scribe generates the geometry (plain or rich-tag parsed) and drives DrawQuads; the capture
        // maps it into element-local space and appends it to the mesh. Vertical alignment isn't done
        // by Scribe (its alignment is horizontal-only), so we offset the whole box by the layout height.
        if (_richText)
        {
            RichTextLayoutSettings rich = new RichTextLayoutSettings
            {
                RegularFont = fontFile,
                BoldFont = ResolveStyleFont(_boldFont, fontFile),
                ItalicFont = ResolveStyleFont(_italicFont, fontFile),
                BoldItalicFont = ResolveStyleFont(_boldItalicFont, fontFile),
                MonoFont = ResolveStyleFont(_monoFont, fontFile),
                PixelSize = pixelSize,
                Quality = rasterQuality,
                MaxWidth = ResolveMaxWidth(w),
                WrapMode = CurrentWrapMode(),
                Alignment = ToScribeAlignment(_alignment),
                DefaultColor = color,
                SpriteProvider = BindSpriteProvider(),
            };
            int sig = ComputeRichSignature(fontFile, pixelSize, w, tinted, rasterQuality);
            if (_richLayout == null || sig != _richSig)
            {
                if (_richLayout == null) _richLayout = new RichTextLayout(_text, rich);
                else { _richLayout.SetSource(_text); _richLayout.SetSettings(rich); }
                _richLayout.Update(fs.System);
                _richAnimated = _richLayout.Effects.Count > 0;
                _richSig = sig;
                RebuildLinkInfos(_richLayout);
            }

            if (_verticalOverflow == VerticalWrapMode.Ellipsis)
                _richLayout.ApplyEllipsis(h, fs.System);

            float verticalOffset = ComputeVerticalOffset(_alignment, h, _richLayout.Size.Y);
            _richVerticalOffset = verticalOffset;
            _captureOriginX = originX;
            _captureBaseY = originY - verticalOffset;
            BeginClippedCapture(fs, builder, originX, originY - verticalOffset, w, h, rt.Pivot, pixelSize, rasterQuality);
            try { _richLayout.Draw(fs.System, fs, Float2.Zero, (double)Time.TimeSinceStartup); }
            finally { fs.EndCapture(); }
        }
        else
        {
            TextLayoutSettings settings = new TextLayoutSettings
            {
                Font          = fontFile,
                PixelSize     = pixelSize,
                Quality       = rasterQuality,
                Alignment     = ToScribeAlignment(_alignment),
                MaxWidth      = ResolveMaxWidth(w),
                WrapMode      = CurrentWrapMode(),
                LineHeight    = 1.0f,
                TabSize       = 4,
                LetterSpacing = 0f,
                WordSpacing   = 0f,
            };
            TextLayout layout = fs.System.CreateLayout(_text, settings);

            float verticalOffset = ComputeVerticalOffset(_alignment, h, layout.Size.Y);
            BeginClippedCapture(fs, builder, originX, originY - verticalOffset, w, h, rt.Pivot, pixelSize, rasterQuality);
            try { fs.System.DrawLayout(layout, Float2.Zero, color); }
            finally { fs.EndCapture(); }
        }
    }


    public bool IgnoreLayout => false;
    public float MinWidth => 0f;
    public float MinHeight => 0f;
    public float FlexibleWidth => 0f;
    public float FlexibleHeight => 0f;
    public float PreferredWidth => GetPreferredValues().X;
    public float PreferredHeight
    {
        get
        {
            float width = CurrentLayoutWidth();
            return width > 0f ? GetPreferredValues(width, float.PositiveInfinity).Y : GetPreferredValues().Y;
        }
    }

    private float CurrentLayoutWidth()
    {
        RectTransform? rt = GameObject.RectTransform;
        if (rt is null) return 0f;
        float computed = rt.ComputedRect.Size.X;
        return computed > 0f ? computed : rt.SizeDelta.X;
    }

    public Float2 GetPreferredValues() => GetPreferredValues(Text, float.PositiveInfinity, float.PositiveInfinity);

    public Float2 GetPreferredValues(string? text) => GetPreferredValues(text, float.PositiveInfinity, float.PositiveInfinity);

    public Float2 GetPreferredValues(float maxWidth, float maxHeight)
        => GetPreferredValues(Text, maxWidth, maxHeight);

    public Float2 GetPreferredValues(string? text, float maxWidth, float maxHeight)
    {
        FontAsset? resolved = ResolvedFont;
        if (resolved?.FontFile is null || string.IsNullOrEmpty(text)) return Float2.Zero;
        float width = float.IsFinite(maxWidth) ? Maths.Max(0f, maxWidth) : 0f;
        float pixelSize = _bestFit && float.IsFinite(maxWidth) && float.IsFinite(maxHeight)
            ? ResolvePixelSize(text, maxWidth, maxHeight, resolved.FontFile)
            : Maths.Max(1, _size);
        return MeasureLayout(text, pixelSize, width, resolved.FontFile);
    }

    private TextWrapMode CurrentWrapMode()
        => _horizontalOverflow == HorizontalWrapMode.Wrap ? TextWrapMode.Wrap : TextWrapMode.NoWrap;

    private float ResolveMaxWidth(float rectWidth)
        => _horizontalOverflow == HorizontalWrapMode.Wrap ? rectWidth : 0f;

    private float ResolvePixelSize(string text, float width, float height, FontFile fontFile)
    {
        int requested = Maths.Max(1, _size);
        if (!_bestFit || string.IsNullOrEmpty(text) || width <= 0f || height <= 0f)
            return requested;

        int min = Maths.Max(1, _bestFitMinSize);
        int max = Maths.Max(min, _bestFitMaxSize);
        int lo = min, hi = max, best = min;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            Float2 size = MeasureLayout(text, mid, ResolveMaxWidth(width), fontFile);
            if (size.X <= width + 0.01f && size.Y <= height + 0.01f)
            {
                best = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        return best;
    }

    private Float2 MeasureLayout(string text, float pixelSize, float maxWidth, FontFile fontFile)
    {
        if (_richText)
        {
            var layout = new RichTextLayout(text, CreateRichSettings(fontFile, pixelSize, maxWidth, Color.White, _quality));
            layout.Update(UIFontSystem.Default.System);
            return layout.Size;
        }

        TextLayout layoutPlain = UIFontSystem.Default.System.CreateLayout(text, CreatePlainSettings(fontFile, pixelSize, maxWidth));
        return new Float2((float)layoutPlain.Size.X, (float)layoutPlain.Size.Y);
    }

    private RichTextLayoutSettings CreateRichSettings(FontFile fontFile, float pixelSize, float maxWidth, Color tinted, FontQuality rasterQuality)
    {
        FontColor color = new FontColor(tinted.R, tinted.G, tinted.B, tinted.A);
        return new RichTextLayoutSettings
        {
            RegularFont = fontFile,
            BoldFont = ResolveStyleFont(_boldFont, fontFile),
            ItalicFont = ResolveStyleFont(_italicFont, fontFile),
            BoldItalicFont = ResolveStyleFont(_boldItalicFont, fontFile),
            MonoFont = ResolveStyleFont(_monoFont, fontFile),
            PixelSize = pixelSize,
            Quality = rasterQuality,
            MaxWidth = maxWidth,
            WrapMode = CurrentWrapMode(),
            Alignment = ToScribeAlignment(_alignment),
            DefaultColor = color,
            SpriteProvider = BindSpriteProvider(),
        };
    }

    private static FontFile ResolveStyleFont(AssetRef<FontAsset> slot, FontFile fallback)
        => slot.Res?.FontFile ?? fallback;

    // The cached rich layout must rebuild when the fonts it was shaped with change - including
    // the async case where a slot resolved to the fallback first and the real font streamed in
    // later - so the signature hashes the resolved FontFile identities, not the AssetRef values.
    private int ComputeRichSignature(FontFile fontFile, float pixelSize, float w, Color tinted, FontQuality rasterQuality)
        => HashCode.Combine(_text, pixelSize, (int)rasterQuality, w,
            HashCode.Combine((int)_alignment, (int)_horizontalOverflow, (int)_verticalOverflow, _bestFit, tinted),
            HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(fontFile),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ResolveStyleFont(_boldFont, fontFile)),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ResolveStyleFont(_italicFont, fontFile)),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ResolveStyleFont(_boldItalicFont, fontFile)),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ResolveStyleFont(_monoFont, fontFile))));

    private IRichSpriteProvider? BindSpriteProvider()
    {
        TextSpriteAsset? asset = _spriteAsset.Res;
        if (asset == null) return null;
        _spriteProvider.Asset = asset;
        return _spriteProvider;
    }

    private sealed class SpriteProviderAdapter : IRichSpriteProvider
    {
        public TextSpriteAsset? Asset;
        public bool TryGetMetrics(in RichInlineSprite sprite, float pixelSize, out RichSpriteMetrics metrics)
        {
            metrics = default;
            return Asset != null && Asset.TryGetMetrics(sprite, pixelSize, out metrics);
        }
    }

    private TextLayoutSettings CreatePlainSettings(FontFile fontFile, float pixelSize, float maxWidth)
        => new TextLayoutSettings
        {
            Font          = fontFile,
            PixelSize     = pixelSize,
            Quality       = _quality,
            Alignment     = ToScribeAlignment(_alignment),
            MaxWidth      = maxWidth,
            WrapMode      = CurrentWrapMode(),
            LineHeight    = 1.0f,
            TabSize       = 4,
            LetterSpacing = 0f,
            WordSpacing   = 0f,
        };

    private void BeginClippedCapture(UIFontSystem fs, UIMeshBuilder builder, float originX, float baseY, float width, float height, Float2 pivot, float pixelSize, FontQuality rasterQuality)
    {
        // Glyph quad extends past its ink by the distance-field margin (transparent pixels).
        // Expand the cull rect by that margin so an edge glyph is not dropped for its padding
        // alone - the capture culls whole triangles, so without this the first character of a
        // left-aligned line ("Account" -> "ccount") or a full-height CJK glyph disappears. Real
        // overflow (a wrapped-away column, a truncated line) sits at least a glyph beyond the
        // rect and is still culled. Rich text can scale glyphs above pixelSize via <size> tags;
        // the base-size margin is the accepted approximation there. The margin must follow the
        // raster bucket actually used (margin em-fraction = padPx / rasterEm), not the authored
        // quality, or glyphs baked at a coarser bucket get over-culled.
        float quality = (int)rasterQuality > 0 ? (int)rasterQuality : (int)FontQuality.Normal;
        float pad = (fs.System.DistanceRange * 0.5f + 1f) * pixelSize / quality;
        float minX = -pivot.X * width - pad;
        float maxX = (1f - pivot.X) * width + pad;
        float minY = -pivot.Y * height - pad;
        float maxY = (1f - pivot.Y) * height + pad;
        bool clipX = _horizontalOverflow == HorizontalWrapMode.Wrap;
        bool clipY = _verticalOverflow == VerticalWrapMode.Truncate
            || _verticalOverflow == VerticalWrapMode.Ellipsis;
        fs.BeginCapture(builder, originX, baseY, 1f, minX, maxX, minY, maxY, clipX, clipY);
    }

    /// <summary>
    /// Measures the width, in design pixels, of <paramref name="s"/> laid out on a single line with this
    /// component's current font, size and quality. Input fields use this to place the caret and selection
    /// box relative to the text. Returns 0 for empty text or when no font is available.
    /// </summary>
    public float MeasureWidth(string? s)
    {
        FontAsset? font = ResolvedFont;
        if (font is null || font.FontFile is null || string.IsNullOrEmpty(s)) return 0f;

        TextLayoutSettings settings = new TextLayoutSettings
        {
            Font          = font.FontFile,
            PixelSize     = Maths.Max(1, _size),
            Quality       = _quality,
            Alignment     = ScribeAlign.Left,
            MaxWidth      = float.MaxValue,
            WrapMode      = TextWrapMode.NoWrap,
            LineHeight    = 1.0f,
            TabSize       = 4,
            LetterSpacing = 0f,
            WordSpacing   = 0f,
        };
        TextLayout layout = UIFontSystem.Default.System.CreateLayout(s, settings);
        return (float)layout.Size.X;
    }

    public override void PopulateProperties(PropertyState p, in UIContext context)
    {
        // Always re-read the atlas through System.Texture - it can be replaced when the
        // atlas grows. Falls back to a 1x1 white texture if Scribe hasn't allocated yet.
        Texture2D atlas = UIFontSystem.Default.Atlas ?? Texture2D.LoadDefault(DefaultTexture.White);
        p.SetTexture("_MainTex", atlas);
        p.SetColor("_MainColor", new Color(1f, 1f, 1f, context.Alpha));
        p.SetVector("_Tiling", new Float2(1, 1));
        p.SetVector("_Offset", Float2.Zero);
    }

    // ============================================================
    // Rich-text link hit testing (TMP-style)
    // ============================================================

    /// <summary>Returns the link href under <paramref name="designPosition"/>, or null.</summary>
    public string? HitLink(Float2 designPosition)
    {
        if (!TryMapDesignToLayoutLocal(designPosition, out Float2 local) || _richLayout == null)
            return null;
        return _richLayout.HitLink(local);
    }

    /// <summary>
    /// Returns the index into <see cref="linkInfo"/> for the link under
    /// <paramref name="designPosition"/>, or -1 when none.
    /// </summary>
    public int FindIntersectingLink(Float2 designPosition)
    {
        if (!TryMapDesignToLayoutLocal(designPosition, out Float2 local) || _richLayout == null)
            return -1;

        int glyphIndex = _richLayout.HitGlyph(local);
        if (glyphIndex < 0) return -1;

        int charIndex = _richLayout.Glyphs[glyphIndex].CharIndex;
        TextLinkInfo[] links = _linkInfos;
        for (int i = 0; i < links.Length; i++)
        {
            int start = links[i].LinkTextFirstCharacterIndex;
            int end = start + links[i].LinkTextLength;
            if (charIndex >= start && charIndex < end)
                return i;
        }
        return -1;
    }

    public void OnPointerClick(PointerEventData e)
    {
        if (e.Button != MouseButton.Left || !_richText) return;

        int index = FindIntersectingLink(e.DesignPosition);
        if (index < 0) return;

        string id = _linkInfos[index].GetLinkID();
        try { OnLinkClicked?.Invoke(id); }
        catch (Exception ex)
        {
            Debug.LogError($"[TextComponent] OnLinkClicked handler on '{Name}' threw: {ex.Message}\n{ex.StackTrace}");
        }
        e.Use();
    }

    private bool TryEnsureRichLayout()
    {
        if (!_richText) { _linkInfos = Array.Empty<TextLinkInfo>(); return false; }

        FontAsset? font = ResolvedFont;
        if (font is null || font.FontFile is null || string.IsNullOrEmpty(_text))
        {
            _linkInfos = Array.Empty<TextLinkInfo>();
            return false;
        }

        RectTransform? rt = GameObject.RectTransform;
        if (rt is null) return false;

        Rect r = rt.ComputedRect;
        float w = Maths.Max(0f, r.Size.X);
        float h = Maths.Max(0f, r.Size.Y);
        UIFontSystem fs = UIFontSystem.Default;
        FontFile fontFile = font.FontFile;
        float pixelSize = w > 0f && h > 0f
            ? ResolvePixelSize(_text, w, h, fontFile)
            : Maths.Max(1, _size);
        Color tinted = TextColor;

        RichTextLayoutSettings rich = CreateRichSettings(fontFile, pixelSize, ResolveMaxWidth(w), tinted, _quality);
        int sig = ComputeRichSignature(fontFile, pixelSize, w, tinted, _quality);
        if (_richLayout == null || sig != _richSig)
        {
            if (_richLayout == null) _richLayout = new RichTextLayout(_text, rich);
            else { _richLayout.SetSource(_text); _richLayout.SetSettings(rich); }
            _richLayout.Update(fs.System);
            _richAnimated = _richLayout.Effects.Count > 0;
            _richSig = sig;
            RebuildLinkInfos(_richLayout);
        }

        _richVerticalOffset = ComputeVerticalOffset(_alignment, h, _richLayout.Size.Y);
        return true;
    }

    private bool TryMapDesignToLayoutLocal(Float2 designPosition, out Float2 layoutLocal)
    {
        layoutLocal = default;
        if (!TryEnsureRichLayout() || _richLayout == null) return false;

        RectTransform? rt = GameObject.RectTransform;
        if (rt is null) return false;

        Rect r = rt.ComputedRect;
        float h = r.Size.Y;
        Float2 fromBl = designPosition - r.Min;
        // Layout origin is the top-left of the text block (after vertical alignment offset).
        // X grows right from the left edge; Y grows down from the top of the text block.
        layoutLocal = new Float2(fromBl.X, (h - fromBl.Y) - _richVerticalOffset);
        return true;
    }

    private void RebuildLinkInfos(RichTextLayout layout)
    {
        var styles = layout.Styles;
        int count = 0;
        for (int i = 0; i < styles.Count; i++)
        {
            if ((styles[i].Flags & RichStyleFlags.Link) != 0 && !string.IsNullOrEmpty(styles[i].LinkHref))
                count++;
        }

        if (count == 0)
        {
            _linkInfos = Array.Empty<TextLinkInfo>();
            return;
        }

        var infos = new TextLinkInfo[count];
        int write = 0;
        for (int i = 0; i < styles.Count; i++)
        {
            RichStyleSpan s = styles[i];
            if ((s.Flags & RichStyleFlags.Link) == 0 || string.IsNullOrEmpty(s.LinkHref)) continue;
            infos[write++] = new TextLinkInfo(s.LinkHref, s.Start, Maths.Max(0, s.End - s.Start));
        }
        _linkInfos = infos;
    }

    private TextInfo BuildTextInfo()
    {
        var info = new TextInfo { linkInfo = _linkInfos, linkCount = _linkInfos.Length };
        if (_richLayout == null)
        {
            info.characterInfo = Array.Empty<TextCharacterInfo>();
            return info;
        }

        var glyphs = _richLayout.Glyphs;
        var chars = new TextCharacterInfo[glyphs.Count];
        for (int i = 0; i < glyphs.Count; i++)
        {
            RichGlyph g = glyphs[i];
            float x0 = g.Position.X;
            float y0 = g.Position.Y;
            float x1 = x0 + (g.IsSprite ? g.SpriteSize.X : g.Advance);
            float y1 = y0 + (g.IsSprite ? g.SpriteSize.Y : g.PixelSize);
            // Layout is +Y down; expose bottom-left / top-right in that space for TMP-like queries.
            chars[i] = new TextCharacterInfo(g.Character, g.CharIndex, g.LineIndex,
                new Float2(x0, y1), new Float2(x1, y0), g.LinkHref);
        }
        info.characterInfo = chars;
        info.characterCount = chars.Length;
        return info;
    }

    // ============================================================
    // Alignment helpers
    // ============================================================

    // TAlignment is a [Flags] enum with one bit per axis position, so both helpers test the
    // individual axis flags rather than switching on the nine named combinations - that way
    // every combination (including TopCenter / TopRight) resolves correctly.

    private static ScribeAlign ToScribeAlignment(TAlignment a)
    {
        if ((a & TAlignment.Right)  != 0) return ScribeAlign.Right;
        if ((a & TAlignment.Middle) != 0) return ScribeAlign.Center;
        return ScribeAlign.Left;
    }

    private static float ComputeVerticalOffset(TAlignment a, float boxHeight, float layoutHeight)
    {
        if ((a & TAlignment.Bottom) != 0) return Maths.Max(0f, boxHeight - layoutHeight);
        if ((a & TAlignment.Center) != 0) return Maths.Max(0f, (boxHeight - layoutHeight) * 0.5f);
        return 0f; // Top (default)
    }
}
