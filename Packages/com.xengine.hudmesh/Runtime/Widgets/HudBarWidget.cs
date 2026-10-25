using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>
/// Sliced bar widget: a full-amount sliced background plus a live-percent sliced fill
/// (UnityHUD HUDBloodMesh port, including the alignment-corrected fill offset). The
/// background and fill may come from different textures and land in different combined
/// meshes. PushValue re-parses only the fill segments.
/// </summary>
internal sealed class HudBarWidget : HudWidget
{
    private readonly HudQuad[] _bg = new HudQuad[3];
    private readonly HudQuad[] _fill = new HudQuad[3];

    private HudSpriteInfo? _background;
    private HudSpriteInfo? _fillSprite;
    private Float2 _fillSize;
    private Float2 _fillOffset; // align-corrected (UnityHUD bd_offset)
    private float _percent = 1f;

    public HudFillAxis Axis = HudFillAxis.Horizontal;
    public bool Reverse;
    public HudAlignment Align;

    public float Percent => _percent;

    public void PushBar(HudSpriteInfo background, HudSpriteInfo fill, Float2 barSize, Float2 fillSize,
        Float2 fillOffset, HudFillAxis axis, bool reverse, HudAlignment align)
    {
        _background = background;
        _fillSprite = fill;
        _fillSize = fillSize;
        Axis = axis;
        Reverse = reverse;
        Align = align;
        _fillOffset = CorrectFillOffset(fillOffset, barSize, fillSize, align);
        _percent = 1f;

        Size = barSize;
        WriteBackground();
        WriteFill();
        IsValid = true;
    }

    public void PushValue(float percent)
    {
        _percent = float.Clamp(percent, 0f, 1f);
        WriteFill();
    }

    private void WriteBackground()
    {
        if (_background is HudSpriteInfo bg)
            HudGeometry.WriteSlicedFill(_bg, bg, 1f, Size, Reverse, Axis, Align);
    }

    private void WriteFill()
    {
        if (_fillSprite is HudSpriteInfo fill)
        {
            HudGeometry.WriteSlicedFill(_fill, fill, _percent, _fillSize, Reverse, Axis, Align);
            // The fill sits inside the bar at the config offset (UnityHUD passes bd_offset to the parser).
            for (int i = 0; i < _fill.Length; i++)
            {
                ref HudQuad q = ref _fill[i];
                q.OffsetBL = new Float2(q.OffsetBL.X + _fillOffset.X, q.OffsetBL.Y + _fillOffset.Y);
                q.OffsetBR = new Float2(q.OffsetBR.X + _fillOffset.X, q.OffsetBR.Y + _fillOffset.Y);
                q.OffsetTR = new Float2(q.OffsetTR.X + _fillOffset.X, q.OffsetTR.Y + _fillOffset.Y);
                q.OffsetTL = new Float2(q.OffsetTL.X + _fillOffset.X, q.OffsetTL.Y + _fillOffset.Y);
            }
        }
    }

    /// <summary>Moves the fill rect inside the bar for non-left alignment (UnityHUD's bd_offset correction).</summary>
    internal static Float2 CorrectFillOffset(Float2 offset, Float2 bar, Float2 fill, HudAlignment align)
        => align switch
        {
            HudAlignment.Middle => new Float2(offset.X - (bar.X - fill.X) * 0.5f, offset.Y),
            HudAlignment.Right => new Float2(offset.X - (bar.X - fill.X), offset.Y),
            _ => offset,
        };

    public override void Fill(List<HudMeshData?> targets, in HudFillContext context)
    {
        FillGroup(targets, _background, _bg, context);
        FillGroup(targets, _fillSprite, _fill, context);
    }

    private void FillGroup(List<HudMeshData?> targets, HudSpriteInfo? sprite, HudQuad[] quads, in HudFillContext context)
    {
        if (sprite is null || sprite.MaterialSlot < 0 || sprite.MaterialSlot >= targets.Count)
            return;
        HudMeshData? target = targets[sprite.MaterialSlot];
        if (target is null)
            return;
        for (int i = 0; i < quads.Length; i++)
            target.AppendQuad(Anchor, context.RightStep, context.UpStep, quads[i], Offset.X, Offset.Y, Scale);
    }

    public override void Release()
    {
        base.Release();
        _background = null;
        _fillSprite = null;
        _percent = 1f;
    }
}
