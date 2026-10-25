using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using XEngine.OrigamiUI;

namespace XEngine.Editor.Theming;

/// <summary>Static editor-background style used when the animated background is off.</summary>
public enum EditorBackgroundStyle { Nebula, Gradient, Color }

/// <summary>
/// A color ramp with a single primary color. Other stops are computed from RGB offsets.
/// Optionally, all stops can be individually overridden.
/// </summary>
public class ColorRamp
{
    /// <summary>Primary color (the ★ stop) as hex.</summary>
    public string Primary { get; set; } = "#FF00FF";

    /// <summary>When true, use Overrides[] instead of computed offsets.</summary>
    public bool OverrideAll { get; set; }

    /// <summary>Per-stop override colors as hex. Only used when OverrideAll=true.</summary>
    public string[]? Overrides { get; set; }

    /// <summary>Number of stops in this ramp.</summary>
    [JsonIgnore] public int StopCount { get; set; }

    /// <summary>Index of the primary stop (0-based).</summary>
    [JsonIgnore] public int PrimaryIndex { get; set; }

    /// <summary>RGB offsets from primary for each stop. Set by Init().</summary>
    [JsonIgnore] public (int R, int G, int B)[] Offsets { get; set; } = [];

    /// <summary>Get the resolved color for a stop index.</summary>
    public Color GetStop(int index)
    {
        if (OverrideAll && Overrides != null && index < Overrides.Length)
            return ParseHex(Overrides[index]);

        if (index == PrimaryIndex)
            return ParseHex(Primary);

        var p = ParseHex(Primary);
        if (index >= Offsets.Length) return p;
        var o = Offsets[index];
        return Color.FromArgb(255,
            Math.Clamp(p.R + o.R, 0, 255),
            Math.Clamp(p.G + o.G, 0, 255),
            Math.Clamp(p.B + o.B, 0, 255));
    }

    /// <summary>Initialize offsets from default colors. Call once at startup.</summary>
    public void Init(int stopCount, int primaryIndex, Color[] defaults)
    {
        StopCount = stopCount;
        PrimaryIndex = primaryIndex;
        var pc = defaults[primaryIndex];
        Offsets = new (int, int, int)[stopCount];
        for (int i = 0; i < stopCount; i++)
            Offsets[i] = (defaults[i].R - pc.R, defaults[i].G - pc.G, defaults[i].B - pc.B);

        // Initialize overrides array from defaults if not set
        if (Overrides == null || Overrides.Length != stopCount)
        {
            Overrides = new string[stopCount];
            for (int i = 0; i < stopCount; i++)
                Overrides[i] = ColorToHex(defaults[i]);
        }
    }

    public static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color ParseHex(string hex)
    {
        try { return ColorTranslator.FromHtml(hex); }
        catch { return Color.Magenta; }
    }
}

/// <summary>
/// Serializable theme data. Ramps store a single primary color + optional per-stop overrides.
/// </summary>
public class EditorThemeData
{
    public string Name { get; set; } = "Unity Dark";

    // Color ramps — flat charcoal greys for Neutral (no purple tint), Unity blue accent.
    // OverrideAll=true bypasses the offset-from-default computation so stops are pure grey.
    public ColorRamp Neutral { get; set; } = new() {
        Primary = "#383838", OverrideAll = true,
        Overrides = new[] { "#1E1E1E", "#2A2A2A", "#454545", "#383838", "#3F3F3F", "#4A4A4A", "#565656" } };
    public ColorRamp Purple { get; set; } = new() {
        Primary = "#2C5D87", OverrideAll = true,
        Overrides = new[] { "#1A3348", "#1E3D54", "#244A66", "#285574", "#2C5D87", "#3A73A0", "#5A92B8" } };
    public ColorRamp Blue { get; set; } = new() { Primary = "#3A71D1" };
    public ColorRamp Red { get; set; } = new() { Primary = "#E05555" };
    public ColorRamp Green { get; set; } = new() { Primary = "#4ADE80" };
    public ColorRamp Amber { get; set; } = new() { Primary = "#FBBF24" };
    public ColorRamp Ink { get; set; } = new() {
        // C300/C400 brightness-tuned for SDF text clarity (mirror of OrigamiTheme.CreateDefaults).
        Primary = "#D4D4D4", OverrideAll = true,
        Overrides = new[] { "#5A5A5A", "#787878", "#A6A6A6", "#CACACA", "#D4D4D4", "#E8E8E8", "#FFFFFF" } };

    // Font
    public string DefaultFontName { get; set; } = "Geist";

    public string DefaultBoldFontName { get; set; } = "Geist";

    public float UserScale { get; set; } = 1f;

    // Sizing
    public float MenuBarHeight { get; set; } = 22f;
    public float StatusBarHeight { get; set; } = 20f;
    public float RowHeight { get; set; } = 18f;
    public float FontSize { get; set; } = 13f;
    public float LabelWidth { get; set; } = 140f;
    public float Spacing { get; set; } = 3f;
    public float Padding { get; set; } = 4f;
    // Single knob driving both the dock gutter padding and the splitter thickness.
    public float DockSpacing { get; set; } = 1f;
    public float TabBarHeight { get; set; } = 22f;
    public float TabPadding { get; set; } = 8f;
    public float Roundness { get; set; } = 2f;

    // Effects
    // Legacy effect fields remain for v1 settings migration. New settings use Performance.
    public bool GlassBlur { get; set; }
    public float BlurAmount { get; set; } = 22f;
    public bool DropShadows { get; set; }
    public bool AccentGlow { get; set; }
    public bool AntiAliasing { get; set; } = true;

