using System;

using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>
/// One camera-facing quad in pixel space. Corners are ordered BL, BR, TR, TL with +Y up;
/// offsets are relative to the widget's anchor and are expanded against the camera basis
/// at fill time (UnityHUD expands in the vertex shader instead — same numbers, CPU side).
/// </summary>
public struct HudQuad
{
    public Float2 OffsetBL;
    public Float2 OffsetBR;
    public Float2 OffsetTR;
    public Float2 OffsetTL;

    public Float2 UvBL;
    public Float2 UvBR;
    public Float2 UvTR;
    public Float2 UvTL;

    public Color32 ColorBL;
    public Color32 ColorBR;
    public Color32 ColorTR;
    public Color32 ColorTL;
}

/// <summary>
/// Pure quad-writers ported from UnityHUD's HUDStringParser. Pixel space is Y-up; UV v = 0
/// is the texture's bottom row (XEngine flips image rows on load, matching SpriteGeometry).
/// UnityHUD swaps UV corners to compensate its Cull Front shader; these writers assign UVs
/// naturally because the Hudmesh shaders use Cull Off.
/// </summary>
public static class HudGeometry
{
    private static readonly Color32 White = new(255, 255, 255, 255);

    /// <summary>Writes a single quad covering <paramref name="sprite"/>'s native pixel size. Returns the size.</summary>
    public static Float2 WriteSprite(Span<HudQuad> quads, HudSpriteInfo sprite, HudAlignment align)
    {
        float width = sprite.Width;
        float height = sprite.Height;
        (float left, float right) = AlignBounds(align, width);

        ref HudQuad q = ref quads[0];
        q.OffsetBL = new Float2(left, 0f);
        q.OffsetBR = new Float2(right, 0f);
        q.OffsetTR = new Float2(right, height);
        q.OffsetTL = new Float2(left, height);

        q.UvBL = sprite.UvMin;
        q.UvBR = new Float2(sprite.UvMax.X, sprite.UvMin.Y);
        q.UvTR = sprite.UvMax;
        q.UvTL = new Float2(sprite.UvMin.X, sprite.UvMax.Y);
        FillWhite(ref q);
        return new Float2(width, height);
    }

    /// <summary>
    /// Writes a nine-slice quad set stretching <paramref name="sprite"/> to
    /// <paramref name="size"/>. Corners stay unstretched; only the center stretches.
    /// Requires at least 9 quads. Returns the size.
    /// </summary>
    public static Float2 WriteSlicedSprite(Span<HudQuad> quads, HudSpriteInfo sprite, Float2 size, HudAlignment align)
    {
        float uPerPixel = (sprite.UvMax.X - sprite.UvMin.X) / sprite.Width;
        float vPerPixel = (sprite.UvMax.Y - sprite.UvMin.Y) / sprite.Height;
        float borderL = sprite.Border.X;
        float borderT = sprite.Border.Y;
        float borderR = sprite.Border.Z;
        float borderB = sprite.Border.W;

        float u1 = sprite.UvMin.X + uPerPixel * borderL;
        float u2 = sprite.UvMin.X + uPerPixel * (sprite.Width - borderR);
        float v1 = sprite.UvMin.Y + vPerPixel * borderB;
        float v2 = sprite.UvMin.Y + vPerPixel * (sprite.Height - borderT);

        float x0 = AlignOffset(align, size.X);
        float centerW = size.X - borderL - borderR;
        float centerH = size.Y - borderB - borderT;
        float y0 = 0f;
        float y1 = borderB;
        float y2 = size.Y - borderT;

        EmitSlice(ref quads[0], x0, y0, borderL, borderB, sprite.UvMin.X, u1, sprite.UvMin.Y, v1);    // bottom-left
        EmitSlice(ref quads[1], x0 + borderL, y0, centerW, borderB, u1, u2, sprite.UvMin.Y, v1);      // bottom-center
        EmitSlice(ref quads[2], x0 + size.X - borderR, y0, borderR, borderB, u2, sprite.UvMax.X, sprite.UvMin.Y, v1); // bottom-right

        EmitSlice(ref quads[3], x0, y1, borderL, centerH, sprite.UvMin.X, u1, v1, v2);                // middle-left
        EmitSlice(ref quads[4], x0 + borderL, y1, centerW, centerH, u1, u2, v1, v2);                  // center
        EmitSlice(ref quads[5], x0 + size.X - borderR, y1, borderR, centerH, u2, sprite.UvMax.X, v1, v2); // middle-right

        EmitSlice(ref quads[6], x0, y2, borderL, borderT, sprite.UvMin.X, u1, v2, sprite.UvMax.Y);    // top-left
        EmitSlice(ref quads[7], x0 + borderL, y2, centerW, borderT, u1, u2, v2, sprite.UvMax.Y);      // top-center
        EmitSlice(ref quads[8], x0 + size.X - borderR, y2, borderR, borderT, u2, sprite.UvMax.X, v2, sprite.UvMax.Y); // top-right

        return size;
    }

