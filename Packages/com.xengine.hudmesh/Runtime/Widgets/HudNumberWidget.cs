using System.Collections.Generic;

using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>
/// Floating damage number (UnityHUD HUDNumberMesh port): a dynamic widget with a style, a
/// lifetime driven by the style's curves, camera-space spawn offset, and two glyph modes —
/// digit-atlas art or SDF text. Not attached to the stack; expiring sets IsValid false and
/// the owning <see cref="HudObject"/> recycles it.
/// </summary>
[Pool]
internal sealed class HudNumberWidget : HudWidget
{
    private const float PixelsPerUnit = HudManager.DefaultPixelsPerUnit;

    private HudNumberStyle? _style;
    private readonly HudQuad[] _digitQuads = new HudQuad[24]; // sign + up to 20 digits + headroom
    private int _digitCount;
    private int _digitSlot = -1;

    // SDF mode state
    private TextLayout? _layout;
    private HudFontRenderer? _renderer;
    private float _blockW;
    private float _blockH;

    private float _startTime;
    private float _endTime;
    private float _alpha = 1f;
    private Float2 _startOffset;

    private static readonly Color32 Color32White = new(255, 255, 255, 255);

    public void Push(HudNumberStyle style, int number, HudSpriteTable sprites,
        Float3 spawnPos, Float3 roleOffset, float now)
    {
        _style = style;
        Anchor = spawnPos;
        RoleOffset = roleOffset;
        _startTime = now;
        _endTime = now + style.Lifetime;
        _alpha = 1f;
        _startOffset = Float2.Zero;
        Offset = _startOffset;
        Scale = 1f;

        string text = BuildNumberText(style, number);
        if (style.Mode == HudNumberMode.DigitAtlas)
        {
            _layout = null;
            WriteDigits(text, sprites);
        }
        else
        {
            BuildTextLayout(text, style);
        }

        IsValid = true;
    }

    private static string BuildNumberText(HudNumberStyle style, int number)
    {
        if (style.Sign && number > 0)
            return "+" + number.ToString();
        if (!style.Sign && number < 0)
            return (-number).ToString();
        return number.ToString();
    }

    public override void Tick(float now)
    {
        if (!IsValid || _style is null)
            return;
        if (now >= _endTime)
        {
            IsValid = false;
            return;
        }

        float d = now - _startTime;
        _alpha = _style.AlphaCurve.Keys.Count > 0 ? _style.AlphaCurve.Evaluate(d) : 1f;
        Scale = _style.ScaleCurve.Keys.Count > 0 ? _style.ScaleCurve.Evaluate(d) : 1f;
        float move = _style.MoveCurve.Keys.Count > 0 ? _style.MoveCurve.Evaluate(d) : 0f;
        Offset = new Float2(_startOffset.X, _startOffset.Y + move * PixelsPerUnit);
    }

    public override void Fill(List<HudMeshData?> targets, in HudFillContext context)
    {
        if (_style is null)
            return;

        if (_style.Mode == HudNumberMode.DigitAtlas)
        {
            if (_digitSlot < 0 || _digitSlot >= targets.Count)
                return;
            HudMeshData? target = targets[_digitSlot];
            if (target is null)
                return;
            // Quad colors are per-frame state, so writing the tinted color directly is safe.
            Color32 c = MultiplyAlpha(_style.Color, _alpha);
            for (int i = 0; i < _digitCount; i++)
            {
                ref HudQuad q = ref _digitQuads[i];
                q.ColorBL = c; q.ColorBR = c; q.ColorTR = c; q.ColorTL = c;
                target.AppendQuad(Anchor, context.RightStep, context.UpStep, q, Offset.X, Offset.Y, Scale);
            }
        }
        else if (_layout is not null)
        {
            _renderer ??= new HudFontRenderer();
            if (targets.Count == 0 || targets[0] is not HudMeshData fontTarget)
                return;

            FontColor color = new(
                _style.Color.R * _alpha,
                _style.Color.G * _alpha,
                _style.Color.B * _alpha,
                _style.Color.A * _alpha);
            float shift = HudTextWidget.AlignShift(_style.Align);

            if (_style.OutlineWidth > 0f)
            {
                FontColor outline = new(_style.OutlineColor.R, _style.OutlineColor.G,
                    _style.OutlineColor.B, _style.OutlineColor.A * _alpha);
                EmitPass(fontTarget, context, shift, outline, -_style.OutlineWidth, -_style.OutlineWidth);
                EmitPass(fontTarget, context, shift, outline, -_style.OutlineWidth, _style.OutlineWidth);
                EmitPass(fontTarget, context, shift, outline, _style.OutlineWidth, -_style.OutlineWidth);
                EmitPass(fontTarget, context, shift, outline, _style.OutlineWidth, _style.OutlineWidth);
            }
            EmitPass(fontTarget, context, shift, color, 0f, 0f);
        }
    }

