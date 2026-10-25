using System;
using System.Collections.Generic;
using System.Drawing;

using XEngine.OrigamiUI;
using XEngine.Scribe;

namespace XEngine.Editor.Theming;

/// <summary>
/// The editor's visual tokens. Locked to Unity 6 Professional Dark: Origami's
/// <see cref="OrigamiTheme.CreateDefaults"/> plus the editor's fonts and metrics.
/// </summary>
public static class EditorTheme
{
    /// <summary>Default transition (seconds) when applying the editor theme to Origami. 0 = snap.</summary>
    public const float OrigamiTransitionSeconds = 0.25f;

    // -- Editor's Origami theme state ----------------------------------
    //
    // The editor owns its own OrigamiTheme and pushes it onto Origami's stack each frame
    // (see PushOrigami). Origami.Root is left untouched. SyncOrigami starts a transition into a
    // freshly-built target theme; TickOrigami advances the lerp once per frame.

    private static OrigamiTheme? _origamiTheme;

    /// <summary>Live theme the editor pushes onto Origami's stack. Frame-fresh when transitioning.</summary>
    public static OrigamiTheme OrigamiTheme
    {
        get
        {
            if (_origamiTheme == null || _origamiTheme.Font == null)
                _origamiTheme = BuildOrigamiTheme();
            return _origamiTheme;
        }
        private set => _origamiTheme = value;
    }

    private static OrigamiTheme? _origamiStart;
    private static OrigamiTheme? _origamiTarget;
    private static float _origamiDuration;
    private static float _origamiElapsed;

    /// <summary>True while a <see cref="SyncOrigami"/> transition is in progress.</summary>
    public static bool IsOrigamiTransitioning => _origamiTarget != null;

    /// <summary>Rebuild the pushed Origami theme and transition toward it.</summary>
    public static void SyncOrigami(float transitionSeconds = OrigamiTransitionSeconds)
    {
        var target = BuildOrigamiTheme();
        if (transitionSeconds <= 0f)
        {
            OrigamiTheme = target;
            _origamiStart = null;
            _origamiTarget = null;
            _origamiDuration = 0f;
            _origamiElapsed = 0f;
            return;
        }

        _origamiStart = OrigamiTheme.Clone();
        _origamiTarget = target;
        _origamiDuration = transitionSeconds;
        _origamiElapsed = 0f;
    }

    /// <summary>Advance the editor's Origami theme transition. Called from the editor's frame loop.</summary>
    public static void TickOrigami(float deltaSeconds)
    {
        if (_origamiTarget == null || _origamiStart == null) return;

        _origamiElapsed += deltaSeconds;
        float t = _origamiDuration > 0f ? Math.Clamp(_origamiElapsed / _origamiDuration, 0f, 1f) : 1f;

        if (t >= 1f)
        {
            OrigamiTheme = _origamiTarget;
            _origamiStart = null;
            _origamiTarget = null;
            _origamiDuration = 0f;
            _origamiElapsed = 0f;
            return;
        }

        OrigamiTheme = XEngine.OrigamiUI.OrigamiTheme.Lerp(_origamiStart, _origamiTarget, t);
    }

    /// <summary>
    /// Push the editor's Origami theme onto Origami's stack. Wrap the editor's render in
    /// <c>using (EditorTheme.PushOrigami()) { ... }</c>.
    /// </summary>
    public static IDisposable PushOrigami() => Origami.PushTheme(OrigamiTheme);

