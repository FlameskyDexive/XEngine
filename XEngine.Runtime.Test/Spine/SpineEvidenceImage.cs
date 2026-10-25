using System.IO.Compression;
using System.Text;

namespace XEngine.Runtime.Test.Spine;

internal static class SpineEvidenceImage
{
    public static void Fill(byte[] rgba, int stride, int x, int y, int w, int h, byte r, byte g, byte b)
    {
        int x1 = Math.Min(stride, x + w);
        int y1 = Math.Min(rgba.Length / (stride * 4), y + h);
        for (int py = Math.Max(0, y); py < y1; py++)
        {
            for (int px = Math.Max(0, x); px < x1; px++)
            {
                int i = (py * stride + px) * 4;
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = b;
                rgba[i + 3] = 255;
            }
        }
    }

    public static void FillCircle(byte[] rgba, int width, int height, int cx, int cy, int radius, byte r, byte g, byte b)
    {
        int x0 = Math.Max(0, cx - radius);
        int x1 = Math.Min(width - 1, cx + radius);
        int y0 = Math.Max(0, cy - radius);
        int y1 = Math.Min(height - 1, cy + radius);
        int r2 = radius * radius;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int dx = x - cx;
                int dy = y - cy;
                if (dx * dx + dy * dy > r2)
                    continue;
                int i = (y * width + x) * 4;
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = b;
                rgba[i + 3] = 255;
            }
        }
    }

    public static byte[] EncodePng(byte[] rgba, int width, int height)
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

    public static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] length = new byte[4];
        WriteInt(length, 0, data.Length);
        stream.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte value in typeBytes) crc = Crc32Step(crc, value);
        foreach (byte value in data) crc = Crc32Step(crc, value);
        byte[] crcBytes = new byte[4];
        WriteInt(crcBytes, 0, (int)(crc ^ 0xFFFFFFFF));
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

    private static uint Crc32Step(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        return crc;
    }
}
