using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>
/// Chat bubble (UnityHUD HUDTalkMesh port): nine-sliced bubble auto-sized to the wrapped text
/// plus an embedded SDF text block. A newer bubble marks the previous one ReadyRecycle, which
/// accelerates its fade-out window (VanishedTime) instead of popping it instantly.
/// </summary>
[Pool]
internal sealed class HudTalkWidget : HudWidget
{
    // Bundled default style (UnityHUD TalkInfoArray[0]); config assets replace these (N8).
    public const string DefaultBubbleSprite = "UI_headtop_bubble_001_17";
    public const float BgSliceLeft = 46f, BgSliceTop = 16f, BgSliceRight = 16f, BgSliceBottom = 32f;
    public const float ContentLeft = 16f, ContentTop = 16f, ContentRight = 16f, ContentBottom = 32f;
    public const int FontSize = 20;
    public const float MaxLineWidth = 266f;
    public const float OutlineWidth = 1f;
    public const float NormalShowTime = 6f;
    public const float VanishedTime = 1f;
    public const float TalkLineGap = 20f;

    private readonly HudQuad[] _bg = new HudQuad[9];
    private readonly HudTextWidget _text = new();
    private HudSpriteInfo? _bgSprite;
    private int _bgSlot = -1;
    private float _bubbleW;
    private float _bubbleH;

    public bool ReadyRecycle;
    private HudTalkStyle? _style;
    private float _lastNow;
    private float _showTimer;
    private float _vanishTimer;

    public void PushTalk(HudSpriteTable sprites, string content, float now)
        => PushTalk(new HudTalkStyle(), sprites, content, now);

    public void PushTalk(HudTalkStyle style, HudSpriteTable sprites, string content, float now)
    {
        _style = style;
        _bgSprite = sprites.Get(style.BubbleSprite);
        _bgSlot = _bgSprite?.MaterialSlot ?? -1;
        ReadyRecycle = false;
        _showTimer = 0f;
        _vanishTimer = 0f;
        _lastNow = now;

        _text.FontSize = style.FontSize;
        _text.MaxWidth = style.MaxLineWidth;
        _text.OutlineWidth = style.OutlineWidth;
        _text.PushText(content, HudAlignment.Left);

        Float2 textSize = _text.Size;
        _bubbleW = float.Max(style.ContentSlices.X + textSize.X + style.ContentSlices.Z,
            style.BgSlices.X + style.BgSlices.Z);
        _bubbleH = float.Max(style.ContentSlices.W + textSize.Y + style.ContentSlices.Y,
            style.BgSlices.W + style.BgSlices.Y);
        if (_bgSprite is not null)
        {
            HudGeometry.WriteSlicedSprite(_bg, _bgSprite, new Float2(_bubbleW, _bubbleH), HudAlignment.Middle);
            Size = new Float2(_bubbleW, _bubbleH);
        }
        IsValid = true;
    }

    public override void Tick(float now)
    {
        if (!IsValid)
            return;
        float dt = now - _lastNow;
        _lastNow = now;

        _showTimer += dt;
        if (ReadyRecycle)
            _vanishTimer += dt;

        float showTime = _style?.ShowTime ?? NormalShowTime;
        float vanishTime = _style?.VanishTime ?? VanishedTime;
        // Expire when the show window runs out, or sooner when a newer bubble
        // replaced this one (accelerated VanishedTime window).
        if (_showTimer >= showTime || (ReadyRecycle && _vanishTimer >= vanishTime))
            IsValid = false;
    }

    public override void Fill(List<HudMeshData?> targets, in HudFillContext context)
    {
        // Bubble background.
        if (_bgSprite is not null && _bgSlot >= 0 && _bgSlot < targets.Count
            && targets[_bgSlot] is HudMeshData bgTarget)
        {
            for (int i = 0; i < _bg.Length; i++)
                bgTarget.AppendQuad(Anchor, context.RightStep, context.UpStep, _bg[i], Offset.X, Offset.Y, Scale);
        }

        // Text block: position per UnityHUD's Rebuild (Middle branch), then reuse the text widget fill.
        _text.Anchor = Anchor;
        _text.Scale = Scale;
        _text.Offset = new Float2(
            Offset.X + ContentLeft - _bubbleW * 0.5f,
            Offset.Y + ContentBottom + _text.Size.Y - FontSize);
        _text.Fill(targets, context);
    }

    public override void Release()
    {
        base.Release();
        _text.Release();
        _bgSprite = null;
        _bgSlot = -1;
        _style = null;
        ReadyRecycle = false;
        _showTimer = 0f;
        _vanishTimer = 0f;
    }
}
