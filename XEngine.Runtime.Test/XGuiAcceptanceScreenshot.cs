// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using System.IO.Compression;
using System.Text;

namespace XEngine.Runtime.Test;

/// <summary>Tiny PNG + bitmap-text helper for XGUI visual acceptance boards.</summary>
internal static class XGuiAcceptanceScreenshot
{
    public sealed class Canvas
    {
        public readonly int Width;
        public readonly int Height;
        public readonly byte[] Rgba;

        public Canvas(int width, int height, byte r, byte g, byte b)
        {
            Width = width;
            Height = height;
            Rgba = new byte[width * height * 4];
            Fill(0, 0, width, height, r, g, b, 255);
        }

        public void Fill(int x, int y, int w, int h, byte r, byte g, byte b, byte a)
        {
            int x0 = Math.Max(0, x);
            int y0 = Math.Max(0, y);
            int x1 = Math.Min(Width, x + w);
            int y1 = Math.Min(Height, y + h);
            for (int py = y0; py < y1; py++)
            {
                int row = py * Width * 4;
                for (int px = x0; px < x1; px++)
                {
                    int i = row + px * 4;
                    Rgba[i] = r;
                    Rgba[i + 1] = g;
                    Rgba[i + 2] = b;
                    Rgba[i + 3] = a;
                }
            }
        }

        public void Stroke(int x, int y, int w, int h, byte r, byte g, byte b, int thickness = 1)
        {
            Fill(x, y, w, thickness, r, g, b, 255);
            Fill(x, y + h - thickness, w, thickness, r, g, b, 255);
            Fill(x, y, thickness, h, r, g, b, 255);
            Fill(x + w - thickness, y, thickness, h, r, g, b, 255);
        }

        public void Text(int x, int y, string text, byte r, byte g, byte b, int scale = 2)
        {
            int cursor = x;
            foreach (char c in text.ToUpperInvariant())
            {
                DrawGlyph(cursor, y, c, r, g, b, scale);
                cursor += 6 * scale;
            }
        }

        private void DrawGlyph(int x, int y, char c, byte r, byte g, byte b, int scale)
        {
            if (!Glyphs.TryGetValue(c, out byte[]? rows))
                rows = Glyphs['?'];
            for (int row = 0; row < 7; row++)
            {
                byte bits = rows[row];
                for (int col = 0; col < 5; col++)
                {
                    if ((bits & (1 << (4 - col))) == 0) continue;
                    Fill(x + col * scale, y + row * scale, scale, scale, r, g, b, 255);
                }
            }
        }
    }

    public static string Save(string name, Canvas canvas)
    {
        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "screenshots"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".png");
        return SaveTo(path, canvas);
    }

    public static string SaveTo(string path, Canvas canvas)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        return path;
    }

    internal static byte[] EncodeRgbaPng(byte[] rgba, int width, int height) => EncodePng(rgba, width, height);

    private static readonly Dictionary<char, byte[]> Glyphs = new()
    {
        [' '] = [0, 0, 0, 0, 0, 0, 0],
        ['-'] = [0, 0, 0, 0x0E, 0, 0, 0],
        ['.'] = [0, 0, 0, 0, 0, 0x04, 0x04],
        [':'] = [0, 0x04, 0x04, 0, 0x04, 0x04, 0],
        ['X'] = [0x11, 0x0A, 0x04, 0x04, 0x0A, 0x11, 0],
        ['/'] = [0x01, 0x02, 0x04, 0x04, 0x08, 0x10, 0],
        ['0'] = [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
        ['1'] = [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
        ['2'] = [0x0E, 0x11, 0x01, 0x06, 0x08, 0x10, 0x1F],
        ['3'] = [0x0E, 0x11, 0x01, 0x06, 0x01, 0x11, 0x0E],
        ['4'] = [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
        ['5'] = [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
        ['6'] = [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
        ['7'] = [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
        ['8'] = [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
        ['9'] = [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
        ['A'] = [0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
        ['B'] = [0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E],
        ['C'] = [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
        ['D'] = [0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E],
        ['E'] = [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F],
        ['F'] = [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10],
        ['G'] = [0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F],
        ['H'] = [0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
        ['I'] = [0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E],
        ['J'] = [0x01, 0x01, 0x01, 0x01, 0x11, 0x11, 0x0E],
        ['K'] = [0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11],
        ['L'] = [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F],
        ['M'] = [0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11],
        ['N'] = [0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11],
        ['O'] = [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
        ['P'] = [0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10],
        ['Q'] = [0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D],
        ['R'] = [0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11],
        ['S'] = [0x0E, 0x11, 0x10, 0x0E, 0x01, 0x11, 0x0E],
        ['T'] = [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
        ['U'] = [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
        ['V'] = [0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04],
        ['W'] = [0x11, 0x11, 0x11, 0x15, 0x15, 0x1B, 0x11],
        ['Y'] = [0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04],
        ['Z'] = [0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F],
        ['?'] = [0x0E, 0x11, 0x01, 0x06, 0x04, 0, 0x04],
    };

    private static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var memory = new MemoryStream();
        memory.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        WriteInt(ihdr, 0, width);
        WriteInt(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(memory, "IHDR", ihdr);

        byte[] raw = new byte[(width * 4 + 1) * height];
        int srcRow = width * 4;
        for (int y = 0; y < height; y++)
        {
            raw[y * (srcRow + 1)] = 0;
            Buffer.BlockCopy(rgba, y * srcRow, raw, y * (srcRow + 1) + 1, srcRow);
        }

        using var deflate = new MemoryStream();
        deflate.WriteByte(0x78);
        deflate.WriteByte(0x9C);
        using (var compressor = new DeflateStream(deflate, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(raw, 0, raw.Length);
        byte[] adler = new byte[4];
        WriteInt(adler, 0, (int)Adler32(raw));
        deflate.Write(adler, 0, 4);
        WriteChunk(memory, "IDAT", deflate.ToArray());
        WriteChunk(memory, "IEND", []);
        return memory.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] length = new byte[4];
        WriteInt(length, 0, data.Length);
        stream.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = Crc32(typeBytes, data);
        byte[] crcBytes = new byte[4];
        WriteInt(crcBytes, 0, (int)crc);
        stream.Write(crcBytes);
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in type) crc = Crc32Step(crc, value);
        foreach (byte value in data) crc = Crc32Step(crc, value);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint Crc32Step(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        return crc;
    }
}
