// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Drawing;

namespace XEngine.OrigamiUI;

/// <summary>
/// Complete visual configuration for an Origami widget tree.
///
/// <para>Colour structure mirrors the host editor's theme: a Neutral surface ramp,
/// branded ramps (<see cref="Primary"/>, <see cref="Blue"/>, <see cref="Red"/>,
/// <see cref="Green"/>, <see cref="Amber"/>), and a foreground/text ramp
/// (<see cref="Ink"/>). <see cref="OrigamiVariant"/> picks which surface ramp a
/// widget reads via <see cref="Get(OrigamiVariant)"/>.</para>
///
/// <para>Apply globally with <see cref="Origami.SetTheme"/>; scope to a region with
/// <see cref="Origami.PushTheme"/>.</para>
/// </summary>
public sealed class OrigamiTheme
{
    // ── Surface ramps (7 stops each, dark → light) ──────────────────

    /// <summary>Neutral grayscale ramp — backgrounds, panels, default surfaces.</summary>
    public OrigamiRamp Neutral = null!;

    /// <summary>Brand ramp — used by the <see cref="OrigamiVariant.Primary"/> variant.</summary>
    public OrigamiRamp Primary = null!;

    /// <summary>Blue ramp — used by the <see cref="OrigamiVariant.Info"/> variant.</summary>
    public OrigamiRamp Blue = null!;

    /// <summary>Red ramp — used by the <see cref="OrigamiVariant.Danger"/> variant.</summary>
    public OrigamiRamp Red = null!;

    /// <summary>Green ramp — used by the <see cref="OrigamiVariant.Success"/> variant.</summary>
    public OrigamiRamp Green = null!;

    /// <summary>Amber ramp — used by the <see cref="OrigamiVariant.Warning"/> variant.</summary>
    public OrigamiRamp Amber = null!;

    // ── Foreground ──────────────────────────────────────────────────

    /// <summary>
    /// Text/foreground ramp. Same shape as the surface ramps so widgets read it uniformly.
    /// Conventions: <c>C100</c> dimmest (rarely used as text), <c>C300</c> muted/disabled,
    /// <c>C500</c> primary labels, <c>C600</c>/<c>C700</c> "extra bright" emphasis (typically
    /// used for hover/focused text on dark surfaces).
    /// </summary>
    public OrigamiRamp Ink = null!;

    // ── Semantic surface / state colours ────────────────────────────
    // Surfaces and borders that aren't a single ramp stop. Explicit (not ramp-derived) so a host can
    // retint them independently; the accent-tinted states are computed off Primary so they follow it.

    /// <summary>Inset fill for toolbars, headers, tag pills and chrome within a panel.</summary>
    public Color Glass;

    /// <summary>Inset fill for text / numeric / dropdown fields (darker than <see cref="Glass"/>).</summary>
    public Color Field;

    /// <summary>Menu / dropdown / popover surface — more opaque than panels so it reads over anything.</summary>
    public Color Popover;

    /// <summary>Optional desktop popup-menu presentation. Null preserves the
    /// standalone menu geometry, palette and persistent toggle behavior.</summary>
    public OrigamiMenuStyle? Menus;

    /// <summary>Dock tab-bar strip and the shell toolbar band (near-black in Unity 6 dark; the active
    /// tab on it reads as the brightest surface).</summary>
    public Color TabStrip;

    /// <summary>Active dock tab fill — sits on <see cref="TabStrip"/> and reads lighter than it.</summary>
    public Color TabActive;

    /// <summary>Soft hairline border / divider (very low alpha).</summary>
    public Color BorderSoft;

    /// <summary>Stronger border for focused / emphasised edges (e.g. popover outlines).</summary>
    public Color BorderStrong;

    /// <summary>Drop-shadow colour for popovers, dropdowns and modals.</summary>
    public Color Shadow;

    /// <summary>Row / control hover overlay.</summary>
    public Color Hover;

    /// <summary>Selected / active fill (hierarchy, lists, toggles).</summary>
    public Color Selected;

    /// <summary>Optional focused/inactive collection selection presentation.</summary>
    public OrigamiSelectionStyle? SelectionStyle;

    /// <summary>Opt in to target mode badges in the drag ghost. Standalone defaults stay unchanged.</summary>
    public bool DragFeedback;

    public Color SelectionBackground(bool focused) => !focused && SelectionStyle is { } style
        ? style.InactiveBackground : Selected;

    public Color SelectionText(bool focused) => SelectionStyle is { } style
        ? (focused ? style.ActiveText : style.InactiveText) : Ink.C600;

    /// <summary>Odd-row zebra stripe fill for trees that opt in via <c>ZebraStripes()</c>.</summary>
    public Color RowAlt;

    /// <summary>Even-row zebra stripe fill. Paired with <see cref="RowAlt"/>.</summary>
    public Color RowEven;

