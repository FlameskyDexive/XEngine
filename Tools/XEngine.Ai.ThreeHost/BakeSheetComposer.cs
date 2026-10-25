using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace XEngine.Ai.ThreeHost;

/// <summary>
/// A5 — composes per-frame PNGs into a single flipbook sheet: row-major frames in a
/// near-square grid (cols = ceil(sqrt(n))), so sheet coordinates are trivial for shaders
/// (frame uv: (col + u)/cols, (row + v)/rows). Transparent bakes keep the frames' alpha
/// channel untouched.
/// </summary>
internal static class BakeSheetComposer
{
    public static byte[] Compose(IReadOnlyList<byte[]> framePngs, int frameWidth, int frameHeight, bool transparent)
    {
        if (framePngs.Count == 0)
            throw new IOException("Cannot compose a flipbook sheet from zero frames.");

        int cols = (int)Math.Ceiling(Math.Sqrt(framePngs.Count));
        int rows = (int)Math.Ceiling(framePngs.Count / (double)cols);
        using var sheet = new Bitmap(cols * frameWidth, rows * frameHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(sheet);
        if (!transparent)
            g.Clear(Color.Black);
        else
            g.Clear(Color.FromArgb(0, 0, 0, 0));

        for (int i = 0; i < framePngs.Count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            using var frame = Image.FromStream(new MemoryStream(framePngs[i]));
            g.DrawImage(frame, col * frameWidth, row * frameHeight, frameWidth, frameHeight);
        }

        using var stream = new MemoryStream();
        sheet.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
