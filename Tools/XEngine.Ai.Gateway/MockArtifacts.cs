namespace XEngine.Ai.Gateway;

/// <summary>
/// Tiny but *valid* placeholder files for the mock provider: the point is that they flow through
/// the real ingestion path (Clay import, asset_subassets, TextureImporter, AudioImporter) exactly
/// like paid-provider output would.
/// </summary>
public static class MockArtifacts
{
    /// <summary>A one-triangle GLB with one mesh ("MockMesh" → sub-asset "MockMesh/prim0") and one
    /// material ("MockMaterial"). Deterministic bytes.</summary>
    public static byte[] MinimalGlb()
    {
        byte[] bin = new byte[44];
        WriteFloats(bin, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0);   // 3 positions
        BitConverter.GetBytes((ushort)0).CopyTo(bin, 36);  // indices 0,1,2
        BitConverter.GetBytes((ushort)1).CopyTo(bin, 38);
        BitConverter.GetBytes((ushort)2).CopyTo(bin, 40);

        string json = """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],
            "nodes":[{"name":"MockNode","mesh":0}],
            "meshes":[{"name":"MockMesh","primitives":[{"attributes":{"POSITION":0},"indices":1,"material":0}]}],
            "materials":[{"name":"MockMaterial","pbrMetallicRoughness":{"baseColorFactor":[0.8,0.8,0.8,1.0]}}],
            "buffers":[{"byteLength":44}],
            "bufferViews":[
             {"buffer":0,"byteOffset":0,"byteLength":36},
             {"buffer":0,"byteOffset":36,"byteLength":6}],
            "accessors":[
             {"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]},
             {"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"}]}
            """;

        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - jsonBytes.Length % 4) % 4;
        int jsonChunk = jsonBytes.Length + jsonPad;
        int total = 12 + 8 + jsonChunk + 8 + bin.Length;

        using var ms = new MemoryStream(total);
        using var w = new BinaryWriter(ms);
        w.Write(0x46546C67u);           // "glTF"
        w.Write(2u);
        w.Write((uint)total);
        w.Write((uint)jsonChunk);
        w.Write(0x4E4F534Au);           // "JSON"
        w.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
        w.Write((uint)bin.Length);
        w.Write(0x004E4942u);           // "BIN\0"
        w.Write(bin);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A unit-cube GLB (8 verts, 12 triangles, mesh "BoxMesh", material "BoxMaterial").
    /// Used as the mock "high poly" stand-in so <c>gen_ensure_budget</c> has something to decimate.</summary>
    public static byte[] BoxGlb()
    {
        float[] positions =
        {
            -0.5f, -0.5f, -0.5f,  0.5f, -0.5f, -0.5f,  0.5f,  0.5f, -0.5f, -0.5f,  0.5f, -0.5f,
            -0.5f, -0.5f,  0.5f,  0.5f, -0.5f,  0.5f,  0.5f,  0.5f,  0.5f, -0.5f,  0.5f,  0.5f,
        };
        ushort[] indices =
        {
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 4, 7, 0, 7, 3,
            1, 2, 6, 1, 6, 5,
            0, 1, 5, 0, 5, 4,
            3, 7, 6, 3, 6, 2,
        };

        byte[] bin = new byte[positions.Length * 4 + indices.Length * 2];
        WriteFloats(bin, 0, positions);
        for (int i = 0; i < indices.Length; i++)
        {
            BitConverter.GetBytes(indices[i]).CopyTo(bin, positions.Length * 4 + i * 2);
        }

        string json = """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],
            "nodes":[{"name":"BoxNode","mesh":0}],
            "meshes":[{"name":"BoxMesh","primitives":[{"attributes":{"POSITION":0},"indices":1,"material":0}]}],
            "materials":[{"name":"BoxMaterial","pbrMetallicRoughness":{"baseColorFactor":[0.7,0.5,0.3,1.0]}}],
            "buffers":[{"byteLength":168}],
            "bufferViews":[
             {"buffer":0,"byteOffset":0,"byteLength":96},
             {"buffer":0,"byteOffset":96,"byteLength":72}],
            "accessors":[
             {"bufferView":0,"componentType":5126,"count":8,"type":"VEC3","min":[-0.5,-0.5,-0.5],"max":[0.5,0.5,0.5]},
             {"bufferView":1,"componentType":5123,"count":36,"type":"SCALAR"}]}
            """;

        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - jsonBytes.Length % 4) % 4;
        int jsonChunk = jsonBytes.Length + jsonPad;
        int total = 12 + 8 + jsonChunk + 8 + bin.Length;