    /// <summary>
    /// The editor theme = the locked Unity Dark palette + the editor's fonts and metrics.
    /// Icons are left at Origami's default vector set (no override).
    /// </summary>
    public static OrigamiTheme BuildOrigamiTheme()
    {
        var t = OrigamiTheme.CreateDefaults();

        // Unity 7 collection selection; Console deliberately retains its active fill on focus loss.
        t.SelectionStyle = new OrigamiSelectionStyle(Color.FromArgb(77, 77, 77),
            Color.White, Color.FromArgb(210, 210, 210));
        t.DragFeedback = true;

        // The Windows Unity 7 reference uses native light popup menus with 22px rows.
        t.Menus = new OrigamiMenuStyle(22f, 12f, 160f, 3f,
            Color.FromArgb(242, 242, 242), Color.FromArgb(190, 190, 190),
            Color.FromArgb(32, 32, 32), Color.FromArgb(145, 145, 145),
            Color.FromArgb(208, 226, 242), Color.FromArgb(16, 16, 16));

        t.Metrics = new OrigamiMetrics
        {
            // Unity 7 IMGUI property fields: 18px line, 2px gap, 12px text, 3px inset.
            Fields            = new OrigamiFieldMetrics(18f, 12f, 3f, 3f, 2f, 0f),
            EditorNumericScrub = true,
            Rounding          = Roundness,
            ContainerRounding = Roundness,
            SmallRounding     = MathF.Max(1f, Roundness * 0.5f),
            RowHeight         = RowHeight,
            HeaderHeight      = RowHeight,
            CompactHeight     = RowHeight - 4f,
            FontSize          = FontSize,
            FontSizeSmall     = FontSizeSmall,
            LabelWidth        = LabelWidth,
            // Spacing / padding scale derived from the two editor knobs (Spacing=4, Padding=6 reproduce
            // Origami's stock 2/4/6/8 and 4/6/12).
            SpacingSmall      = Spacing * 0.5f,
            Spacing           = Spacing,
            SpacingMedium     = Spacing * 1.5f,
            SpacingLarge      = Spacing * 2f,
            PaddingSmall      = Padding * (2f / 3f),
            Padding           = Padding,
            PaddingLarge      = Padding * 2f,
            TabBarHeight      = TabBarHeight,
            TabPadding        = TabPadding,
            TabGap            = TabGap,
            TabCloseSize      = TabCloseSize,
            SplitterSize      = SplitterSize,
            DockPadding       = DockPadding,
            IndicatorSize     = IndicatorSize,
            IndicatorGap      = IndicatorGap,
            // Editor surfaces use tinted fills only. Backdrop sampling is deliberately disabled
            // because it is not reliable across the supported presentation backends.
            WindowBackdropBlur = 0f,
        };

        t.Font         = Font;
        t.FontMedium   = FontMedium;
        t.FontSemiBold = FontSemiBold;
        t.FontBold     = FontBold;
        t.FontMono     = FontMono;
        t.Icons        = BuildIconSet();
        // Unity 6.7 sampled zebra (2026-09 parity): #3C3C3C / #2A2A2A — low contrast, both darker
        // than the old #424242/#686868 pair whose light stripe glared against the #383838 body.
        t.RowAlt       = Color.FromArgb(255, 60, 60, 60);
        t.RowEven      = Color.FromArgb(255, 42, 42, 42);
        return t;
    }

    /// <summary>The editor overrides Origami's built-in SVG icon set with Font Awesome glyphs (from the
    /// editor's <see cref="EditorIcons"/>) via the theme's <c>Icons</c> hook. Origami standalone keeps its
    /// own vector set. Falls back to Origami's defaults until the icon font is loaded.</summary>
    private static OrigamiIcons BuildIconSet()
    {
        // EditorGlyphIcon resolves the font at draw time, so chrome icons never flash blank before the
        // editor font finishes loading.
        XEngine.Editor.GUI.EditorGlyphIcon I(string glyph) => new(glyph, scale: 0.82f);
        return new OrigamiIcons
        {
            ChevronDown = I(EditorIcons.ChevronDown), ChevronRight = I(EditorIcons.ChevronRight),
            ChevronUp = I(EditorIcons.ChevronUp), ChevronLeft = I(EditorIcons.ChevronLeft),
            ArrowLeft = I(EditorIcons.ArrowLeft), ArrowRight = I(EditorIcons.ArrowRight),
            ArrowUp = I(EditorIcons.ArrowUp), ArrowDown = I(EditorIcons.ArrowDown),
            Check = I(EditorIcons.Check), Close = I(EditorIcons.Xmark),
            Info = I(EditorIcons.CircleInfo), Warning = I(EditorIcons.TriangleExclamation),
            Danger = I(EditorIcons.CircleExclamation), Success = I(EditorIcons.CircleCheck),
            CheckboxOff = I(EditorIcons.Square), CheckboxOn = I(EditorIcons.SquareCheck),
            Search = I(EditorIcons.MagnifyingGlass), More = I(EditorIcons.EllipsisVertical),
            Eye = I(EditorIcons.Eye), EyeOff = I(EditorIcons.EyeSlash),
            Plus = I(EditorIcons.Plus), Pencil = I(EditorIcons.Pencil),
            Trash = I(EditorIcons.Trash), Duplicate = I(EditorIcons.Copy),
            Folder = I(EditorIcons.Folder), FolderPlus = I(EditorIcons.FolderPlus),
            File = I(EditorIcons.File), Document = I(EditorIcons.FileLines),
            Drive = I(EditorIcons.HardDrive), Star = I(EditorIcons.Star),
            Clock = I(EditorIcons.Clock), Desktop = I(EditorIcons.Desktop),
            Download = I(EditorIcons.Download), User = I(EditorIcons.User),
        };
    }

