using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Scribe;
using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>
/// Text slot widget backed by the engine's shared SDF font system. Layout is shaped once per
/// (text, style) change via Scribe; every fill re-emits glyph quads straight into the slot-0
/// combined mesh through <see cref="HudFontRenderer"/> — so atlas growth automatically flows
/// through without rebuild hooks (Scribe re-lays-out stale layouts on draw).
/// </summary>
internal sealed class HudTextWidget : HudWidget
{
    public string Text = "";
    public HudAlignment Align;

    public Color Color = Color.White;

    /// <summary>Per-corner multiply applied on top of <see cref="Color"/> (UnityHUD's corner gradient).</summary>
    public Color32 GradientBL = Color32White;
    public Color32 GradientBR = Color32White;
    public Color32 GradientTL = Color32White;
    public Color32 GradientTR = Color32White;

    public Color OutlineColor = Color.White;
    public float OutlineWidth;

    public int FontSize = 22;
    public float MaxWidth;
    public FontAsset? Font;

    private TextLayout? _layout;
    private int _layoutSig;
    private readonly HudFontRenderer _renderer = new();

    private static readonly Color32 Color32White = new(255, 255, 255, 255);

    /// <summary>Horizontal fraction of the block width the anchor sits at (0 left, 0.5 middle, 1 right).</summary>
    internal static float AlignShift(HudAlignment align)
        => align switch
        {
            HudAlignment.Middle => 0.5f,
            HudAlignment.Right => 1f,
            _ => 0f,
        };

    public void PushText(string text, HudAlignment align)
    {
        Text = text ?? "";
        Align = align;
        RebuildLayout();
        IsValid = Text.Length > 0 && _layout is not null;
    }

    private void RebuildLayout()
    {
        FontAsset font = Font ?? FontAsset.LoadDefault();
        if (font.FontFile is null || Text.Length == 0)
        {
            _layout = null;
            Size = Float2.Zero;
            return;
        }

        int sig = HashCode.Combine(Text, FontSize, MaxWidth, AlignShift(Align),
            RuntimeHelpers.GetHashCode(font.FontFile));
        if (_layout is not null && sig == _layoutSig)
            return;

        var settings = new TextLayoutSettings
        {
            Font = font.FontFile,
            PixelSize = Maths.Max(1, FontSize),
            Alignment = Scribe.TextAlignment.Left, // block alignment is applied at fill time
            MaxWidth = MaxWidth,
            WrapMode = MaxWidth > 0f ? TextWrapMode.Wrap : TextWrapMode.NoWrap,
            LineHeight = 1.0f,
        };
        _layout = UIFontSystem.Default.System.CreateLayout(Text, settings);
        _layoutSig = sig;
        Size = new Float2(_layout.Size.X, _layout.Size.Y);
    }

    public override void Fill(List<HudMeshData?> targets, in HudFillContext context)
    {
        RebuildLayout(); // text may have changed since PushText; Scribe refreshes stale layouts on draw
        if (_layout is null || targets.Count == 0)
            return;
        HudMeshData? target = targets[0]; // slot 0 = shared font material
        if (target is null)
            return;

        FontColor fill = new(Color.R, Color.G, Color.B, Color.A);
        float blockW = _layout.Size.X;
        float blockH = _layout.Size.Y;
        float shift = AlignShift(Align);

        if (OutlineWidth > 0f)
        {
            FontColor outline = new(OutlineColor.R, OutlineColor.G, OutlineColor.B, OutlineColor.A);
            EmitPass(target, context, blockW, blockH, shift, outline, -OutlineWidth, -OutlineWidth);
            EmitPass(target, context, blockW, blockH, shift, outline, -OutlineWidth, OutlineWidth);
            EmitPass(target, context, blockW, blockH, shift, outline, OutlineWidth, -OutlineWidth);
            EmitPass(target, context, blockW, blockH, shift, outline, OutlineWidth, OutlineWidth);
        }

        EmitPass(target, context, blockW, blockH, shift, fill, 0f, 0f);
    }

    private void EmitPass(HudMeshData target, in HudFillContext context, float blockW, float blockH,
        float shift, in FontColor color, float passOffsetX, float passOffsetY)
    {
        _renderer.Begin(target, Anchor, context, Offset.X + passOffsetX, Offset.Y + passOffsetY,
            Scale, shift, blockW, blockH, Color, GradientBL, GradientBR, GradientTL, GradientTR);
        UIFontSystem.Default.System.DrawLayout(_layout, Float2.Zero, color, _renderer);
    }

    public override void Release()
    {
        base.Release();
        Text = "";
        _layout = null;
        _layoutSig = 0;
    }
}