    // Background: animated nebula, or a static style (frozen nebula / gradient / solid colour).
    public bool AnimatedBackground { get; set; } = false;
    public float BackgroundSpeed { get; set; } = 1f;
    public EditorBackgroundStyle BackgroundStyle { get; set; } = EditorBackgroundStyle.Color;
    public string BackgroundColorA { get; set; } = "#383838";
    public string BackgroundColorB { get; set; } = "#383838";

    // Nebula layer toggles + the raw void colour behind everything.
    public bool BgShowGradients { get; set; } = true;
    public bool BgShowStars { get; set; } = true;
    public bool BgShowComets { get; set; } = true;
    public string BackgroundVoidColor { get; set; } = "#1E1E1E";

    // Default ramp stops match OrigamiTheme.CreateDefaults (Unity Dark).
    private static readonly Color[] DefaultNeutral = [H("#1E1E1E"), H("#2A2A2A"), H("#454545"), H("#383838"), H("#3F3F3F"), H("#4A4A4A"), H("#565656")];
    private static readonly Color[] DefaultPurple  = [H("#1A3348"), H("#1E3D54"), H("#244A66"), H("#285574"), H("#2C5D87"), H("#3A73A0"), H("#5A92B8")];
    private static readonly Color[] DefaultBlue    = [H("#1A2A3D"), H("#1E3354"), H("#26487A"), H("#2F5C9D"), H("#3A71D1"), H("#5588DD"), H("#7BA8E6")];
    private static readonly Color[] DefaultRed     = [H("#3A1818"), H("#5A2424"), H("#8C3434"), H("#B04444"), H("#E05555"), H("#E87878"), H("#F0A0A0")];
    private static readonly Color[] DefaultGreen   = [H("#0F1F15"), H("#162C20"), H("#1F4530"), H("#2D6446"), H("#4ADE80"), H("#78E6A0"), H("#AAF0C3")];
    private static readonly Color[] DefaultAmber   = [H("#1F1808"), H("#3A2A10"), H("#5C4017"), H("#825C28"), H("#FBBF24"), H("#FCD060"), H("#FAE0A0")];
    private static readonly Color[] DefaultInk     = [H("#5A5A5A"), H("#787878"), H("#9A9A9A"), H("#B8B8B8"), H("#D4D4D4"), H("#E8E8E8"), H("#FFFFFF")];

    private static Color H(string hex) => ColorTranslator.FromHtml(hex);

    /// <summary>Initialize ramp offsets from the Origami defaults. Must be called after deserialization.</summary>
    public void InitRamps()
    {
        Neutral.Init(7, 4, DefaultNeutral);   // ★ = C500 (index 4)
        Purple.Init(7, 4, DefaultPurple);
        Blue.Init(7, 4, DefaultBlue);
        Red.Init(7, 4, DefaultRed);
        Green.Init(7, 4, DefaultGreen);
        Amber.Init(7, 4, DefaultAmber);
        Ink.Init(7, 4, DefaultInk);
    }

    /// <summary>Write Unity Dark chrome tokens onto an Origami theme (kept for tests / migration).</summary>
    public void ApplyTo(OrigamiTheme t)
    {
        ApplyRamp(t.Neutral, Neutral);
        ApplyRamp(t.Primary, Purple);
        ApplyRamp(t.Blue, Blue);
        ApplyRamp(t.Red, Red);
        ApplyRamp(t.Green, Green);
        ApplyRamp(t.Amber, Amber);
        ApplyRamp(t.Ink, Ink);

        t.Glass = Color.FromArgb(255, 69, 69, 69);
        t.Field = Color.FromArgb(255, 40, 40, 40);
        t.Popover = Color.FromArgb(255, 60, 60, 60);
        t.BorderSoft = Color.FromArgb(255, 25, 25, 25);
        t.BorderStrong = Color.FromArgb(255, 15, 15, 15);
        t.Shadow = Color.FromArgb(40, 0, 0, 0);
        t.Hover = Color.FromArgb(28, 255, 255, 255);
        t.Selected = Color.FromArgb(255, 44, 93, 135);
        t.RowAlt = Color.FromArgb(255, 66, 66, 66);
        t.RowEven = Color.FromArgb(255, 104, 104, 104);
    }

    private static void ApplyRamp(OrigamiRamp dst, ColorRamp src)
    {
        dst.C100 = KeepAlpha(dst.C100, src.GetStop(0));
        dst.C200 = KeepAlpha(dst.C200, src.GetStop(1));
        dst.C300 = KeepAlpha(dst.C300, src.GetStop(2));
        dst.C400 = KeepAlpha(dst.C400, src.GetStop(3));
        dst.C500 = KeepAlpha(dst.C500, src.GetStop(4));
        dst.C600 = KeepAlpha(dst.C600, src.GetStop(5));
        dst.C700 = KeepAlpha(dst.C700, src.GetStop(6));
    }

    private static Color KeepAlpha(Color dst, Color rgb) => Color.FromArgb(dst.A, rgb.R, rgb.G, rgb.B);

    public EditorThemeData Clone()
    {
        var json = JsonSerializer.Serialize(this);
        var clone = JsonSerializer.Deserialize<EditorThemeData>(json)!;
        clone.InitRamps();
        return clone;
    }

    public void ExportToFile(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public static EditorThemeData? ImportFromFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<EditorThemeData>(json);
            data?.InitRamps();
            return data;
        }
        catch { return null; }
    }

    public static EditorThemeData CreateDefault()
    {
        var d = new EditorThemeData();
        d.InitRamps();
        return d;
    }
}