    private void EmitPass(HudMeshData target, in HudFillContext context, float shift,
        in FontColor color, float passOffsetX, float passOffsetY)
    {
        _renderer!.Begin(target, Anchor, context, Offset.X + passOffsetX, Offset.Y + passOffsetY,
            Scale, shift, _blockW, _blockH, Color.White,
            Color32White, Color32White, Color32White, Color32White);
        UIFontSystem.Default.System.DrawLayout(_layout, Float2.Zero, color, _renderer);
    }

    private void WriteDigits(string digits, HudSpriteTable table)
    {
        _digitCount = 0;
        _digitSlot = -1;

        float width = 0f;
        float height = 0f;
        for (int i = 0; i < digits.Length && _digitCount < _digitQuads.Length; i++)
        {
            HudSpriteInfo? info = table.Get(string.Concat(_style!.Prefix, digits[i]));
            if (info is null)
                continue;
            if (info.MaterialSlot >= 0)
                _digitSlot = info.MaterialSlot;

            ref HudQuad q = ref _digitQuads[_digitCount++];
            q.OffsetBL = new Float2(width, 0f);
            q.OffsetBR = new Float2(width + info.Width, 0f);
            q.OffsetTR = new Float2(width + info.Width, info.Height);
            q.OffsetTL = new Float2(width, info.Height);
            q.UvBL = info.UvMin;
            q.UvBR = new Float2(info.UvMax.X, info.UvMin.Y);
            q.UvTR = info.UvMax;
            q.UvTL = new Float2(info.UvMin.X, info.UvMax.Y);
            q.ColorBL = Color32White;
            q.ColorBR = Color32White;
            q.ColorTR = Color32White;
            q.ColorTL = Color32White;

            width += info.Width + _style.Gap;
            height = float.Max(height, info.Height);
        }

        width -= _style!.Gap;
        float shift = HudTextWidget.AlignShift(_style.Align) * width;
        for (int i = 0; i < _digitCount; i++)
        {
            ref HudQuad q = ref _digitQuads[i];
            q.OffsetBL = new Float2(q.OffsetBL.X - shift, q.OffsetBL.Y);
            q.OffsetBR = new Float2(q.OffsetBR.X - shift, q.OffsetBR.Y);
            q.OffsetTR = new Float2(q.OffsetTR.X - shift, q.OffsetTR.Y);
            q.OffsetTL = new Float2(q.OffsetTL.X - shift, q.OffsetTL.Y);
        }
        Size = new Float2(width, height);
    }

    private void BuildTextLayout(string text, HudNumberStyle style)
    {
        FontAsset font = FontAsset.LoadDefault();
        if (font.FontFile is null)
        {
            _layout = null;
            return;
        }
        var settings = new TextLayoutSettings
        {
            Font = font.FontFile,
            PixelSize = Maths.Max(1, style.FontSize),
            Alignment = Scribe.TextAlignment.Left,
            WrapMode = TextWrapMode.NoWrap,
            LineHeight = 1.0f,
        };
        _layout = UIFontSystem.Default.System.CreateLayout(text, settings);
        _blockW = _layout.Size.X;
        _blockH = _layout.Size.Y;
        Size = new Float2(_blockW, _blockH);
    }

    internal static Color32 MultiplyAlpha(in Color color, float alpha)
        => new(
            (byte)color.R,
            (byte)color.G,
            (byte)color.B,
            (byte)(color.A * float.Clamp(alpha, 0f, 1f)));

    public override void Release()
    {
        base.Release();
        _style = null;
        _digitCount = 0;
        _digitSlot = -1;
        _layout = null;
        _alpha = 1f;
    }
}