    // -- Fonts ---------------------------------------------------------

    /// <summary>Regular (400) weight the editor's primary UI face.</summary>
    public static FontFile? DefaultFont;
    /// <summary>Bold (700) weight.</summary>
    public static FontFile? DefaultBoldFont;
    public static FontFile? FontMedium;
    public static FontFile? FontSemiBold;
    public static FontFile? FontMono;
    /// <summary>Display face for wordmarks / big headings (Space Grotesk).</summary>
    public static FontFile? FontDisplay;
    /// <summary>Logo face for the XENGINE wordmark (Audiowide).</summary>
    public static FontFile? FontLogo;
    /// <summary>Font Awesome solid (filled) glyphs, for drawing an icon directly in the filled style.</summary>
    public static FontFile? FontIconSolid;
    /// <summary>Font Awesome regular (outline) glyphs, for drawing an icon directly in the outline style.</summary>
    public static FontFile? FontIconOutline;

    /// <summary>
    /// Every fallback face registered on the editor's main Paper instance (icon fonts + the CJK /
    /// international chain), in registration order. Offscreen Paper instances — panel screenshots,
    /// for one — start with no fallbacks, so icons and non-Latin text come out blank unless they
    /// re-register these.
    /// </summary>
    public static readonly List<FontFile> FallbackFonts = new();

    public static FontFile? Font => DefaultFont;
    public static FontFile? FontBold => DefaultBoldFont ?? FontSemiBold ?? DefaultFont;

    public static string DefaultFontName = "Geist";
    public static string DefaultBoldFontName = "Geist";

    // DPI Scaling value
    public static float UserScale { get; set; } = 1f;

    // -- Sizing (locked Unity Dark density + 1px readability bump: 12px Geist Regular
    // rendered via SDF reads small/washed next to native chrome; 13/12 keeps the same
    // density while matching the perceived size of Segoe UI at the same point size) -----------
    public static float MenuBarHeight = 22f;
    public static float ToolbarHeight = 28f;
    public static float StatusBarHeight = 20f;
    public static float RowHeight = 18f;
    // Base spacing/padding the full Origami metric scale (SpacingSmall..PaddingLarge) is derived from
    // these in BuildOrigamiTheme, so tweaking them retunes gaps/padding everywhere (property grid, etc.).
    public static float Spacing = 3f;
    public static float Padding = 4f;
    public static float FontSize = 13f;
    public static float FontSizeSmall = 12f;
    public static float FontSizeLarge = FontSize + 2f;
    public static float FontSizeLogo = 72f;
    public static float LabelWidth = 150f;
    public static float Roundness = 2f;