    /// <summary>
    /// Writes the three horizontal or vertical fill segments of a sliced fill bar
    /// (port of UnityHUD's PasreSlicedFillSprite). Requires at least 3 quads. Returns the size.
    /// </summary>
    public static Float2 WriteSlicedFill(Span<HudQuad> quads, HudSpriteInfo sprite, float fillAmount, Float2 size,
        bool reverse, HudFillAxis axis, HudAlignment align)
    {
        fillAmount = float.Clamp(fillAmount, 0f, 1f);
        float uPerPixel = (sprite.UvMax.X - sprite.UvMin.X) / sprite.Width;
        float vPerPixel = (sprite.UvMax.Y - sprite.UvMin.Y) / sprite.Height;
        float borderL = sprite.Border.X;
        float borderT = sprite.Border.Y;
        float borderR = sprite.Border.Z;
        float borderB = sprite.Border.W;

        float alignX = AlignOffset(align, size.X);
        float filledW = size.X * fillAmount;
        float filledH = size.Y * fillAmount;

        if (axis == HudFillAxis.Horizontal)
        {
            float uv1 = sprite.UvMin.X + uPerPixel * borderL;
            float uv2 = sprite.UvMax.X - uPerPixel * borderR;
            float len0, len1, len2, segX0, segX1, segX2;
            if (!reverse)
            {
                ComputeSegments(filledW, borderL, borderR, out len0, out len1, out len2);
                segX0 = alignX;
                segX1 = alignX + len0;
                segX2 = alignX + len0 + len1;
            }
            else
            {
                ComputeSegmentsReverse(filledW, borderL, borderR, out len0, out len1, out len2);
                segX0 = alignX + size.X - filledW;
                segX1 = segX0 + len0;
                segX2 = alignX + size.X - len2;
            }
            EmitSlice(ref quads[0], segX0, 0f, len0, size.Y, sprite.UvMin.X, uv1, sprite.UvMin.Y, sprite.UvMax.Y);
            EmitSlice(ref quads[1], segX1, 0f, len1, size.Y, uv1, uv2, sprite.UvMin.Y, sprite.UvMax.Y);
            EmitSlice(ref quads[2], segX2, 0f, len2, size.Y, uv2, sprite.UvMax.X, sprite.UvMin.Y, sprite.UvMax.Y);
        }
        else
        {
            float uv1 = sprite.UvMin.Y + vPerPixel * borderB;
            float uv2 = sprite.UvMax.Y - vPerPixel * borderT;
            float len0, len1, len2;
            if (!reverse)
            {
                ComputeSegments(filledH, borderB, borderT, out len0, out len1, out len2);
                EmitSlice(ref quads[0], alignX, 0f, size.X, len0, sprite.UvMin.X, sprite.UvMax.X, sprite.UvMin.Y, uv1);
                EmitSlice(ref quads[1], alignX, len0, size.X, len1, sprite.UvMin.X, sprite.UvMax.X, uv1, uv2);
                EmitSlice(ref quads[2], alignX, filledH - len2, size.X, len2, sprite.UvMin.X, sprite.UvMax.X, uv2, sprite.UvMax.Y);
            }
            else
            {
                ComputeSegmentsReverse(filledH, borderB, borderT, out len0, out len1, out len2);
                float y0 = size.Y - filledH;
                EmitSlice(ref quads[0], alignX, y0, size.X, len0, sprite.UvMin.X, sprite.UvMax.X, sprite.UvMin.Y, uv1);
                EmitSlice(ref quads[1], alignX, y0 + len0, size.X, len1, sprite.UvMin.X, sprite.UvMax.X, uv1, uv2);
                EmitSlice(ref quads[2], alignX, size.Y - len2, size.X, len2, sprite.UvMin.X, sprite.UvMax.X, uv2, sprite.UvMax.Y);
            }
        }

        return size;
    }

    /// <summary>Forward fill: left border, then center, then right border (truncated as the fill runs out).</summary>
    private static void ComputeSegments(float filled, float borderA, float borderB, out float len0, out float len1, out float len2)
    {
        len0 = 0f; len1 = 0f; len2 = 0f;
        if (filled <= 0f) return;
        len0 = borderA;
        if (filled - borderA - borderB > 0f)
        {
            len1 = filled - borderA - borderB;
            len2 = borderB;
        }
    }

    /// <summary>Reverse fill: right border, then center, then left border (mirrors ComputeSegments).</summary>
    private static void ComputeSegmentsReverse(float filled, float borderA, float borderB, out float len0, out float len1, out float len2)
    {
        len0 = 0f; len1 = 0f; len2 = 0f;
        if (filled <= 0f) return;
        len2 = borderB;
        if (filled - borderB - borderA > 0f)
        {
            len1 = filled - borderB - borderA;
            len0 = borderA;
        }
    }

    private static (float left, float right) AlignBounds(HudAlignment align, float width)
    {
        return align switch
        {
            HudAlignment.Middle => (-width * 0.5f, width * 0.5f),
            HudAlignment.Right => (-width, 0f),
            _ => (0f, width),
        };
    }

    private static float AlignOffset(HudAlignment align, float width)
        => align switch
        {
            HudAlignment.Middle => -width * 0.5f,
            HudAlignment.Right => -width,
            _ => 0f,
        };

    private static void EmitSlice(ref HudQuad q, float x, float y, float w, float h, float u0, float u1, float v0, float v1)
    {
        q.OffsetBL = new Float2(x, y);
        q.OffsetBR = new Float2(x + w, y);
        q.OffsetTR = new Float2(x + w, y + h);
        q.OffsetTL = new Float2(x, y + h);
        q.UvBL = new Float2(u0, v0);
        q.UvBR = new Float2(u1, v0);
        q.UvTR = new Float2(u1, v1);
        q.UvTL = new Float2(u0, v1);
        FillWhite(ref q);
    }

    private static void FillWhite(ref HudQuad q)
    {
        q.ColorBL = White;
        q.ColorBR = White;
        q.ColorTR = White;
        q.ColorTL = White;
    }
}