        using var ms = new MemoryStream(total);
        using var w = new BinaryWriter(ms);
        w.Write(0x46546C67u);
        w.Write(2u);
        w.Write((uint)total);
        w.Write((uint)jsonChunk);
        w.Write(0x4E4F534Au);
        w.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
        w.Write((uint)bin.Length);
        w.Write(0x004E4942u);
        w.Write(bin);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A one-triangle GLB with two named animation clips (Idle, Walk) so the character
    /// wire path can build an AnimatorController without a paid motion provider.</summary>
    public static byte[] AnimatedGlb()
    {
        byte[] bin = new byte[76];
        WriteFloats(bin, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0);
        BitConverter.GetBytes((ushort)0).CopyTo(bin, 36);
        BitConverter.GetBytes((ushort)1).CopyTo(bin, 38);
        BitConverter.GetBytes((ushort)2).CopyTo(bin, 40);
        WriteFloats(bin, 44, 0f, 1f);
        WriteFloats(bin, 52, 0, 0, 0, 0, 1, 0);

        string json = """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],
            "nodes":[{"name":"HeroNode","mesh":0}],
            "meshes":[{"name":"HeroMesh","primitives":[{"attributes":{"POSITION":0},"indices":1,"material":0}]}],
            "materials":[{"name":"HeroMaterial","pbrMetallicRoughness":{"baseColorFactor":[0.4,0.6,0.9,1.0]}}],
            "animations":[
             {"name":"Idle","samplers":[{"input":2,"output":3,"interpolation":"LINEAR"}],
              "channels":[{"sampler":0,"target":{"node":0,"path":"translation"}}]},
             {"name":"Walk","samplers":[{"input":2,"output":3,"interpolation":"LINEAR"}],
              "channels":[{"sampler":0,"target":{"node":0,"path":"translation"}}]}],
            "buffers":[{"byteLength":76}],
            "bufferViews":[
             {"buffer":0,"byteOffset":0,"byteLength":36},
             {"buffer":0,"byteOffset":36,"byteLength":6},
             {"buffer":0,"byteOffset":44,"byteLength":8},
             {"buffer":0,"byteOffset":52,"byteLength":24}],
            "accessors":[
             {"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]},
             {"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"},
             {"bufferView":2,"componentType":5126,"count":2,"type":"SCALAR","min":[0],"max":[1]},
             {"bufferView":3,"componentType":5126,"count":2,"type":"VEC3"}]}
            """;

        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - jsonBytes.Length % 4) % 4;
        int jsonChunk = jsonBytes.Length + jsonPad;
        int binPad = (4 - bin.Length % 4) % 4;
        int binChunk = bin.Length + binPad;
        int total = 12 + 8 + jsonChunk + 8 + binChunk;

        using var ms = new MemoryStream(total);
        using var w = new BinaryWriter(ms);
        w.Write(0x46546C67u);
        w.Write(2u);
        w.Write((uint)total);
        w.Write((uint)jsonChunk);
        w.Write(0x4E4F534Au);
        w.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
        w.Write((uint)binChunk);
        w.Write(0x004E4942u);
        w.Write(bin);
        for (int i = 0; i < binPad; i++) w.Write((byte)0x00);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A 4×1 walk-cycle strip (32×8 RGB, 8×8 cells: red / green / blue / yellow) so
    /// <c>sprite_slice</c> can grid-cut mock <c>kind=sprite</c> output without a paid PixelLab call.</summary>
    public static byte[] SpritesheetPng(int columns = 4, int rows = 1, int cell = 8)
    {
        int w = Math.Max(1, columns) * Math.Max(1, cell);
        int h = Math.Max(1, rows) * Math.Max(1, cell);
        var rgb = new byte[w * h * 3];
        byte[][] palette =
        {
            new byte[] { 200, 60, 60 },
            new byte[] { 60, 180, 80 },
            new byte[] { 60, 90, 200 },
            new byte[] { 220, 200, 50 },
        };
        for (int y = 0; y < h; y++)
        {
            int row = y / cell;
            for (int x = 0; x < w; x++)
            {
                int col = x / cell;
                byte[] c = palette[(row * columns + col) % palette.Length];
                int i = (y * w + x) * 3;
                rgb[i] = c[0];
                rgb[i + 1] = c[1];
                rgb[i + 2] = c[2];
            }
        }

        return EncodeRgbPng(rgb, w, h);
    }

    /// <summary>1×1 transparent PNG.</summary>
    public static byte[] Png1x1() => new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x62, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    };

    /// <summary>0.1 s of 16-bit mono 8 kHz silence (standard 44-byte RIFF/WAVE header + data).</summary>
    public static byte[] SilentWav()
    {
        const int sampleRate = 8000;
        const short bitsPerSample = 16;
        const short channels = 1;
        int samples = sampleRate / 10;
        int dataLen = samples * (bitsPerSample / 8) * channels;

        using var ms = new MemoryStream(44 + dataLen);
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataLen);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);                                     // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * (bitsPerSample / 8));  // byte rate
        w.Write((short)(channels * (bitsPerSample / 8)));      // block align
        w.Write(bitsPerSample);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataLen);
        w.Write(new byte[dataLen]);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] EncodeRgbPng(byte[] rgb, int w, int h)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgb, y * w * 3, w * 3);
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
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        WritePngChunk(png, "IHDR", PngIhdr(w, h));
        WritePngChunk(png, "IDAT", deflated);
        WritePngChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static byte[] PngIhdr(int w, int h)
    {
        var b = new byte[13];
        b[0] = (byte)(w >> 24); b[1] = (byte)(w >> 16); b[2] = (byte)(w >> 8); b[3] = (byte)w;
        b[4] = (byte)(h >> 24); b[5] = (byte)(h >> 16); b[6] = (byte)(h >> 8); b[7] = (byte)h;
        b[8] = 8;
        b[9] = 2;
        return b;
    }

    private static void WritePngChunk(MemoryStream png, string type, byte[] data)
    {
        WriteBe32(png, data.Length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < typeBytes.Length; i++) crc = PngCrc(crc, typeBytes[i]);
        for (int i = 0; i < data.Length; i++) crc = PngCrc(crc, data[i]);
        WriteBe32(png, crc ^ 0xFFFFFFFF);
    }

    private static void WriteBe32(MemoryStream s, int v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static void WriteBe32(MemoryStream s, uint v) => WriteBe32(s, (int)v);

    private static uint PngCrc(uint crc, byte b)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }

    private static void WriteFloats(byte[] target, int offset, params float[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            BitConverter.GetBytes(values[i]).CopyTo(target, offset + i * 4);
        }
    }
}
