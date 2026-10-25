using System;
using System.IO;
using System.IO.Compression;
using System.Text;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Owned CPU face pixels. Demo atlas and packaged PNGs share this generator.</summary>
public static class VoxelFaceTextures
{
    public const uint Size = 16;
    public const uint WhiteSize = 64;

    public static readonly Guid White64Guid = Guid.Parse("fdc9f888-96fc-048b-f860-0563a62b9e7e");
    public static readonly Guid GrassTopGuid = Guid.Parse("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4a11");
    public static readonly Guid DirtGuid = Guid.Parse("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4a12");
    public static readonly Guid StoneGuid = Guid.Parse("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4a13");
    public static readonly Guid LeavesGuid = Guid.Parse("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4a14");
    public static readonly Guid GrassSideGuid = Guid.Parse("3c8e1a20-7b4d-4e91-9c2a-0f6d8b1e4a15");
    public static readonly Guid SandGuid = Guid.Parse("4d9e2b30-8c5e-4f02-ad3b-1a7c9d0e5f21");
    public static readonly Guid SnowGuid = Guid.Parse("4d9e2b30-8c5e-4f02-ad3b-1a7c9d0e5f22");
    public static readonly Guid WaterGuid = Guid.Parse("4d9e2b30-8c5e-4f02-ad3b-1a7c9d0e5f23");
    public static readonly Guid CloudGuid = Guid.Parse("4d9e2b30-8c5e-4f02-ad3b-1a7c9d0e5f24");
    public static readonly Guid WoodGuid = Guid.Parse("4d9e2b30-8c5e-4f02-ad3b-1a7c9d0e5f25");

    public const string White64Comment = "XEngine VoxelWorld owned white 64";

    public static byte[] White() => Solid(Size, 240, 240, 240);
    public static byte[] White64() => Solid(WhiteSize, 240, 240, 240);
    public static byte[] GrassTop() => Paint(Size, (x, y) =>
        Jitter((76, 168, 78), x, y, 11, (46, 122, 48), (180, 198, 72), (58, 140, 56), (98, 186, 90), (64, 150, 62)));

    public static byte[] Dirt() => Paint(Size, (x, y) =>
        Jitter((141, 108, 86), x, y, 21, (96, 70, 52), (168, 128, 92), (120, 88, 68), (154, 118, 94), (110, 82, 64)));

    public static byte[] GrassSide() => Paint(Size, (x, y) =>
    {
        int fringe = 4 + Noise(x, 0, 17, 2);
        if (y >= Size - fringe)
            return Jitter((76, 168, 78), x, y, 11, (46, 122, 48), (180, 198, 72), (58, 140, 56), (98, 186, 90));
        return Jitter((141, 108, 86), x, y, 21, (96, 70, 52), (168, 128, 92), (120, 88, 68), (154, 118, 94));
    });

    public static byte[] Stone() => Paint(Size, (x, y) =>
    {
        bool crack = (x + y * 3) % 11 == 0 || Noise(x, y, 31, 12) == 0;
        if (crack)
            return (88, 88, 92);
        return Jitter((150, 150, 148), x, y, 31, (176, 176, 172), (128, 128, 124), (164, 160, 156), (140, 142, 138));
    });

    public static byte[] Leaves() => Paint(Size, (x, y) =>
        Jitter((86, 168, 72), x, y, 41, (48, 110, 44), (118, 196, 88), (64, 140, 56), (98, 178, 78), (72, 154, 62)));

    public static byte[] Sand() => Paint(Size, (x, y) =>
        Jitter((228, 194, 122), x, y, 51, (196, 160, 88), (240, 214, 150), (210, 176, 104), (236, 204, 136), (220, 184, 110)));

    public static byte[] Snow() => Paint(Size, (x, y) =>
        Jitter((240, 244, 248), x, y, 61, (210, 222, 230), (255, 255, 255), (226, 234, 240), (248, 250, 252), (218, 228, 236)));

