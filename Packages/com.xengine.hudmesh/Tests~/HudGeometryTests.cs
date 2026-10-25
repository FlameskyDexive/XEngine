using Xunit;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Hudmesh.Tests;

public sealed class HudGeometryTests
{
    private static HudSpriteInfo MakeSprite(float width = 100f, float height = 50f, Float4? border = null)
        => new()
        {
            Name = "test",
            Width = width,
            Height = height,
            UvMin = new Float2(0.1f, 0.2f),
            UvMax = new Float2(0.6f, 0.7f),
            Border = border ?? Float4.Zero,
        };

    [Fact]
    public void WriteSprite_FullRectQuadWithNaturalUv()
    {
        Span<HudQuad> quads = stackalloc HudQuad[1];
        Float2 size = HudGeometry.WriteSprite(quads, MakeSprite(), HudAlignment.Middle);

        Assert.Equal(100f, size.X);
        Assert.Equal(50f, size.Y);

        ref HudQuad q = ref quads[0];
        Assert.Equal(-50f, q.OffsetBL.X);
        Assert.Equal(0f, q.OffsetBL.Y);
        Assert.Equal(50f, q.OffsetBR.X);
        Assert.Equal(0f, q.OffsetBR.Y);
        Assert.Equal(50f, q.OffsetTR.X);
        Assert.Equal(50f, q.OffsetTR.Y);
        Assert.Equal(-50f, q.OffsetTL.X);
        Assert.Equal(50f, q.OffsetTL.Y);

        // v = 0 at the bottom row: BL gets vMin, TL gets vMax.
        Assert.Equal(0.1f, q.UvBL.X);
        Assert.Equal(0.2f, q.UvBL.Y);
        Assert.Equal(0.6f, q.UvTR.X);
        Assert.Equal(0.7f, q.UvTR.Y);
        Assert.Equal(0.6f, q.UvBR.X);
        Assert.Equal(0.2f, q.UvBR.Y);
        Assert.Equal(0.1f, q.UvTL.X);
        Assert.Equal(0.7f, q.UvTL.Y);
    }

    [Fact]
    public void WriteSprite_AlignmentShiftsPixels()
    {
        Span<HudQuad> left = stackalloc HudQuad[1];
        HudGeometry.WriteSprite(left, MakeSprite(), HudAlignment.Left);
        Assert.Equal(0f, left[0].OffsetBL.X);

        Span<HudQuad> right = stackalloc HudQuad[1];
        HudGeometry.WriteSprite(right, MakeSprite(), HudAlignment.Right);
        Assert.Equal(-100f, right[0].OffsetBL.X);
        Assert.Equal(0f, right[0].OffsetBR.X);
    }

    [Fact]
    public void WriteSlicedSprite_CornersUnstretchedCenterStretched()
    {
        HudSpriteInfo sprite = MakeSprite(width: 90f, height: 60f, border: new Float4(10f, 20f, 10f, 15f)); // L,T,R,B
        Span<HudQuad> quads = new HudQuad[9];
        Float2 size = HudGeometry.WriteSlicedSprite(quads, sprite, new Float2(200f, 100f), HudAlignment.Left);

        Assert.Equal(200f, size.X);
        Assert.Equal(100f, size.Y);

        // Bottom-left slice keeps the native border size at the rect origin.
        Assert.Equal(0f, quads[0].OffsetBL.X);
        Assert.Equal(0f, quads[0].OffsetBL.Y);
        Assert.Equal(10f, quads[0].OffsetBR.X);
        Assert.Equal(15f, quads[0].OffsetTR.Y);

        // Center stretches: spans between the borders.
        Assert.Equal(10f, quads[4].OffsetBL.X);
        Assert.Equal(190f, quads[4].OffsetBR.X);
        Assert.Equal(15f, quads[4].OffsetBL.Y);
        Assert.Equal(80f, quads[4].OffsetTR.Y); // 100 - top border 20

        // Center UVs span between the border UVs.
        float u1 = 0.1f + (0.5f / 90f) * 10f;
        float u2 = 0.1f + (0.5f / 90f) * 80f; // 90 - right border 10
        float v1 = 0.2f + (0.5f / 60f) * 15f;
        float v2 = 0.2f + (0.5f / 60f) * 40f; // 60 - top border 20
        Assert.Equal(u1, quads[4].UvBL.X, 5);
        Assert.Equal(u2, quads[4].UvBR.X, 5);
        Assert.Equal(v1, quads[4].UvBL.Y, 5);
        Assert.Equal(v2, quads[4].UvTR.Y, 5);

        // Top-right slice touches the rect corner.
        Assert.Equal(200f, quads[8].OffsetBR.X);
        Assert.Equal(100f, quads[8].OffsetTR.Y);
        Assert.Equal(0.6f, quads[8].UvTR.X, 5);
        Assert.Equal(0.7f, quads[8].UvTR.Y, 5);
    }

