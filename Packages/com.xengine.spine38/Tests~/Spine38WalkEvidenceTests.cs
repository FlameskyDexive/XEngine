using ImageMagick;

using Spine;

using XEngine.Runtime;
using XEngine.Vector;

using Xunit;

namespace XEngine.Spine38.Tests;

public sealed class Spine38WalkEvidenceTests
{
    [Fact]
    public void SpineboyWalk_TexturedMeshChangesAndWritesEvidence()
    {
        string package = Spine38TestPaths.PackageRoot();
        string jsonPath = Path.Combine(package, "Samples~", "GettingStarted", "Skeletons", "Spineboy", "spineboy-ess.json");
        string atlasPath = Path.Combine(package, "Samples~", "GettingStarted", "Skeletons", "Spineboy", "spineboy-pma.atlas");
        string pngPath = Path.Combine(package, "Samples~", "GettingStarted", "Skeletons", "Spineboy", "spineboy-pma.png");
        Assert.True(File.Exists(pngPath), pngPath);

        using var atlasReader = new StreamReader(atlasPath);
        var atlas = new Atlas(atlasReader, Path.GetDirectoryName(atlasPath) ?? "", SpineNoopTextureLoader.Instance);
        atlas.FlipV();
        var json = new SkeletonJson(new AtlasAttachmentLoader(atlas)) { Scale = 0.01f };
        using var skeletonReader = new StreamReader(jsonPath);
        SkeletonData data = json.ReadSkeletonData(skeletonReader);
        var skeleton = new Skeleton(data);
        skeleton.SetToSetupPose();
        var state = new AnimationState(new AnimationStateData(data));
        state.SetAnimation(0, "walk", loop: true);

        AtlasPixels page = AtlasPixels.Load(pngPath);
        var generator = new SpineMeshGenerator();
        skeleton.UpdateWorldTransform();
        generator.Build(skeleton, Color.White);
        Assert.True(generator.BatchCount > 0);
        byte[] frame0 = Raster(generator, page, 320, 400);
        AssertDistinctTexels(frame0, "setup");

        state.Update(0.4f);
        state.Apply(skeleton);
        skeleton.UpdateWorldTransform();
        generator.Build(skeleton, Color.White);
        byte[] frame1 = Raster(generator, page, 320, 400);
        AssertDistinctTexels(frame1, "walk 0.4s");
        Assert.False(frame0.AsSpan().SequenceEqual(frame1), "walk pose should change textured pixels");

        string repo = Spine38TestPaths.RepositoryRoot();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "packages", "spine38");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "spineboy-walk-t0.png"), EncodePng(frame0, 320, 400));
        File.WriteAllBytes(Path.Combine(dir, "spineboy-walk-t04.png"), EncodePng(frame1, 320, 400));
        File.WriteAllText(Path.Combine(dir, "spineboy-walk-mesh.md"),
            "# Spine 3.8 spineboy walk (textured)\n\n" +
            $"Date: {DateTime.UtcNow:yyyy-MM-dd}. Package: com.xengine.spine38 {Spine38Runtime.Version}.\n\n" +
            "Official `spineboy-ess.json` (spine 3.8.55) + `spineboy-pma.png` sampled through " +
            $"`SpineMeshGenerator` UVs. batches={generator.BatchCount}, verts[0]={generator.GetBatch(0).Vertices.Length}.\n\n" +
            "Frames: [spineboy-walk-t0.png](spineboy-walk-t0.png) (setup) vs " +
            "[spineboy-walk-t04.png](spineboy-walk-t04.png) (walk t=0.4s).\n");
        Assert.True(new FileInfo(Path.Combine(dir, "spineboy-walk-t0.png")).Length > 256);
    }

    [Fact]
    public void ResolvePageTexture_ReturnsNullForUnboundPage()
    {
        Assert.Null(SpineMeshGenerator.ResolvePageTexture(null));
        Assert.Null(SpineMeshGenerator.ResolvePageTexture("spineboy-pma.png"));
    }

    private static void AssertDistinctTexels(byte[] rgba, string label)
    {
        var colors = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 8)
                continue;
            colors.Add(rgba[i] << 16 | rgba[i + 1] << 8 | rgba[i + 2]);
        }

        Assert.True(colors.Count >= 20, $"{label} should sample the atlas, unique opaque colors={colors.Count}");
    }

    private static byte[] Raster(SpineMeshGenerator generator, AtlasPixels page, int width, int height)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 24;
            rgba[i + 1] = 26;
            rgba[i + 2] = 32;
            rgba[i + 3] = 255;
        }

        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (int b = 0; b < generator.BatchCount; b++)
        {
            SpineMeshBatch batch = generator.GetBatch(b);
            for (int i = 0; i < batch.Vertices.Length; i++)
            {
                minX = Math.Min(minX, batch.Vertices[i].X);
                maxX = Math.Max(maxX, batch.Vertices[i].X);
                minY = Math.Min(minY, batch.Vertices[i].Y);
                maxY = Math.Max(maxY, batch.Vertices[i].Y);
            }
        }

        float pad = 16f;
        float sx = (width - pad * 2) / Math.Max(1f, maxX - minX);
        float sy = (height - pad * 2) / Math.Max(1f, maxY - minY);
        float scale = Math.Min(sx, sy);

        Float2 Map(Float3 v) => new(pad + (v.X - minX) * scale, height - (pad + (v.Y - minY) * scale));
        for (int b = 0; b < generator.BatchCount; b++)
        {
            SpineMeshBatch batch = generator.GetBatch(b);
            for (int i = 0; i + 2 < batch.Indices.Length; i += 3)
            {
                int ia = (int)batch.Indices[i];
                int ib = (int)batch.Indices[i + 1];
                int ic = (int)batch.Indices[i + 2];
                FillTexturedTriangle(rgba, width, height, page,
                    Map(batch.Vertices[ia]), batch.UVs[ia], batch.Colors[ia],
                    Map(batch.Vertices[ib]), batch.UVs[ib], batch.Colors[ib],
                    Map(batch.Vertices[ic]), batch.UVs[ic], batch.Colors[ic]);
            }
        }

        return rgba;
    }

    private static void FillTexturedTriangle(byte[] rgba, int width, int height, AtlasPixels page,
        Float2 a, Float2 auv, Color32 ac,
        Float2 b, Float2 buv, Color32 bc,
        Float2 c, Float2 cuv, Color32 cc)
    {
        int minX = (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)));
        int maxX = (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X)));
        int minY = (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)));
        int maxY = (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y)));
        minX = Math.Clamp(minX, 0, width - 1);
        maxX = Math.Clamp(maxX, 0, width - 1);
        minY = Math.Clamp(minY, 0, height - 1);
        maxY = Math.Clamp(maxY, 0, height - 1);
        float area = Edge(a, b, c);
        if (MathF.Abs(area) < 1e-5f)
            return;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                var p = new Float2(x + 0.5f, y + 0.5f);
                float w0 = Edge(b, c, p) / area;
                float w1 = Edge(c, a, p) / area;
                float w2 = Edge(a, b, p) / area;
                if (w0 < 0f || w1 < 0f || w2 < 0f)
                    continue;

                float u = auv.X * w0 + buv.X * w1 + cuv.X * w2;
                float v = auv.Y * w0 + buv.Y * w1 + cuv.Y * w2;
                page.Sample(u, v, out byte sr, out byte sg, out byte sb, out byte sa);
                if (sa < 8)
                    continue;

                float cr = (ac.R * w0 + bc.R * w1 + cc.R * w2) / 255f;
                float cg = (ac.G * w0 + bc.G * w1 + cc.G * w2) / 255f;
                float cb = (ac.B * w0 + bc.B * w1 + cc.B * w2) / 255f;
                float ca = (ac.A * w0 + bc.A * w1 + cc.A * w2) / 255f;
                int i = (y * width + x) * 4;
                float srcA = (sa / 255f) * ca;
                float outA = srcA + (rgba[i + 3] / 255f) * (1f - srcA);
                rgba[i] = Premul(sr * cr, srcA, rgba[i], rgba[i + 3], outA);
                rgba[i + 1] = Premul(sg * cg, srcA, rgba[i + 1], rgba[i + 3], outA);
                rgba[i + 2] = Premul(sb * cb, srcA, rgba[i + 2], rgba[i + 3], outA);
                rgba[i + 3] = (byte)Math.Clamp((int)(outA * 255f), 0, 255);
            }
        }
    }

    private static byte Premul(float srcChan, float srcA, byte dst, byte dstA, float outA)
    {
        float dstN = dst / 255f;
        float dstAn = dstA / 255f;
        float value = srcChan / 255f + dstN * (1f - srcA);
        if (outA > 0f)
            value /= outA;
        return (byte)Math.Clamp((int)(value * outA * 255f), 0, 255);
    }

    private static float Edge(Float2 a, Float2 b, Float2 c)
        => (c.X - a.X) * (b.Y - a.Y) - (c.Y - a.Y) * (b.X - a.X);

    private readonly struct AtlasPixels
    {
        public readonly int Width;
        public readonly int Height;
        public readonly byte[] Rgba;

        private AtlasPixels(int width, int height, byte[] rgba)
        {
            Width = width;
            Height = height;
            Rgba = rgba;
        }

        public static AtlasPixels Load(string path)
        {
            using var image = new MagickImage(path);
            image.Flip();
            image.ColorSpace = ColorSpace.sRGB;
            image.ColorType = ColorType.TrueColorAlpha;
            byte[] rgba = image.GetPixels().ToByteArray(PixelMapping.RGBA)
                ?? throw new InvalidOperationException("Failed to decode " + path);
            return new AtlasPixels((int)image.Width, (int)image.Height, rgba);
        }

        public void Sample(float u, float v, out byte r, out byte g, out byte b, out byte a)
        {
            float x = Math.Clamp(u, 0f, 1f) * (Width - 1);
            float y = Math.Clamp(v, 0f, 1f) * (Height - 1);
            int x0 = (int)Math.Floor(x);
            int y0 = (int)Math.Floor(y);
            int x1 = Math.Min(x0 + 1, Width - 1);
            int y1 = Math.Min(y0 + 1, Height - 1);
            float fx = x - x0;
            float fy = y - y0;
            Lerp(Pixel(x0, y0), Pixel(x1, y0), Pixel(x0, y1), Pixel(x1, y1), fx, fy, out r, out g, out b, out a);
        }

        private ReadOnlySpan<byte> Pixel(int x, int y)
            => Rgba.AsSpan((y * Width + x) * 4, 4);

        private static void Lerp(ReadOnlySpan<byte> p00, ReadOnlySpan<byte> p10, ReadOnlySpan<byte> p01, ReadOnlySpan<byte> p11,
            float fx, float fy, out byte r, out byte g, out byte b, out byte a)
        {
            static float Mix(byte a, byte b, float t) => a + (b - a) * t;
            float r0 = Mix(p00[0], p10[0], fx);
            float g0 = Mix(p00[1], p10[1], fx);
            float b0 = Mix(p00[2], p10[2], fx);
            float a0 = Mix(p00[3], p10[3], fx);
            float r1 = Mix(p01[0], p11[0], fx);
            float g1 = Mix(p01[1], p11[1], fx);
            float b1 = Mix(p01[2], p11[2], fx);
            float a1 = Mix(p01[3], p11[3], fx);
            r = (byte)Math.Clamp((int)(r0 + (r1 - r0) * fy), 0, 255);
            g = (byte)Math.Clamp((int)(g0 + (g1 - g0) * fy), 0, 255);
            b = (byte)Math.Clamp((int)(b0 + (b1 - b0) * fy), 0, 255);
            a = (byte)Math.Clamp((int)(a0 + (a1 - a0) * fy), 0, 255);
        }
    }

    private static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        WriteChunk(bw, "IHDR", Ihdr(width, height));
        WriteChunk(bw, "IDAT", Zlib(Filter(rgba, width, height)));
        WriteChunk(bw, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] Ihdr(int width, int height)
    {
        byte[] b = new byte[13];
        WriteInt(b, 0, width);
        WriteInt(b, 4, height);
        b[8] = 8;
        b[9] = 6;
        return b;
    }

    private static byte[] Filter(byte[] rgba, int width, int height)
    {
        var raw = new byte[(width * 4 + 1) * height];
        for (int y = 0; y < height; y++)
        {
            raw[y * (width * 4 + 1)] = 0;
            Buffer.BlockCopy(rgba, y * width * 4, raw, y * (width * 4 + 1) + 1, width * 4);
        }

        return raw;
    }

    private static byte[] Zlib(byte[] raw)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x01);
        const int max = 65535;
        for (int offset = 0; offset < raw.Length; offset += max)
        {
            int n = Math.Min(max, raw.Length - offset);
            bool last = offset + n >= raw.Length;
            ms.WriteByte((byte)(last ? 1 : 0));
            ms.WriteByte((byte)(n & 0xff));
            ms.WriteByte((byte)(n >> 8));
            int ninv = ~n;
            ms.WriteByte((byte)(ninv & 0xff));
            ms.WriteByte((byte)(ninv >> 8));
            ms.Write(raw, offset, n);
        }

        uint adler = Adler(raw);
        ms.WriteByte((byte)(adler >> 24));
        ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8));
        ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    private static uint Adler(byte[] data)
    {
        uint a = 1, b = 0;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static void WriteChunk(BinaryWriter bw, string type, byte[] data)
    {
        WriteInt(bw, data.Length);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        bw.Write(typeBytes);
        bw.Write(data);
        uint crc = Crc(typeBytes, data);
        WriteInt(bw, (int)crc);
    }

    private static void WriteInt(BinaryWriter bw, int value)
    {
        bw.Write((byte)(value >> 24));
        bw.Write((byte)(value >> 16));
        bw.Write((byte)(value >> 8));
        bw.Write((byte)value);
    }

    private static void WriteInt(byte[] dest, int offset, int value)
    {
        dest[offset] = (byte)(value >> 24);
        dest[offset + 1] = (byte)(value >> 16);
        dest[offset + 2] = (byte)(value >> 8);
        dest[offset + 3] = (byte)value;
    }

    private static uint Crc(byte[] type, byte[] data)
    {
        uint c = 0xffffffff;
        for (int i = 0; i < type.Length; i++) c = CrcByte(c, type[i]);
        for (int i = 0; i < data.Length; i++) c = CrcByte(c, data[i]);
        return c ^ 0xffffffff;
    }

    private static uint CrcByte(uint c, byte b)
    {
        c ^= b;
        for (int k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
        return c;
    }
}