    // -- Effects (mirrored from EditorThemeData; applied globally) -----
    public static EditorPerformanceProfile PerformanceProfile = EditorPerformanceProfile.Balanced;
    public static BackdropBlurMode BackdropBlurMode = BackdropBlurMode.Off;
    public static bool GlassBlur
    {
        get => BackdropBlurMode != BackdropBlurMode.Off;
        set => BackdropBlurMode = value ? BackdropBlurMode.Always : BackdropBlurMode.Off;
    }
    public static float BlurAmount = 22f;
    public static bool DropShadows = false;
    public static bool AccentGlow = false;
    public static bool AntiAliasing = true;
    public static bool AnimatedBackground = false;
    public static float BackgroundSpeed = 1f;
    public static EditorBackgroundStyle BackgroundStyle = EditorBackgroundStyle.Color;
    public static Color BackgroundColorA = Color.FromArgb(56, 56, 56);
    public static Color BackgroundColorB = Color.FromArgb(56, 56, 56);
    public static bool BgShowGradients = false;
    public static bool BgShowStars = false;
    public static bool BgShowComets = false;
    public static Color BackgroundVoidColor = Color.FromArgb(30, 30, 30);

    /// <summary>True when the (animated or static) nebula should be drawn rather than a gradient/solid.</summary>
    public static bool UsesNebulaBackground => AnimatedBackground || BackgroundStyle == EditorBackgroundStyle.Nebula;

    /// <summary>Backdrop blur is not supported by the editor UI.</summary>
    public static float EffectiveBlur => 0f;

    public static bool IsBackdropBlurActive(bool interactive) => false;

    public static float IndicatorSize = 28f;
    public static float IndicatorGap = 4f;
    public static float SplitterSize = 1f;
    public static float DockPadding = 1f;

    public static float SidePixelPadding = 10f;
    public static float VerticalNavbarSpacing = 4f;

    public static float TabBarHeight = 22f;
    public static float TabPadding = 8f;
    public static float TabCloseSize = 14f;
    public static float TabGap = 0f;

    // ==================================================================
    //  Colour tokens — views over the locked Unity Dark Origami theme.
    //  Neutral100/200/500 are editor-specific depth stops (void / shell / separator).
    // ==================================================================

    private static OrigamiTheme T => OrigamiTheme;

    // -- Neutral: editor depth stack. 100/200/500 are editor-specific; 300/400/600/700 map to the ramp. --
    public static Color Neutral100 => Color.FromArgb(255, 25, 25, 25);     // void #191919
    public static Color Neutral200 => Color.FromArgb(255, 56, 56, 56);     // app shell #383838
    public static Color Neutral300 => T.Neutral.C300;                      // headers / tab bars #454545
    public static Color Neutral400 => T.Neutral.C500;                      // cards / raised surface
    public static Color Neutral500 => Color.FromArgb(255, 25, 25, 25);     // border / separator #191919
    public static Color Neutral600 => T.Neutral.C600;
    public static Color Neutral700 => T.Neutral.C700;

    // -- Brand ramps (the editor's ★400 slot maps to the ramp's bright C500 stop). --
    public static Color Purple100 => T.Primary.C100;
    public static Color Purple200 => T.Primary.C200;
    public static Color Purple300 => T.Primary.C300;
    public static Color Purple400 => T.Primary.C500;
    public static Color Purple500 => T.Primary.C600;
    public static Color Purple600 => T.Primary.C700;
    public static Color Purple700 => T.Primary.C700;

    public static Color Blue100 => T.Blue.C100;
    public static Color Blue200 => T.Blue.C200;
    public static Color Blue300 => T.Blue.C300;
    public static Color Blue400 => T.Blue.C500;
    public static Color Blue500 => T.Blue.C600;
    public static Color Blue600 => T.Blue.C700;
    public static Color Blue700 => T.Blue.C700;

    public static Color Red100 => T.Red.C100;
    public static Color Red200 => T.Red.C200;
    public static Color Red300 => T.Red.C300;
    public static Color Red400 => T.Red.C500;
    public static Color Red500 => T.Red.C600;
    public static Color Red600 => T.Red.C700;
    public static Color Red700 => T.Red.C700;