    public static byte[] Water() => Paint(Size, (x, y) =>
        Jitter((40, 120, 190), x, y, 71, (70, 160, 210), (28, 88, 150), (52, 140, 200), (36, 108, 176), (64, 148, 204)));

    public static byte[] Wood() => Paint(Size, (x, y) =>
    {
        int ring = Math.Abs((x - 8) * (x - 8) + (y % 4) - 4);
        if (x % 4 == 0)
            return (78, 48, 24);
        if (ring < 3)
            return Jitter((148, 98, 52), x, y, 81, (160, 110, 60), (132, 86, 44));
        return Jitter((110, 72, 40), x, y, 81, (124, 82, 46), (96, 62, 34), (118, 78, 42));
    });

    public static byte[] Forge() => Paint(Size, (x, y) =>
    {
        // stone body, dark iron band and a warm ember mouth
        if (y >= Size - 2)
            return Jitter((255, 148, 40), x, y, 91, (255, 120, 24), (255, 190, 80));
        if (y == 3)
            return (52, 52, 56);
        return Jitter((118, 118, 122), x, y, 91, (100, 100, 104), (140, 140, 144), (88, 88, 92));
    });

    public static byte[] Bed() => Paint(Size, (x, y) =>
    {
        // red blanket with a white pillow strip
        if (y < 3)
            return Jitter((238, 238, 238), x, y, 101, (222, 222, 226), (250, 250, 250));
        return Jitter((178, 44, 48), x, y, 101, (150, 32, 38), (204, 60, 64), (166, 40, 44));
    });

    public static byte[] Campfire() => Paint(Size, (x, y) =>
    {
        // log ring around a flame core
        int dx = x - 8;
        int dy = y - 8;
        int r2 = dx * dx + dy * dy;
        if (r2 < 14)
            return Jitter((255, 196, 64), x, y, 111, (255, 150, 32), (255, 232, 140));
        if (x % 4 == 0 || y % 4 == 0)
            return Jitter((96, 60, 32), x, y, 111, (78, 46, 24), (118, 76, 42));
        return Jitter((64, 44, 30), x, y, 111, (54, 36, 24), (76, 54, 38));
    });

    public static byte[] Obelisk() => Paint(Size, (x, y) =>
    {
        // deep violet pillar with a glowing rune band
        if (y >= 3 && y <= 5)
            return Jitter((140, 90, 255), x, y, 121, (110, 60, 230), (190, 150, 255));
        if (x % 4 == 0)
            return (60, 40, 92);
        return Jitter((86, 60, 130), x, y, 121, (70, 48, 110), (104, 74, 152), (78, 54, 118));
    });

    public static byte[] Cloud() => Paint(Size, (x, y) =>
    {
        int dx = x - 8;
        int dy = y - 8;
        int r2 = dx * dx + dy * dy;
        if (r2 > 62)
            return (228, 236, 244);
        if (Noise(x, y, 91, 11) == 0)
            return (214, 224, 236);
        return Jitter((248, 250, 252), x, y, 91, (255, 255, 255), (236, 242, 248), (242, 246, 250), (252, 253, 255));
    });

    public static bool TryGetOwned(Guid guid, out byte[] pixels, out uint size)
    {
        if (guid == White64Guid)
        {
            pixels = White64();
            size = WhiteSize;
            return true;
        }

        if (guid == GrassTopGuid) { pixels = GrassTop(); size = Size; return true; }
        if (guid == DirtGuid) { pixels = Dirt(); size = Size; return true; }
        if (guid == StoneGuid) { pixels = Stone(); size = Size; return true; }
        if (guid == LeavesGuid) { pixels = Leaves(); size = Size; return true; }
        if (guid == GrassSideGuid) { pixels = GrassSide(); size = Size; return true; }
        if (guid == SandGuid) { pixels = Sand(); size = Size; return true; }
        if (guid == SnowGuid) { pixels = Snow(); size = Size; return true; }
        if (guid == WaterGuid) { pixels = Water(); size = Size; return true; }
        if (guid == WoodGuid) { pixels = Wood(); size = Size; return true; }
        if (guid == CloudGuid) { pixels = Cloud(); size = Size; return true; }
        pixels = [];
        size = 0;
        return false;
    }