    [Fact]
    public void WriteSlicedFill_ForwardHorizontalTruncatesFromRight()
    {
        HudSpriteInfo sprite = MakeSprite(width: 100f, height: 20f, border: new Float4(10f, 10f, 10f, 10f));
        Span<HudQuad> quads = new HudQuad[3];
        HudGeometry.WriteSlicedFill(quads, sprite, 0.5f, new Float2(100f, 20f), reverse: false,
            HudFillAxis.Horizontal, HudAlignment.Left);

        // Filled width 50: left border full (10), center 30, right border 10.
        Assert.Equal(0f, quads[0].OffsetBL.X);
        Assert.Equal(10f, quads[0].OffsetBR.X);
        Assert.Equal(10f, quads[1].OffsetBL.X);
        Assert.Equal(40f, quads[1].OffsetBR.X);
        Assert.Equal(40f, quads[2].OffsetBL.X);
        Assert.Equal(50f, quads[2].OffsetBR.X);
        // Nothing beyond the fill edge.
        Assert.Equal(0f, quads[2].OffsetBR.X - quads[2].OffsetBL.X - 10f);
    }

    [Fact]
    public void WriteSlicedFill_ReverseHorizontalAnchorsToRightEdge()
    {
        HudSpriteInfo sprite = MakeSprite(width: 100f, height: 20f, border: new Float4(10f, 10f, 10f, 10f));
        Span<HudQuad> quads = new HudQuad[3];
        HudGeometry.WriteSlicedFill(quads, sprite, 0.5f, new Float2(100f, 20f), reverse: true,
            HudFillAxis.Horizontal, HudAlignment.Left);

        // Display region is [50, 100]: left border at 50, center to 90, right border to 100.
        Assert.Equal(50f, quads[0].OffsetBL.X);
        Assert.Equal(60f, quads[0].OffsetBR.X);
        Assert.Equal(60f, quads[1].OffsetBL.X);
        Assert.Equal(90f, quads[1].OffsetBR.X);
        Assert.Equal(90f, quads[2].OffsetBL.X);
        Assert.Equal(100f, quads[2].OffsetBR.X);
    }

    [Fact]
    public void WriteSlicedFill_FullAmountCoversWholeRect()
    {
        HudSpriteInfo sprite = MakeSprite(width: 100f, height: 20f, border: new Float4(10f, 10f, 10f, 10f));
        Span<HudQuad> quads = new HudQuad[3];
        HudGeometry.WriteSlicedFill(quads, sprite, 1f, new Float2(100f, 20f), reverse: false,
            HudFillAxis.Horizontal, HudAlignment.Left);

        Assert.Equal(0f, quads[0].OffsetBL.X);
        Assert.Equal(100f, quads[2].OffsetBR.X);
    }

    [Fact]
    public void WriteSlicedFill_VerticalFillsBottomUp()
    {
        HudSpriteInfo sprite = MakeSprite(width: 100f, height: 20f, border: new Float4(10f, 10f, 10f, 10f));
        Span<HudQuad> quads = new HudQuad[3];
        HudGeometry.WriteSlicedFill(quads, sprite, 0.5f, new Float2(100f, 20f), reverse: false,
            HudFillAxis.Vertical, HudAlignment.Left);

        // filledH = 10 → only the bottom border fits (UnityHUD truncates borders before centers).
        Assert.Equal(0f, quads[0].OffsetBL.Y);
        Assert.Equal(10f, quads[0].OffsetTR.Y);
        Assert.Equal(0f, quads[1].OffsetTR.Y - quads[1].OffsetBL.Y);

        // With smaller borders the center segment appears: border 5, fill 0.75 of 20 → 5 + 5.
        HudSpriteInfo thin = MakeSprite(width: 100f, height: 20f, border: new Float4(5f, 5f, 5f, 5f));
        HudGeometry.WriteSlicedFill(quads, thin, 0.75f, new Float2(100f, 20f), reverse: false,
            HudFillAxis.Vertical, HudAlignment.Left);
        Assert.Equal(0f, quads[0].OffsetBL.Y);
        Assert.Equal(5f, quads[0].OffsetTR.Y);
        Assert.Equal(5f, quads[1].OffsetBL.Y);
        Assert.Equal(10f, quads[1].OffsetTR.Y);
    }
}