    /// <summary>Return <paramref name="c"/> with a new alpha (0-255). Handy for state overlays.</summary>
    public static Color WithAlpha(Color c, int a) => Color.FromArgb(a, c.R, c.G, c.B);

    // ── Sizing / icons / font ───────────────────────────────────────

    public OrigamiMetrics Metrics = new();
    public OrigamiIcons Icons = new();

    /// <summary>Regular (400) weight face. Widgets fall back to this when a heavier weight is unset.</summary>
    public XEngine.Scribe.FontFile? Font;

    /// <summary>Medium (500) weight face. Optional — falls back to <see cref="Font"/>.</summary>
    public XEngine.Scribe.FontFile? FontMedium;

    /// <summary>Semi-bold (600) weight face. Optional — falls back to Medium then <see cref="Font"/>.</summary>
    public XEngine.Scribe.FontFile? FontSemiBold;

    /// <summary>Bold (700) weight face. Optional — falls back to SemiBold, Medium, then <see cref="Font"/>.</summary>
    public XEngine.Scribe.FontFile? FontBold;

    /// <summary>Monospace face for value/code fields (numeric, vector, color, hex). Falls back to <see cref="Font"/>.</summary>
    public XEngine.Scribe.FontFile? FontMono;
    /// <summary>Resolved monospace face, or the regular face if none supplied.</summary>
    public XEngine.Scribe.FontFile? Mono => FontMono ?? Font;

    /// <summary>Resolved medium (500) weight, or the regular face if none supplied.</summary>
    public XEngine.Scribe.FontFile? Medium => FontMedium ?? Font;

    /// <summary>Resolved semi-bold (600) weight, or the closest lighter face supplied.</summary>
    public XEngine.Scribe.FontFile? SemiBold => FontSemiBold ?? FontMedium ?? Font;

    /// <summary>Resolved bold (700) weight, or the closest lighter face supplied.</summary>
    public XEngine.Scribe.FontFile? Bold => FontBold ?? FontSemiBold ?? FontMedium ?? Font;

    /// <summary>
    /// Map a variant to its surface ramp. <see cref="OrigamiVariant.Default"/> and
    /// <see cref="OrigamiVariant.Subtle"/> both point at <see cref="Neutral"/>; widgets
    /// distinguish Subtle by treating low ramp stops as transparent (typically suppressing
    /// the idle background entirely).
    /// </summary>
    public OrigamiRamp Get(OrigamiVariant variant) => variant switch
    {
        OrigamiVariant.Primary => Primary,
        OrigamiVariant.Info => Blue,
        OrigamiVariant.Danger => Red,
        OrigamiVariant.Success => Green,
        OrigamiVariant.Warning => Amber,
        OrigamiVariant.Subtle => Neutral,
        _ => Neutral,
    };

    public OrigamiTheme Clone() => new()
    {
        Neutral = Neutral.Clone(),
        Primary = Primary.Clone(),
        Blue = Blue.Clone(),
        Red = Red.Clone(),
        Green = Green.Clone(),
        Amber = Amber.Clone(),
        Ink = Ink.Clone(),
        Glass = Glass,
        Field = Field,
        Popover = Popover,
        Menus = Menus,
        TabStrip = TabStrip,
        TabActive = TabActive,
        BorderSoft = BorderSoft,
        BorderStrong = BorderStrong,
        Shadow = Shadow,
        Hover = Hover,
        Selected = Selected,
        SelectionStyle = SelectionStyle,
        DragFeedback = DragFeedback,
        RowAlt = RowAlt,
        RowEven = RowEven,
        Metrics = Metrics.Clone(),
        Icons = Icons.Clone(),
        Font = Font,
        FontMedium = FontMedium,
        FontSemiBold = FontSemiBold,
        FontBold = FontBold,
        FontMono = FontMono,
    };

