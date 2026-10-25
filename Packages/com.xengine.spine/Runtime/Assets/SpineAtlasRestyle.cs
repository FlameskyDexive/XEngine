using System;
using Spine;

namespace XEngine.Spine;

/// <summary>
/// CPU unpack / pack for atlas pages. Buffers are tightly packed RGBA, y-from-top
/// (same as the <c>.atlas</c> file). Set <paramref name="flipY"/> when the buffer
/// came from a GL texture (bottom-up).
/// </summary>
public static class SpineAtlasRestyle
{
    public static byte[] ExtractPacked(byte[] pageRgba, int pageW, int pageH, AtlasRegion region, bool flipY = false)
    {
        SpineAtlasLayout.GetPackedRect(region, out int x, out int y, out int width, out int height);
        ValidatePage(pageRgba, pageW, pageH);
        ValidateRect(pageW, pageH, x, y, width, height);
        byte[] dest = new byte[width * height * 4];
        CopyRect(pageRgba, pageW, pageH, x, y, width, height, dest, width, 0, 0, flipY, toPage: false);
        return dest;
    }

    public static void BlitPacked(byte[] pageRgba, int pageW, int pageH, AtlasRegion region, byte[] packedRgba, bool flipY = false)
    {
        SpineAtlasLayout.GetPackedRect(region, out int x, out int y, out int width, out int height);
        ValidatePage(pageRgba, pageW, pageH);
        ValidateRect(pageW, pageH, x, y, width, height);
        if (packedRgba == null || packedRgba.Length < width * height * 4)
            throw new ArgumentException("Packed buffer is smaller than the region.", nameof(packedRgba));
        CopyRect(packedRgba, width, height, 0, 0, width, height, pageRgba, pageW, x, y, flipY, toPage: true);
    }

    /// <summary>Unrotated crop for img2img. 90° packed regions are rotated back.</summary>
    public static byte[] ExtractUnrotated(byte[] pageRgba, int pageW, int pageH, AtlasRegion region, bool flipY = false)
    {
        byte[] packed = ExtractPacked(pageRgba, pageW, pageH, region, flipY);
        return region.degrees == 90
            ? Rotate90Ccw(packed, region.width, region.height)
            : packed;
    }

    /// <summary>Write an unrotated crop back into the packed rect (re-applies 90° if needed).</summary>
    public static void BlitUnrotated(byte[] pageRgba, int pageW, int pageH, AtlasRegion region, byte[] unrotatedRgba, bool flipY = false)
    {
        byte[] packed = region.degrees == 90
            ? Rotate90Cw(unrotatedRgba, region.height, region.width)
            : unrotatedRgba;
        BlitPacked(pageRgba, pageW, pageH, region, packed, flipY);
    }

    public static void TintPacked(byte[] pageRgba, int pageW, int pageH, AtlasRegion region, byte r, byte g, byte b, bool flipY = false)
    {
        byte[] packed = ExtractPacked(pageRgba, pageW, pageH, region, flipY);
        Tint(packed, r, g, b);
        BlitPacked(pageRgba, pageW, pageH, region, packed, flipY);
    }

    public static void TintAllRegions(byte[] pageRgba, int pageW, int pageH, Atlas atlas, byte r, byte g, byte b, bool flipY = false)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        for (int i = 0; i < atlas.Regions.Count; i++)
            TintPacked(pageRgba, pageW, pageH, atlas.Regions[i], r, g, b, flipY);
    }

    public static void Tint(byte[] rgba, byte r, byte g, byte b)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            rgba[i] = (byte)(rgba[i] * r / 255);
            rgba[i + 1] = (byte)(rgba[i + 1] * g / 255);
            rgba[i + 2] = (byte)(rgba[i + 2] * b / 255);
        }
    }

    public static void Premultiply(byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            byte a = rgba[i + 3];
            rgba[i] = (byte)(rgba[i] * a / 255);
            rgba[i + 1] = (byte)(rgba[i + 1] * a / 255);
            rgba[i + 2] = (byte)(rgba[i + 2] * a / 255);
        }
    }

    public static void Unpremultiply(byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            byte a = rgba[i + 3];
            if (a == 0)
            {
                rgba[i] = 0;
                rgba[i + 1] = 0;
                rgba[i + 2] = 0;
                continue;
            }

            rgba[i] = Clamp255(rgba[i] * 255 / a);
            rgba[i + 1] = Clamp255(rgba[i + 1] * 255 / a);
            rgba[i + 2] = Clamp255(rgba[i + 2] * 255 / a);
        }
    }

    /// <summary>CW 90°: src W×H → dest H×W.</summary>
    public static byte[] Rotate90Cw(byte[] src, int width, int height)
    {
        ValidateBuffer(src, width, height);
        byte[] dest = new byte[width * height * 4];
        int destW = height;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int dx = height - 1 - y;
                int dy = x;
                CopyPixel(src, (y * width + x) * 4, dest, (dy * destW + dx) * 4);
            }
        }

        return dest;
    }

    /// <summary>CCW 90°: src W×H → dest H×W.</summary>
    public static byte[] Rotate90Ccw(byte[] src, int width, int height)
    {
        ValidateBuffer(src, width, height);
        byte[] dest = new byte[width * height * 4];
        int destW = height;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int dx = y;
                int dy = width - 1 - x;
                CopyPixel(src, (y * width + x) * 4, dest, (dy * destW + dx) * 4);
            }
        }

        return dest;
    }

    private static void CopyRect(
        byte[] src,
        int srcW,
        int srcH,
        int srcX,
        int srcY,
        int width,
        int height,
        byte[] dest,
        int destW,
        int destX,
        int destY,
        bool flipY,
        bool toPage)
    {
        for (int ly = 0; ly < height; ly++)
        {
            int fromY = srcY + ly;
            int toY = destY + ly;
            if (flipY)
            {
                if (toPage)
                    toY = dest.Length / (destW * 4) - 1 - (destY + ly);
                else
                    fromY = srcH - 1 - (srcY + ly);
            }

            Buffer.BlockCopy(src, (fromY * srcW + srcX) * 4, dest, (toY * destW + destX) * 4, width * 4);
        }
    }

    private static void CopyPixel(byte[] src, int srcIndex, byte[] dest, int destIndex)
    {
        dest[destIndex] = src[srcIndex];
        dest[destIndex + 1] = src[srcIndex + 1];
        dest[destIndex + 2] = src[srcIndex + 2];
        dest[destIndex + 3] = src[srcIndex + 3];
    }

    private static void ValidatePage(byte[] pageRgba, int pageW, int pageH)
    {
        ArgumentNullException.ThrowIfNull(pageRgba);
        if (pageW <= 0 || pageH <= 0 || pageRgba.Length < pageW * pageH * 4)
            throw new ArgumentException("Page buffer does not match width/height.");
    }

    private static void ValidateBuffer(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("Buffer does not match width/height.");
    }

    private static void ValidateRect(int pageW, int pageH, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > pageW || y + height > pageH)
            throw new ArgumentOutOfRangeException(nameof(x), "Region is outside the page.");
    }

    private static byte Clamp255(int value) => (byte)(value > 255 ? 255 : value);
}
