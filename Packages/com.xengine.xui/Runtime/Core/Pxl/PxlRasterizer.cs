// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.XUI.Core;

/// <summary>
/// Turns a parsed grid into RGBA pixels. Pure BCL, so the importer and the offline
/// <c>pxl_render</c> tool produce the same bytes.
/// </summary>
public static class PxlRasterizer
{
    /// <summary>
    /// Rasterises one section, top row first — the order both PNG encoders and texture uploads expect,
    /// and the order the grid reads in the file.
    /// </summary>
    public static byte[] Rasterize(PxlDocument document, PxlSection section, int scale = 1)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(section);
        if (scale < 1) scale = 1;

        int width = section.Width * scale;
        int height = section.Height * scale;
        var rgba = new byte[width * height * 4];

        for (int y = 0; y < section.Height; y++)
        {
            string row = section.Rows[y];
            for (int x = 0; x < section.Width; x++)
            {
                XuiColor color = document.ColorOf(row[x]);
                byte r = ToByte(color.R);
                byte g = ToByte(color.G);
                byte b = ToByte(color.B);
                byte a = ToByte(color.A);

                // Nearest-neighbour upscale: pixel art must stay crisp, and any filtering here would
                // hide exactly the single-pixel mistakes the render is meant to reveal.
                for (int sy = 0; sy < scale; sy++)
                {
                    int destRow = (y * scale + sy) * width;
                    for (int sx = 0; sx < scale; sx++)
                    {
                        int index = (destRow + x * scale + sx) * 4;
                        rgba[index] = r;
                        rgba[index + 1] = g;
                        rgba[index + 2] = b;
                        rgba[index + 3] = a;
                    }
                }
            }
        }

        return rgba;
    }

    /// <summary>
    /// Lays every section out in a single column with one transparent pixel between them, and reports
    /// where each landed. One texture per file keeps the sections of a control's states together, which
    /// is how they are authored and how they are packed.
    /// </summary>
    public static byte[] RasterizeSheet(
        PxlDocument document,
        int scale,
        out int width,
        out int height,
        out List<PxlSheetEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (scale < 1) scale = 1;

        entries = new List<PxlSheetEntry>(document.Sections.Count);
        width = 0;
        height = 0;

        for (int i = 0; i < document.Sections.Count; i++)
        {
            PxlSection section = document.Sections[i];
            width = Math.Max(width, section.Width * scale);
            if (i > 0) height += 1;
            height += section.Height * scale;
        }

        if (width == 0 || height == 0)
        {
            width = 0;
            height = 0;
            return Array.Empty<byte>();
        }

        var sheet = new byte[width * height * 4];
        int cursorY = 0;
        for (int i = 0; i < document.Sections.Count; i++)
        {
            PxlSection section = document.Sections[i];
            if (i > 0) cursorY += 1;

            int sectionWidth = section.Width * scale;
            int sectionHeight = section.Height * scale;
            byte[] pixels = Rasterize(document, section, scale);

            for (int y = 0; y < sectionHeight; y++)
            {
                Buffer.BlockCopy(
                    pixels, y * sectionWidth * 4,
                    sheet, ((cursorY + y) * width) * 4,
                    sectionWidth * 4);
            }

            entries.Add(new PxlSheetEntry(section.Name, 0, cursorY, sectionWidth, sectionHeight));
            cursorY += sectionHeight;
        }

        return sheet;
    }

    private static byte ToByte(float value)
    {
        int scaled = (int)(value * 255f + 0.5f);
        if (scaled < 0) scaled = 0;
        if (scaled > 255) scaled = 255;
        return (byte)scaled;
    }
}

/// <summary>Where one section landed in the packed sheet. Y is measured from the top.</summary>
public sealed record PxlSheetEntry(string Name, int X, int Y, int Width, int Height);