    /// <summary>
    /// Linearly interpolate the lerpable parts (ramps, ink, metrics) between two themes.
    /// Non-lerpable members (font, icons) snap to <paramref name="b"/> at the start of the
    /// transition.
    /// </summary>
    public static OrigamiTheme Lerp(OrigamiTheme a, OrigamiTheme b, float t) => new()
    {
        Neutral = OrigamiRamp.Lerp(a.Neutral, b.Neutral, t),
        Primary = OrigamiRamp.Lerp(a.Primary, b.Primary, t),
        Blue = OrigamiRamp.Lerp(a.Blue, b.Blue, t),
        Red = OrigamiRamp.Lerp(a.Red, b.Red, t),
        Green = OrigamiRamp.Lerp(a.Green, b.Green, t),
        Amber = OrigamiRamp.Lerp(a.Amber, b.Amber, t),
        Ink = OrigamiRamp.Lerp(a.Ink, b.Ink, t),
        Glass = OrigamiRamp.LerpColor(a.Glass, b.Glass, t),
        Field = OrigamiRamp.LerpColor(a.Field, b.Field, t),
        Popover = OrigamiRamp.LerpColor(a.Popover, b.Popover, t),
        Menus = OrigamiMenuStyle.Lerp(a.Menus, b.Menus, t),
        TabStrip = OrigamiRamp.LerpColor(a.TabStrip, b.TabStrip, t),
        TabActive = OrigamiRamp.LerpColor(a.TabActive, b.TabActive, t),
        BorderSoft = OrigamiRamp.LerpColor(a.BorderSoft, b.BorderSoft, t),
        BorderStrong = OrigamiRamp.LerpColor(a.BorderStrong, b.BorderStrong, t),
        Shadow = OrigamiRamp.LerpColor(a.Shadow, b.Shadow, t),
        Hover = OrigamiRamp.LerpColor(a.Hover, b.Hover, t),
        Selected = OrigamiRamp.LerpColor(a.Selected, b.Selected, t),
        SelectionStyle = OrigamiSelectionStyle.Lerp(a.SelectionStyle, b.SelectionStyle, t),
        DragFeedback = b.DragFeedback,
        RowAlt = OrigamiRamp.LerpColor(a.RowAlt, b.RowAlt, t),
        RowEven = OrigamiRamp.LerpColor(a.RowEven, b.RowEven, t),
        Metrics = OrigamiMetrics.Lerp(a.Metrics, b.Metrics, t),
        FontMedium = b.FontMedium,
        FontSemiBold = b.FontSemiBold,
        FontBold = b.FontBold,
        FontMono = b.FontMono,
        Icons = b.Icons,
        Font = b.Font,
    };

    /// <summary>
    /// Standalone defaults — Unity 6 Professional Dark: opaque charcoal chrome, blue selection.
    /// Used when no host has called <see cref="Origami.SetTheme"/>.
    /// </summary>
    public static OrigamiTheme CreateDefaults() => new()
    {
        // Neutral: C100 void, C200 widget track, C300 tab/header #454545, C400 body #383838.
        Neutral = Ramp("#1E1E1E", "#2A2A2A", "#454545", "#383838", "#3F3F3F", "#4A4A4A", "#565656"),
        // Primary = Unity hierarchy selection blue (#2C5D87 at C500).
        Primary = Ramp("#1A3348", "#1E3D54", "#244A66", "#285574", "#2C5D87", "#3A73A0", "#5A92B8"),
        Blue = Ramp("#1A2A3D", "#1E3354", "#26487A", "#2F5C9D", "#3A71D1", "#5588DD", "#7BA8E6"),
        Red = Ramp("#3A1818", "#5A2424", "#8C3434", "#B04444", "#E05555", "#E87878", "#F0A0A0"),
        Green = Ramp("#0F1F15", "#162C20", "#1F4530", "#2D6446", "#4ADE80", "#78E6A0", "#AAF0C3"),
        Amber = Ramp("#1F1808", "#3A2A10", "#5C4017", "#825C28", "#FBBF24", "#FCD060", "#FAE0A0"),
        // Ink: C300 muted, C500 primary labels #D4D4D4. C300/C400 are brightness-tuned for
        // SDF grayscale text on #383838 surfaces (2026-08 clarity pass): low-contrast gray
        // body text was the dominant remaining "blurry" complaint after edge fixes.
        Ink = Ramp("#5A5A5A", "#787878", "#A6A6A6", "#CACACA", "#D4D4D4", "#E8E8E8", "#FFFFFF"),

        Glass = Hex("#454545"),
        Field = Hex("#282828"),
        Popover = Hex("#3C3C3C"),
        // Dock tab bars / shell toolbar strip and the active tab on them, sampled from
        // Unity 6.7 (2026-09 parity pass): near-black strip, active tab as the brightest
        // surface on it — the inverse of the old #454545-bar-with-darker-tab arrangement.
        TabStrip = Hex("#141414"),
        TabActive = Hex("#3C3C3C"),
        BorderSoft = Hex("#191919"),
        BorderStrong = Hex("#0F0F0F"),
        Shadow = Color.FromArgb(40, 0, 0, 0),
        Hover = Color.FromArgb(28, 255, 255, 255),
        Selected = Hex("#2C5D87"),
        RowAlt = Hex("#3C3C3C"),
        RowEven = Hex("#2A2A2A"),
    };

    private static Color Hex(string s) => ColorTranslator.FromHtml(s);

    private static OrigamiRamp Ramp(string c1, string c2, string c3, string c4, string c5, string c6, string c7) => new()
    {
        C100 = Hex(c1),
        C200 = Hex(c2),
        C300 = Hex(c3),
        C400 = Hex(c4),
        C500 = Hex(c5),
        C600 = Hex(c6),
        C700 = Hex(c7),
    };
}