    public static Color Green100 => T.Green.C100;
    public static Color Green200 => T.Green.C200;
    public static Color Green300 => T.Green.C300;
    public static Color Green400 => T.Green.C500;
    public static Color Green500 => T.Green.C600;
    public static Color Green600 => T.Green.C700;
    public static Color Green700 => T.Green.C700;

    public static Color Amber100 => T.Amber.C100;
    public static Color Amber200 => T.Amber.C200;
    public static Color Amber300 => T.Amber.C300;
    public static Color Amber400 => T.Amber.C500;
    public static Color Amber500 => T.Amber.C600;
    public static Color Amber600 => T.Amber.C700;
    public static Color Amber700 => T.Amber.C700;

    // -- Ink: borders (100/200 editor-specific) + text hierarchy (300 hint -> 500 primary). --
    public static Color Ink100 => Color.FromArgb(40, 120, 120, 120);
    public static Color Ink200 => Color.FromArgb(72, 150, 150, 150);
    public static Color Ink300 => T.Ink.C300;
    public static Color Ink400 => T.Ink.C400;
    public static Color Ink500 => T.Ink.C500;
    public static Color Ink600 => T.Ink.C600;
    public static Color Ink700 => T.Ink.C700;
    /// <summary>Dim text tier (below <see cref="Ink300"/>), for de-emphasised metadata.</summary>
    public static Color InkDim => T.Ink.C200;
    /// <summary>Faintest text tier, for the most-muted captions / placeholders.</summary>
    public static Color InkFaint => T.Ink.C100;

    // -- Semantic surfaces / states. --
    /// <summary>Toolbar / header fill (#454545).</summary>
    public static Color Glass => T.Glass;
    /// <summary>The shell toolbar band and dock tab strips (near-black #141414, per Unity 6.7).</summary>
    public static Color Toolbar => T.TabStrip;
    /// <summary>Active dock tab fill (#3C3C3C — brightest surface on the tab strip).</summary>
    public static Color TabActive => T.TabActive;
    /// <summary>Text / numeric / dropdown field fill (#282828).</summary>
    public static Color Field => T.Field;
    /// <summary>Menu / dropdown / popover surface (more opaque so it reads over anything).</summary>
    public static Color Popover => T.Popover;
    /// <summary>Soft hairline border / divider.</summary>
    public static Color BorderSoft => T.BorderSoft;
    /// <summary>Stronger border for focused / emphasised edges.</summary>
    public static Color BorderStrong => T.BorderStrong;
    /// <summary>Drop-shadow colour for popovers, dropdowns and modals.</summary>
    public static Color Shadow => T.Shadow;
    /// <summary>The bright accent (buttons, focus, active). Same as <see cref="Purple400"/>.</summary>
    public static Color Accent => T.Primary.C500;
    /// <summary>Brighter accent for hover.</summary>
    public static Color AccentBright => T.Primary.C600;
    /// <summary>Light accent for text / small highlights.</summary>
    public static Color AccentText => T.Primary.C700;

    public static Color Hover => T.Hover;
    public static Color Selected => T.Selected;
    /// <summary>Odd-row zebra stripe used by Hierarchy (and any tree that opts in).</summary>
    public static Color RowAlt => T.RowAlt;
    /// <summary>Even-row zebra stripe used by Hierarchy (and any tree that opts in).</summary>
    public static Color RowEven => T.RowEven;
    /// <summary>Pressed tool / overlay chrome. Unity Dark lifts the button gray; selection blue is for lists.</summary>
    public static Color ToolbarActive => Neutral600;

    /// <summary>Unity 6 Dark OS / File-menu chrome: light bar, dark ink (sampled #FFFFFF off 6000.7.0a4).</summary>
    public static Color MenuBar => Color.FromArgb(255, 255, 255, 255);
    public static Color MenuBarInk => Color.FromArgb(255, 32, 32, 32);
    public static Color MenuBarInkMuted => Color.FromArgb(255, 64, 64, 64);
    public static Color MenuBarHover => Color.FromArgb(255, 227, 227, 227);

    public static Color WithAlpha(Color c, int a) => Color.FromArgb(a, c.R, c.G, c.B);
}
