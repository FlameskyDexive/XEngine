using System.Collections.Generic;

using Xunit;

using XEngine.Vector;

namespace XEngine.Hudmesh.Tests;

/// <summary>
/// Verifies the world-space expansion math of the widget fill path by filling into a real
/// <see cref="HudMeshData"/> and reading the written vertices (no GPU involved — no Seal),
/// plus the distance-sort comparator contract and pooled handle lifecycle.
/// </summary>
public sealed class HudCoreTests
{
    private static HudSpriteInfo MakeSprite(float width = 100f, float height = 20f)
        => new() { Name = "s", Width = width, Height = height, MaterialSlot = 0 };

    [Fact]
    public void SpriteWidgetFill_ExpandsPixelsAgainstCameraBasis()
    {
        HudMeshData target = new();
        List<HudMeshData?> targets = [target];

        HudSpriteWidget widget = new();
        widget.PushSprite(MakeSprite(), HudAlignment.Middle);
        widget.RoleOffset = new Float3(0f, 1f, 0f);
        widget.Anchor = widget.RoleOffset; // role at the origin
        widget.Offset = new Float2(0f, 42f);

        HudFillContext context = new()
        {
            RightStep = new Float3(0.01f, 0f, 0f),
            UpStep = new Float3(0f, 0.01f, 0f),
        };
        widget.Fill(targets, context);

        Assert.Equal(4, target.VertexCount);
        // Middle-aligned 100px-wide quad: BL at -50px, BR at +50px; unit = 0.01 world/px.
        Float3 bl = target.VertexAt(0);
        Assert.Equal(-0.5f, bl.X, 5);
        Assert.Equal(1f + 0.01f * 42f, bl.Y, 5);
        // BR: BL + right*100 px.
        Float3 br = target.VertexAt(1);
        Assert.Equal(0.5f, br.X, 5);
        Assert.Equal(bl.Y, br.Y, 5);
        // TR: BR + up*20 px.
        Float3 tr = target.VertexAt(2);
        Assert.Equal(br.X, tr.X, 5);
        Assert.Equal(1f + 0.01f * 62f, tr.Y, 5);
        // TL: back to X of BL.
        Float3 tl = target.VertexAt(3);
        Assert.Equal(bl.X, tl.X, 5);
        Assert.Equal(tr.Y, tl.Y, 5);
    }

    [Fact]
    public void SpriteWidgetFill_ScaleAndPixelOffsetMultiplyThrough()
    {
        HudMeshData target = new();
        List<HudMeshData?> targets = [target];

        HudSpriteWidget widget = new();
        widget.PushSprite(MakeSprite(width: 10f, height: 10f), HudAlignment.Left);
        widget.Anchor = Float3.Zero;
        widget.Offset = new Float2(5f, 7f);
        widget.Scale = 2f;

        widget.Fill(targets, new HudFillContext
        {
            RightStep = new Float3(1f, 0f, 0f),
            UpStep = new Float3(0f, 1f, 0f),
        });

        // BR = right*(5 + 10*2) px.
        Float3 br = target.VertexAt(1);
        Assert.Equal(25f, br.X, 5);
        Assert.Equal(7f, br.Y, 5);
    }

    [Fact]
    public void SpriteWidgetFill_SkipsUnknownMaterialSlot()
    {
        HudMeshData target = new();
        List<HudMeshData?> targets = [target];

        HudSpriteWidget widget = new();
        HudSpriteInfo unassigned = MakeSprite();
        unassigned.MaterialSlot = -1; // never resolved by a manager
        widget.PushSprite(unassigned, HudAlignment.Middle);
        widget.Anchor = Float3.Zero;
        widget.Fill(targets, new HudFillContext());

        Assert.Equal(0, target.VertexCount);
    }

    [Fact]
    public void HudLifecycle_SetIconHideShowRelease()
    {
        HudObject obj = new();
        obj.InitHeadless(Float2.Zero);

        obj.SetSprite(HudSlot.GuildIcon, MakeSprite(width: 30f, height: 30f), HudAlignment.Middle);
        Assert.True(obj.HasVisibleContent);
        Assert.True(obj.IsSlotVisible(HudSlot.GuildIcon));

        obj.SetSlotVisible(HudSlot.GuildIcon, false);
        Assert.False(obj.IsSlotVisible(HudSlot.GuildIcon));

        obj.SetSlotVisible(HudSlot.GuildIcon, true);
        Assert.True(obj.IsSlotVisible(HudSlot.GuildIcon));

        obj.SetSprite(HudSlot.GuildIcon, null, HudAlignment.Middle);
        Assert.False(obj.IsSlotVisible(HudSlot.GuildIcon));

        obj.Release();
    }

    [Fact]
    public void SortKey_FarHudSortsFirst()
    {
        HudObject near = new();
        near.InitHeadless(Float2.Zero);
        near.SortKey = 1.0;

        HudObject far = new();
        far.InitHeadless(Float2.Zero);
        far.SortKey = 100.0;

        // Same comparison the manager uses: descending SortKey (far → near).
        List<HudObject> sorted = [near, far];
        sorted.Sort(static (a, b) => b.SortKey.CompareTo(a.SortKey));

        Assert.Same(far, sorted[0]);
        Assert.Same(near, sorted[1]);
    }
}