    public static Color Sample(ReadOnlySpan<byte> pixels, uint size, float u, float v)
    {
        if (size == 0)
            return Color.White;
        int x = Wrap(u, (int)size);
        int y = Wrap(v, (int)size);
        int i = (y * (int)size + x) * 4;
        if (i + 3 >= pixels.Length)
            return Color.White;
        return new Color(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }

    public static void WriteOwnedPackageFiles(string packageRoot)
    {
        ArgumentNullException.ThrowIfNull(packageRoot);
        string flat = Path.Combine(packageRoot, "Runtime", "Resources", "Worlds", "Flat");
        string earth = Path.Combine(packageRoot, "Runtime", "Resources", "Worlds", "Earth");
        Directory.CreateDirectory(flat);
        Directory.CreateDirectory(earth);

        WritePng(Path.Combine(flat, "grass_top.png"), GrassTop(), Size, Size);
        WritePng(Path.Combine(flat, "grass_side.png"), GrassSide(), Size, Size);
        WritePng(Path.Combine(flat, "dirt.png"), Dirt(), Size, Size);
        WritePng(Path.Combine(flat, "stone.png"), Stone(), Size, Size);
        WritePng(Path.Combine(flat, "leaves.png"), Leaves(), Size, Size);
        WritePng(Path.Combine(flat, "white64.png"), White64(), WhiteSize, WhiteSize, White64Comment);
        WritePng(Path.Combine(earth, "sand.png"), Sand(), Size, Size);
        WritePng(Path.Combine(earth, "snow.png"), Snow(), Size, Size);
        WritePng(Path.Combine(earth, "water.png"), Water(), Size, Size);
        WritePng(Path.Combine(earth, "wood.png"), Wood(), Size, Size);
        WritePng(Path.Combine(earth, "cloud.png"), Cloud(), Size, Size);
        WriteTextureMeta(Path.Combine(flat, "grass_side.png.meta"), GrassSideGuid);
        WriteTextureMeta(Path.Combine(earth, "sand.png.meta"), SandGuid);
        WriteTextureMeta(Path.Combine(earth, "snow.png.meta"), SnowGuid);
        WriteTextureMeta(Path.Combine(earth, "water.png.meta"), WaterGuid);
        WriteTextureMeta(Path.Combine(earth, "wood.png.meta"), WoodGuid);
        WriteTextureMeta(Path.Combine(earth, "cloud.png.meta"), CloudGuid);
    }

    public static byte[] EncodePng(byte[] rgba, uint width, uint height, string? textComment = null)
    {
        using var memory = new MemoryStream();
        memory.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] ihdr = new byte[13];
        WriteBe(ihdr, 0, (int)width);
        WriteBe(ihdr, 4, (int)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(memory, "IHDR", ihdr);
        if (!string.IsNullOrEmpty(textComment))
        {
            byte[] keyword = Encoding.ASCII.GetBytes("Comment");
            byte[] value = Encoding.ASCII.GetBytes(textComment);
            var text = new byte[keyword.Length + 1 + value.Length];
            Buffer.BlockCopy(keyword, 0, text, 0, keyword.Length);
            Buffer.BlockCopy(value, 0, text, keyword.Length + 1, value.Length);
            WriteChunk(memory, "tEXt", text);
        }

        int srcRow = (int)width * 4;
        byte[] raw = new byte[(srcRow + 1) * (int)height];
        for (int y = 0; y < height; y++)
        {
            raw[y * (srcRow + 1)] = 0;
            Buffer.BlockCopy(rgba, y * srcRow, raw, y * (srcRow + 1) + 1, srcRow);
        }

        using var deflate = new MemoryStream();
        deflate.WriteByte(0x78);
        deflate.WriteByte(0x9C);
        using (var compressor = new DeflateStream(deflate, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(raw);
        uint adler = Adler32(raw);
        WriteBe(deflate, (int)adler);
        WriteChunk(memory, "IDAT", deflate.ToArray());
        WriteChunk(memory, "IEND", []);
        return memory.ToArray();
    }

    private static void WritePng(string path, byte[] rgba, uint width, uint height, string? comment = null) =>
        File.WriteAllBytes(path, EncodePng(rgba, width, height, comment));

    private static void WriteTextureMeta(string path, Guid guid)
    {
        if (File.Exists(path) && File.ReadAllText(path).Contains(guid.ToString("D"), StringComparison.Ordinal))
            return;
        File.WriteAllText(path,
            "{\n" +
            $"  \"guid\": \"{guid:D}\",\n" +
            "  \"importer\": \"TextureImporter\",\n" +
            "  \"importerVersion\": 6,\n" +
            "  \"settings\": {\n" +
            "    \"generateMipmaps\": false,\n" +
            "    \"sRGB\": true,\n" +
            "    \"sRGBSampling\": false,\n" +
            "    \"minFilter\": 5,\n" +
            "    \"magFilter\": 1,\n" +
            "    \"wrapMode\": 0\n" +
            "  }\n" +
            "}\n");
    }

    private static byte[] Solid(uint size, byte r, byte g, byte b)
    {
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    private static (byte R, byte G, byte B) Jitter(
        (byte R, byte G, byte B) basis,
        int x,
        int y,
        int seed,
        params (byte R, byte G, byte B)[] accents)
    {
        int n = Noise(x, y, seed, accents.Length + 2);
        if (n < accents.Length)
            return accents[n];
        int wobble = Noise(x, y, seed + 99, 7) - 3;
        return (
            (byte)Math.Clamp(basis.R + wobble * 4, 0, 255),
            (byte)Math.Clamp(basis.G + wobble * 3, 0, 255),
            (byte)Math.Clamp(basis.B + wobble * 2, 0, 255));
    }

    private static byte[] Paint(uint size, Func<int, int, (byte R, byte G, byte B)> shade)
    {
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                (byte r, byte g, byte b) = shade(x, y);
                int i = (y * (int)size + x) * 4;
                pixels[i] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = 255;
            }
        }

        return pixels;
    }

    private static int Noise(int x, int y, int seed, int modulo)
    {
        int h = x * 374761393 + y * 668265263 + seed * 1274126177;
        h = (h ^ (h >> 13)) * 1274126177;
        h ^= h >> 16;
        return Math.Abs(h) % modulo;
    }

    private static int Wrap(float uv, int size)
    {
        float wrapped = uv - MathF.Floor(uv);
        int cell = (int)(wrapped * size);
        if (cell < 0)
            cell = 0;
        if (cell >= size)
            cell = size - 1;
        return cell;
    }

    private static void WriteChunk(Stream dest, string type, byte[] data)
    {
        WriteBe(dest, data.Length);
        foreach (char c in type)
            dest.WriteByte((byte)c);
        dest.Write(data);
        uint crc = 0xFFFFFFFFu;
        foreach (char c in type)
            crc = Crc(crc, (byte)c);
        foreach (byte b in data)
            crc = Crc(crc, b);
        WriteBe(dest, (int)(crc ^ 0xFFFFFFFFu));
    }

    private static void WriteBe(Stream dest, int value)
    {
        dest.WriteByte((byte)(value >> 24));
        dest.WriteByte((byte)(value >> 16));
        dest.WriteByte((byte)(value >> 8));
        dest.WriteByte((byte)value);
    }

    private static void WriteBe(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static uint Crc(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }
}
