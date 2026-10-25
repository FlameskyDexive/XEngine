using System.IO.Compression;

namespace XEngine.Ai.Host;

/// <summary>8-bit RGB/RGBA PNG, no interlace. y-from-top tightly packed RGBA.</summary>
internal static class PngRgba
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("RGBA buffer does not match width/height.");

        using var raw = new MemoryStream((width * 4 + 1) * height);
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgba, y * width * 4, width * 4);
        }

        byte[] deflated;
        using (var ms = new MemoryStream())
        {
            using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                raw.Position = 0;
                raw.CopyTo(zlib);
            }

            deflated = ms.ToArray();
        }

        using var png = new MemoryStream();
        png.Write(Signature);
        WriteChunk(png, "IHDR", Ihdr(width, height, colorType: 6));
        WriteChunk(png, "IDAT", deflated);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    public static byte[] Decode(byte[] png, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("Not a PNG.");

        width = 0;
        height = 0;
        int bitDepth = 0;
        int colorType = 0;
        int interlace = 0;
        using var idat = new MemoryStream();
        int offset = 8;
        while (offset + 12 <= png.Length)
        {
            int len = ReadBe32(png, offset);
            if (offset + 12 + len > png.Length)
                throw new InvalidDataException("Truncated PNG chunk.");
            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            int data = offset + 8;
            if (type == "IHDR")
            {
                if (len < 13)
                    throw new InvalidDataException("Invalid IHDR.");
                width = ReadBe32(png, data);
                height = ReadBe32(png, data + 4);
                bitDepth = png[data + 8];
                colorType = png[data + 9];
                interlace = png[data + 12];
            }
            else if (type == "IDAT")
            {
                idat.Write(png, data, len);
            }
            else if (type == "IEND")
            {
                break;
            }

            offset += 12 + len;
        }

        if (width <= 0 || height <= 0 || bitDepth != 8 || interlace != 0 || (colorType != 2 && colorType != 6))
            throw new InvalidDataException($"Unsupported PNG (w={width} h={height} depth={bitDepth} type={colorType} interlace={interlace}).");

        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
            zlib.CopyTo(inflated);
        byte[] raw = inflated.ToArray();
        int bpp = colorType == 6 ? 4 : 3;
        int stride = width * bpp;
        int expected = (stride + 1) * height;
        if (raw.Length < expected)
            throw new InvalidDataException("PNG IDAT is shorter than the image.");

        byte[] rgba = new byte[width * height * 4];
        byte[] prev = new byte[stride];
        byte[] cur = new byte[stride];
        int rawOff = 0;
        for (int y = 0; y < height; y++)
        {
            byte filter = raw[rawOff++];
            Buffer.BlockCopy(raw, rawOff, cur, 0, stride);
            rawOff += stride;
            Unfilter(filter, cur, prev, bpp);
            if (colorType == 6)
            {
                Buffer.BlockCopy(cur, 0, rgba, y * width * 4, stride);
            }
            else
            {
                int dest = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    rgba[dest++] = cur[x * 3];
                    rgba[dest++] = cur[x * 3 + 1];
                    rgba[dest++] = cur[x * 3 + 2];
                    rgba[dest++] = 255;
                }
            }

            Buffer.BlockCopy(cur, 0, prev, 0, stride);
        }

        return rgba;
    }

    /// <summary>Bilinear resize. Uniform color stays uniform (used to downscale GPT Image / ComfyUI
    /// output to the unrotated atlas region before pack-crop).</summary>
    public static byte[] Resize(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0 || src.Length < srcW * srcH * 4)
            throw new ArgumentException("RGBA buffer does not match width/height.");
        if (srcW == dstW && srcH == dstH)
            return src;

        byte[] dst = new byte[dstW * dstH * 4];
        float scaleX = (float)srcW / dstW;
        float scaleY = (float)srcH / dstH;
        for (int y = 0; y < dstH; y++)
        {
            float srcY = (y + 0.5f) * scaleY - 0.5f;
            int y0 = (int)MathF.Floor(srcY);
            int y1 = y0 + 1;
            float fy = srcY - y0;
            if (y0 < 0)
            {
                y0 = 0;
                y1 = 0;
                fy = 0;
            }

            if (y1 >= srcH)
            {
                y1 = srcH - 1;
                y0 = Math.Min(y0, y1);
            }

            for (int x = 0; x < dstW; x++)
            {
                float srcX = (x + 0.5f) * scaleX - 0.5f;
                int x0 = (int)MathF.Floor(srcX);
                int x1 = x0 + 1;
                float fx = srcX - x0;
                if (x0 < 0)
                {
                    x0 = 0;
                    x1 = 0;
                    fx = 0;
                }

                if (x1 >= srcW)
                {
                    x1 = srcW - 1;
                    x0 = Math.Min(x0, x1);
                }

                int i00 = (y0 * srcW + x0) * 4;
                int i10 = (y0 * srcW + x1) * 4;
                int i01 = (y1 * srcW + x0) * 4;
                int i11 = (y1 * srcW + x1) * 4;
                int di = (y * dstW + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    float v0 = src[i00 + c] + (src[i10 + c] - src[i00 + c]) * fx;
                    float v1 = src[i01 + c] + (src[i11 + c] - src[i01 + c]) * fx;
                    dst[di + c] = (byte)Math.Clamp((int)MathF.Round(v0 + (v1 - v0) * fy), 0, 255);
                }
            }
        }

        return dst;
    }

    private static void Unfilter(byte filter, byte[] cur, byte[] prev, int bpp)
    {
        int n = cur.Length;
        switch (filter)
        {
            case 0:
                return;
            case 1:
                for (int i = bpp; i < n; i++)
                    cur[i] = (byte)(cur[i] + cur[i - bpp]);
                return;
            case 2:
                for (int i = 0; i < n; i++)
                    cur[i] = (byte)(cur[i] + prev[i]);
                return;
            case 3:
                for (int i = 0; i < n; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    cur[i] = (byte)(cur[i] + ((a + prev[i]) / 2));
                }

                return;
            case 4:
                for (int i = 0; i < n; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    int b = prev[i];
                    int c = i >= bpp ? prev[i - bpp] : 0;
                    cur[i] = (byte)(cur[i] + Paeth(a, b, c));
                }

                return;
            default:
                throw new InvalidDataException("Unsupported PNG filter " + filter);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
            return a;
        return pb <= pc ? b : c;
    }

    private static byte[] Ihdr(int width, int height, byte colorType)
    {
        var b = new byte[13];
        WriteBe32(b, 0, width);
        WriteBe32(b, 4, height);
        b[8] = 8;
        b[9] = colorType;
        return b;
    }

    private static void WriteChunk(MemoryStream png, string type, byte[] data)
    {
        WriteBe32(png, data.Length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < typeBytes.Length; i++)
            crc = Crc(crc, typeBytes[i]);
        for (int i = 0; i < data.Length; i++)
            crc = Crc(crc, data[i]);
        WriteBe32(png, (int)(crc ^ 0xFFFFFFFF));
    }

    private static void WriteBe32(MemoryStream s, int v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static void WriteBe32(byte[] b, int offset, int v)
    {
        b[offset] = (byte)(v >> 24);
        b[offset + 1] = (byte)(v >> 16);
        b[offset + 2] = (byte)(v >> 8);
        b[offset + 3] = (byte)v;
    }

    private static int ReadBe32(byte[] b, int offset)
        => (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];

    private static uint Crc(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }
}
