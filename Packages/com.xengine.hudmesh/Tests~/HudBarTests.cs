using System.Collections.Generic;

using Xunit;

using XEngine.Vector;

namespace XEngine.Hudmesh.Tests;

public sealed class HudBarTests
{
    private static HudSpriteInfo MakeSprite(float width, float height, float borderX)
        => new()
        {
            Name = "bar",
            Width = width,
            Height = height,
            MaterialSlot = 0,
            Border = new Float4(borderX, 0f, borderX, 0f), // L, T, R, B — bars slice horizontally
        };

    [Fact]
    public void CorrectFillOffset_CentersFillInsideBar()
    {
        Float2 bar = new(180f, 30f);
        Float2 fill = new(160f, 13f);
        Float2 offset = HudBarWidget.CorrectFillOffset(new Float2(10f, 10f), bar, fill, HudAlignment.Middle);
        Assert.Equal(0f, offset.X, 5); // 10 - (180-160)/2
        Assert.Equal(10f, offset.Y, 5);
    }

    [Fact]
    public void PushBar_FillsBackgroundFullAndFillByPercent()
    {
        HudMeshData target = new();
        List<HudMeshData?> targets = [target];

        HudBarWidget bar = new();
        bar.PushBar(
            MakeSprite(64f, 32f, 12f),   // background source art
            MakeSprite(64f, 16f, 12f),   // fill source art
            new Float2(180f, 30f), new Float2(160f, 13f), new Float2(10f, 10f),
            HudFillAxis.Horizontal, reverse: false, HudAlignment.Middle);
        bar.Anchor = Float3.Zero;

        bar.Fill(targets, new HudFillContext
        {
            RightStep = new Float3(1f, 0f, 0f),
            UpStep = new Float3(0f, 1f, 0f),
        });

        // bg 3 quads + fill 3 quads = 6 quads = 24 vertices.
        Assert.Equal(24, target.VertexCount);
        // Full background spans the centered 180px bar: -90..+90 (quad 2 BR is the right edge).
        Assert.Equal(90f, target.VertexAt(9).X, 5);

        // Half fill: fill right edge ends at bar center (fill spans -80..+80, half = -80..0).
        bar.PushValue(0.5f);
        target.BeginFrame();
        bar.Fill(targets, new HudFillContext
        {
            RightStep = new Float3(1f, 0f, 0f),
            UpStep = new Float3(0f, 1f, 0f),
        });
        Assert.Equal(24, target.VertexCount);
        Assert.Equal(0f, target.VertexAt(21).X, 5); // fill quad 2 BR right edge lands on the bar center
    }

    [Fact]
    public void PushValue_Clamps()
    {
        HudBarWidget bar = new();
        bar.PushBar(
            MakeSprite(64f, 32f, 12f), MakeSprite(64f, 16f, 12f),
            new Float2(180f, 30f), new Float2(160f, 13f), new Float2(10f, 10f),
            HudFillAxis.Horizontal, reverse: false, HudAlignment.Middle);
        bar.PushValue(2f);
        Assert.Equal(1f, bar.Percent, 5);
        bar.PushValue(-1f);
        Assert.Equal(0f, bar.Percent, 5);
    }
}
