// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Resources;

namespace XEngine.Editor.Importers;

/// <summary>One frame's rectangle inside a packed Aseprite sheet (bottom-left / UV space).</summary>
public readonly struct AsepritePackedFrame
{
    public AsepritePackedFrame(int index, string spriteId, string displayName, SpriteRect rect, int durationMs)
    {
        Index = index;
        SpriteId = spriteId;
        DisplayName = displayName;
        Rect = rect;
        DurationMs = durationMs;
    }

    public int Index { get; }
    public string SpriteId { get; }
    public string DisplayName { get; }
    public SpriteRect Rect { get; }
    public int DurationMs { get; }
}

/// <summary>Horizontal strip of flattened Aseprite frames. CPU only — no Texture2D.</summary>
public sealed class AsepritePackedSheet
{
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Top-left RGBA8, length <c>Width * Height * 4</c>.</summary>
    public byte[] RgbaTopLeft { get; init; } = Array.Empty<byte>();
    public IReadOnlyList<AsepritePackedFrame> Frames { get; init; } = Array.Empty<AsepritePackedFrame>();
}

/// <summary>★ Gen-P4.3 — packs flattened Aseprite frames into a deterministic horizontal strip.</summary>
public static class AsepriteSpriteSheet
{
    public static AsepritePackedSheet? PackHorizontal(IReadOnlyList<AsepriteFramePixels> frames)
    {
        if (frames == null || frames.Count == 0)
            return null;

        int cellW = frames[0].Width;
        int cellH = frames[0].Height;
        if (cellW <= 0 || cellH <= 0)
            return null;

        int sheetW = cellW * frames.Count;
        int sheetH = cellH;
        var rgba = new byte[sheetW * sheetH * 4];
        var packed = new AsepritePackedFrame[frames.Count];

        for (int i = 0; i < frames.Count; i++)
        {
            AsepriteFramePixels frame = frames[i];
            int fw = Math.Min(cellW, frame.Width);
            int fh = Math.Min(cellH, frame.Height);
            Blit(frame.Rgba, frame.Width, rgba, sheetW, i * cellW, 0, fw, fh);
            packed[i] = new AsepritePackedFrame(frame.Index, $"sprite:{frame.Index}", $"Sprite {frame.Index}",
                new SpriteRect(i * cellW, 0, cellW, cellH), frame.DurationMs);
        }

        return new AsepritePackedSheet
        {
            Width = sheetW,
            Height = sheetH,
            RgbaTopLeft = rgba,
            Frames = packed,
        };
    }

    public static bool HasOpaque(AsepritePackedSheet sheet)
    {
        byte[] px = sheet.RgbaTopLeft;
        for (int i = 3; i < px.Length; i += 4)
        {
            if (px[i] > 0)
                return true;
        }

        return false;
    }

    /// <summary>Flips a top-left RGBA buffer to bottom-left (GL / <see cref="Texture2D"/> upload order).</summary>
    public static byte[] FlipVertical(byte[] src, int width, int height)
    {
        int stride = width * 4;
        var dst = new byte[src.Length];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(src, y * stride, dst, (height - 1 - y) * stride, stride);
        return dst;
    }

    private static void Blit(byte[] src, int srcW, byte[] dst, int dstW, int dx, int dy, int w, int h)
    {
        if (src.Length < srcW * h * 4)
            return;
        for (int y = 0; y < h; y++)
        {
            int srcOff = y * srcW * 4;
            int dstOff = ((dy + y) * dstW + dx) * 4;
            Buffer.BlockCopy(src, srcOff, dst, dstOff, w * 4);
        }
    }
}
