using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>Health bar layout (UnityHUD HUDBloodInfoObject port).</summary>
public sealed class HudBloodStyle
{
    public string BackgroundSprite = "Blood_bg";

    /// <summary>Fill sprite base name; per-relation fills append the relation index (_Fill_1..4).</summary>
    public string FillSpriteBase = "_Fill_";

    public Float2 BarSize = new(180f, 30f);
    public Float2 FillSize = new(160f, 13f);
    public Float2 FillOffset = new(10f, 10f);
    public HudFillAxis Axis = HudFillAxis.Horizontal;
    public bool Reverse;
    public HudAlignment Align = HudAlignment.Middle;
}

/// <summary>Chat bubble layout (UnityHUD HUDTalkInfoObject port).</summary>
public sealed class HudTalkStyle
{
    public string BubbleSprite = HudTalkWidget.DefaultBubbleSprite;
    public Float4 BgSlices = new(HudTalkWidget.BgSliceLeft, HudTalkWidget.BgSliceTop, HudTalkWidget.BgSliceRight, HudTalkWidget.BgSliceBottom); // L, T, R, B
    public Float4 ContentSlices = new(HudTalkWidget.ContentLeft, HudTalkWidget.ContentTop, HudTalkWidget.ContentRight, HudTalkWidget.ContentBottom);
    public int FontSize = HudTalkWidget.FontSize;
    public float MaxLineWidth = HudTalkWidget.MaxLineWidth;
    public float OutlineWidth = HudTalkWidget.OutlineWidth;
    public float ShowTime = HudTalkWidget.NormalShowTime;
    public float VanishTime = HudTalkWidget.VanishedTime;
    public float LineGap = HudTalkWidget.TalkLineGap;
    public HudAlignment Align = HudAlignment.Middle;
}

/// <summary>
/// Hudmesh package configuration (UnityHUD HUDConfigObject port): stack gaps, the bundled
/// art's bar/bubble layouts, and floating-number styles. Persisted as an Echo <c>.hudconfig</c>
/// asset; <see cref="HudManager.ChangeConfig"/> applies it live (UnityHUD's hot-swap configs).
/// </summary>
public sealed class HudmeshConfig : ScriptableObject
{
    public float SpriteLineGap = 12f;
    public float TextLineGap = 12f;

    public HudBloodStyle Blood = new();
    public HudTalkStyle Talk = new();

    public List<HudNumberStyle> NumberStyles = [new HudNumberStyle()];

    public static HudmeshConfig CreateDefault() => new() { Name = "DefaultHudmeshConfig" };
}
