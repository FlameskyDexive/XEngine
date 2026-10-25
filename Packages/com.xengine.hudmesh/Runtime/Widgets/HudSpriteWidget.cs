using System;
using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>Static slot widget drawing one sprite, optionally nine-sliced (UnityHUD HUDSpriteMesh port).</summary>
internal sealed class HudSpriteWidget : HudWidget
{
    private HudQuad[] _quads = new HudQuad[9];
    private int _quadCount;

    public HudSpriteInfo? Sprite { get; private set; }

    public void PushSprite(HudSpriteInfo sprite, HudAlignment align)
    {
        Sprite = sprite;
        _quadCount = 1;
        MaterialSlot = sprite.MaterialSlot;
        Size = HudGeometry.WriteSprite(_quads.AsSpan(0, 1), sprite, align);
        IsValid = true;
    }

    public void PushSlicedSprite(HudSpriteInfo sprite, Float2 size, HudAlignment align)
    {
        Sprite = sprite;
        _quadCount = 9;
        MaterialSlot = sprite.MaterialSlot;
        Size = HudGeometry.WriteSlicedSprite(_quads, sprite, size, align);
        IsValid = true;
    }

    /// <summary>Rewrites only the fill segments of a previously pushed sliced sprite (bar percent updates).</summary>
    public void UpdateSlicedFill(float fillAmount, bool reverse, HudFillAxis axis, HudAlignment align)
    {
        if (Sprite is null || _quadCount != 9)
            return;
        HudGeometry.WriteSlicedFill(_quads.AsSpan(0, 3), Sprite, fillAmount, Size, reverse, axis, align);
    }

    public override void Fill(List<HudMeshData?> targets, in HudFillContext context)
    {
        if (Sprite is null || MaterialSlot < 0 || MaterialSlot >= targets.Count)
            return;
        HudMeshData? target = targets[MaterialSlot];
        if (target is null)
            return;
        for (int i = 0; i < _quadCount; i++)
            target.AppendQuad(Anchor, context.RightStep, context.UpStep, _quads[i], Offset.X, Offset.Y, Scale);
    }

    public override void Release()
    {
        base.Release();
        Sprite = null;
        _quadCount = 0;
    }
}
