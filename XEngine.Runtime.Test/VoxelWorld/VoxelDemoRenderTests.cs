using System.IO.Compression;

using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelDemoRenderTests
{
    private const int Width = 1280;
    private const int Height = 720;

    [Fact]
    public void FlatWorld_RendersVoxelOverview()
    {
        Shot shot = Render(
            "demo-flat-overview",
            new FlatTerrainGenerator { Altitude = 50, SurfaceTypeIndex = 2 },
            viewDistance: 3,
            maxChunkY: 3,
            camera: new Float3(48f, 78f, -18f),
            lookAt: new Float3(16f, 50f, 28f),
            terrainType: 3);
        AssertLooksLikeVoxelTerrain(shot, expectGrass: true, expectDirt: false, expectWater: false, expectCloud: false);
    }

    [Fact]
    public void EarthWorld_RendersUnityStyleVoxelDemo()
    {
        Shot shot = Render(
            "demo-earth-overview",
            new EarthTerrainGenerator { Seed = 1, BaseHeight = 36, Amplitude = 20, AddWater = true, WaterLevel = 28, AddClouds = true, CloudAltitude = 44 },
            viewDistance: 4,
            maxChunkY: 3,
            camera: new Float3(56f, 86f, -22f),
            lookAt: new Float3(18f, 40f, 36f),
            terrainType: 3);
        Assert.True(shot.WaterVoxels > 50, $"Earth overview generated no water ({shot.WaterVoxels}).");
        Assert.True(shot.CloudVoxels > 20, $"Earth overview generated no clouds ({shot.CloudVoxels}).");
        AssertLooksLikeVoxelTerrain(shot, expectGrass: true, expectDirt: true, expectWater: true, expectCloud: true);

        Float3 water = shot.WaterLookAt;
        Assert.True(shot.HasSurfaceWater, $"No surface water near look-at; water voxels={shot.WaterVoxels}.");
        Shot close = Render(
            "demo-earth-water-clouds",
            new EarthTerrainGenerator { Seed = 1, BaseHeight = 36, Amplitude = 20, AddWater = true, WaterLevel = 28, AddClouds = true, CloudAltitude = 44 },
            viewDistance: 4,
            maxChunkY: 3,
            camera: water + new Float3(0.2f, 16f, 0.2f),
            lookAt: water,
            terrainType: 3);
        Assert.True(close.WaterVoxels > 0, $"Close-up generated no water voxels at {water}.");
        Assert.True(
            CountWaterPixels(close.Rgba) > 80,
            $"Missing water texels ({CountWaterPixels(close.Rgba)}) at {water}, generated {close.WaterVoxels} water voxels.");
    }

    [Fact]
    public void FirstPerson_RendersHorizonWalkView()
    {
        Shot shot = Render(
            "demo-earth-firstperson",
            new EarthTerrainGenerator { Seed = 1, BaseHeight = 36, Amplitude = 20, AddWater = true, WaterLevel = 28, AddClouds = true, CloudAltitude = 44 },
            viewDistance: 4,
            maxChunkY: 3,
            camera: new Float3(12f, 46f, 10f),
            lookAt: new Float3(40f, 42f, 48f),
            terrainType: 3);
        AssertLooksLikeVoxelTerrain(shot, expectGrass: true, expectDirt: false, expectWater: false, expectCloud: false);
    }

    private static Shot Render(
        string name,
        VoxelTerrainGenerator generator,
        int viewDistance,
        int maxChunkY,
        Float3 camera,
        Float3 lookAt,
        ushort terrainType)
    {
        var rgba = new byte[Width * Height * 4];
        var depth = new float[Width * Height];
        Array.Fill(depth, float.PositiveInfinity);
        FillSky(rgba);

        Float3 forward = Float3.Normalize(lookAt - camera);
        Float3 right = Float3.Normalize(Float3.Cross(new Float3(0f, 1f, 0f), forward));
        if (Float3.LengthSquared(right) < 1e-6f)
            right = new Float3(1f, 0f, 0f);
        Float3 up = Float3.Cross(forward, right);
        Float3 light = Float3.Normalize(new Float3(0.45f, 0.82f, 0.35f));
        float focal = 0.5f * Height / MathF.Tan(30f * MathF.PI / 180f);

        Int3 focus = VoxelCoords.ChunkCoord(lookAt);
        VoxelAtlas atlas = XEngine.VoxelWorld.VoxelWorld.CreateDemoAtlas();
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];
        var bakeLight = new byte[VoxelCoords.ChunkVoxelCount];
        int triangles = 0;
        int waterVoxels = 0;
        int cloudVoxels = 0;
        Float3 waterLook = lookAt;
        Float3 cloudLook = lookAt;
        float bestWater = float.PositiveInfinity;
        for (int dz = -viewDistance; dz <= viewDistance; dz++)
        {
            for (int dx = -viewDistance; dx <= viewDistance; dx++)
            {
                for (int cy = 0; cy <= maxChunkY; cy++)
                {
                    var coord = new Int3(focus.X + dx, cy, focus.Z + dz);
                    Int3 min = VoxelCoords.ChunkMin(coord);
                    generator.PaintChunk(voxels, min, terrainType);
                    for (int i = 0; i < voxels.Length; i++)
                    {
                        ushort type = voxels[i].TypeIndex;
                        if (type == VoxelDecorations.Water)
                        {
                            waterVoxels++;
                            Int3 local = VoxelCoords.LocalFromIndex(i);
                            bool openAbove = local.Y == VoxelCoords.ChunkSize - 1 ||
                                voxels[VoxelCoords.Index(local.X, local.Y + 1, local.Z)].IsEmpty;
                            if (!openAbove)
                                continue;
                            var pos = new Float3(min.X + local.X + 0.5f, min.Y + local.Y + 0.5f, min.Z + local.Z + 0.5f);
                            float dist = Float3.LengthSquared(pos - lookAt);
                            if (dist < bestWater)
                            {
                                bestWater = dist;
                                waterLook = pos;
                            }
                        }
                        else if (type == VoxelDecorations.Cloud)
                        {
                            if (cloudVoxels == 0)
                            {
                                Int3 local = VoxelCoords.LocalFromIndex(i);
                                cloudLook = new Float3(min.X + local.X + 0.5f, min.Y + local.Y + 0.5f, min.Z + local.Z + 0.5f);
                            }

                            cloudVoxels++;
                        }
                    }

                    VoxelLighting.Bake(voxels, bakeLight);
                    VoxelMeshBuild mesh = VoxelGreedyMesher.Build(voxels, atlas, bakeLight);
                    triangles += Rasterize(mesh, atlas, min, camera, right, up, forward, light, focal, rgba, depth);
                }
            }
        }

        Assert.True(triangles > 200, $"Expected a meshed landscape, got {triangles} triangles.");
        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        string path = Path.Combine(evidence, name + ".png");
        WritePng(path, rgba, Width, Height);
        File.WriteAllText(Path.Combine(evidence, name + ".md"),
            $"# Voxel World demo — {name}\n\n" +
            "Date: 2026-09-16. Branch: `main`.\n\n" +
            "Perspective raster of greedy-meshed chunks sampling the owned Texture2DArray faces " +
            $"(not flat type colors). Camera {camera} look-at {lookAt}. Triangles: {triangles}. " +
            $"Water voxels: {waterVoxels}. Cloud voxels: {cloudVoxels}.\n\n" +
            $"Screenshot: [{name}.png]({name}.png)\n");
        return new Shot
        {
            Path = path,
            Rgba = rgba,
            WaterVoxels = waterVoxels,
            CloudVoxels = cloudVoxels,
            WaterLookAt = waterLook,
            CloudLookAt = cloudLook,
            HasSurfaceWater = bestWater < float.PositiveInfinity,
        };
    }

    private static int Rasterize(
        VoxelMeshBuild mesh,
        VoxelAtlas atlas,
        Int3 chunkMin,
        Float3 camera,
        Float3 right,
        Float3 up,
        Float3 forward,
        Float3 light,
        float focal,
        byte[] rgba,
        float[] depth)
    {
        int triangles = 0;
        Float3 origin = new(chunkMin.X, chunkMin.Y, chunkMin.Z);
        for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            int i0 = (int)mesh.Indices[i];
            int i1 = (int)mesh.Indices[i + 1];
            int i2 = (int)mesh.Indices[i + 2];
            if (!Project(mesh.Vertices[i0] + origin, camera, right, up, forward, focal, out Float3 p0))
                continue;
            if (!Project(mesh.Vertices[i1] + origin, camera, right, up, forward, focal, out Float3 p1))
                continue;
            if (!Project(mesh.Vertices[i2] + origin, camera, right, up, forward, focal, out Float3 p2))
                continue;

            int slice = mesh.UV2.Count > i0 ? (int)mesh.UV2[i0].X : 0;
            Float2 uv0 = mesh.UV.Count > i0 ? mesh.UV[i0] : default;
            Float2 uv1 = mesh.UV.Count > i1 ? mesh.UV[i1] : default;
            Float2 uv2 = mesh.UV.Count > i2 ? mesh.UV[i2] : default;
            Float3 n = mesh.Normals.Count > i0 ? mesh.Normals[i0] : new Float3(0f, 1f, 0f);
            float lambert = Math.Clamp(0.28f + 0.72f * MathF.Max(0f, Float3.Dot(n, light)), 0.12f, 1f);
            if (mesh.Colors.Count > i0)
            {
                Color baked = mesh.Colors[i0];
                lambert *= Math.Clamp((baked.R + baked.G + baked.B) / 3f, 0.35f, 1f);
            }

            FillTexturedTriangle(p0, p1, p2, uv0, uv1, uv2, atlas, slice, lambert, rgba, depth);
            triangles++;
        }

        return triangles;
    }

    private static bool Project(
        Float3 world,
        Float3 camera,
        Float3 right,
        Float3 up,
        Float3 forward,
        float focal,
        out Float3 screen)
    {
        Float3 rel = world - camera;
        float z = Float3.Dot(rel, forward);
        if (z < 0.4f)
        {
            screen = default;
            return false;
        }

        float x = Float3.Dot(rel, right);
        float y = Float3.Dot(rel, up);
        screen = new Float3(
            Width * 0.5f + x * focal / z,
            Height * 0.5f - y * focal / z,
            z);
        return true;
    }

    private static void FillTexturedTriangle(
        Float3 a,
        Float3 b,
        Float3 c,
        Float2 uv0,
        Float2 uv1,
        Float2 uv2,
        VoxelAtlas atlas,
        int slice,
        float lambert,
        byte[] rgba,
        float[] depth)
    {
        int minX = Math.Clamp((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0, Width - 1);
        int maxX = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))), 0, Width - 1);
        int minY = Math.Clamp((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), 0, Height - 1);
        int maxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), 0, Height - 1);
        float area = Edge(a, b, c);
        if (MathF.Abs(area) < 1e-3f)
            return;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                var p = new Float3(x + 0.5f, y + 0.5f, 0f);
                float w0 = Edge(b, c, p) / area;
                float w1 = Edge(c, a, p) / area;
                float w2 = Edge(a, b, p) / area;
                if (w0 < 0f || w1 < 0f || w2 < 0f)
                    continue;
                float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                int di = y * Width + x;
                if (z >= depth[di])
                    continue;
                depth[di] = z;
                float u = w0 * uv0.X + w1 * uv1.X + w2 * uv2.X;
                float v = w0 * uv0.Y + w1 * uv1.Y + w2 * uv2.Y;
                Color tex = atlas.SampleLayer(slice, u, v);
                int o = di * 4;
                rgba[o] = (byte)Math.Clamp((int)(tex.R * 255f * lambert), 0, 255);
                rgba[o + 1] = (byte)Math.Clamp((int)(tex.G * 255f * lambert), 0, 255);
                rgba[o + 2] = (byte)Math.Clamp((int)(tex.B * 255f * lambert), 0, 255);
                rgba[o + 3] = 255;
            }
        }
    }

    private static float Edge(Float3 a, Float3 b, Float3 c) =>
        (c.X - a.X) * (b.Y - a.Y) - (c.Y - a.Y) * (b.X - a.X);

    private static void FillSky(byte[] rgba)
    {
        for (int y = 0; y < Height; y++)
        {
            float t = y / (float)(Height - 1);
            byte r = (byte)(110 + (210 - 110) * t);
            byte g = (byte)(168 + (228 - 168) * t);
            byte b = (byte)(228 + (245 - 228) * t);
            for (int x = 0; x < Width; x++)
            {
                int o = (y * Width + x) * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = 255;
            }
        }
    }

    private static void AssertLooksLikeVoxelTerrain(
        Shot shot,
        bool expectGrass,
        bool expectDirt,
        bool expectWater,
        bool expectCloud)
    {
        Assert.True(File.Exists(shot.Path));
        Assert.True(new FileInfo(shot.Path).Length > 8000);
        byte[] png = File.ReadAllBytes(shot.Path);
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        if (expectGrass)
            Assert.True(CountNear(shot.Rgba, 76, 168, 78, 55) > 80, "Missing grass texels.");
        if (expectDirt)
            Assert.True(CountNear(shot.Rgba, 141, 108, 86, 55) > 40, "Missing dirt texels.");
        if (expectWater)
            Assert.True(CountWaterPixels(shot.Rgba) > 120, $"Missing water texels ({CountWaterPixels(shot.Rgba)}).");
        if (expectCloud)
            Assert.True(CountCloudPixels(shot.Rgba) > 60, $"Missing cloud texels ({CountCloudPixels(shot.Rgba)}).");
    }

    private static int CountWaterPixels(byte[] rgba)
    {
        int n = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            byte r = rgba[i];
            byte g = rgba[i + 1];
            byte b = rgba[i + 2];
            if (r < 95 && g > 35 && g < 185 && b > g + 12 && b > r + 35 && b < 230)
                n++;
        }

        return n;
    }

    private static int CountCloudPixels(byte[] rgba)
    {
        int n = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            byte r = rgba[i];
            byte g = rgba[i + 1];
            byte b = rgba[i + 2];
            if (r >= 100 && g >= 100 && b >= 100 &&
                Math.Abs(r - g) < 28 && Math.Abs(g - b) < 28 && Math.Abs(r - b) < 32 &&
                (b - r) < 30 &&
                !(b >= 200 && (b - r) >= 28) &&
                !(g > r + 18 && g > b))
                n++;
        }

        return n;
    }

    private static int CountNear(byte[] rgba, byte r, byte g, byte b, int tol)
    {
        int n = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (Math.Abs(rgba[i] - r) <= tol &&
                Math.Abs(rgba[i + 1] - g) <= tol &&
                Math.Abs(rgba[i + 2] - b) <= tol)
                n++;
        }

        return n;
    }

    private sealed class Shot
    {
        public string Path = "";
        public byte[] Rgba = [];
        public int WaterVoxels;
        public int CloudVoxels;
        public Float3 WaterLookAt;
        public Float3 CloudLookAt;
        public bool HasSurfaceWater;
    }

    private static void WritePng(string path, byte[] rgba, int width, int height)
    {
        using var memory = new MemoryStream();
        memory.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] ihdr = new byte[13];
        WriteBe(ihdr, 0, width);
        WriteBe(ihdr, 4, height);
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
            compressor.Write(raw);
        uint adler = Adler32(raw);
        WriteBe(deflate, (int)adler);
        WriteChunk(memory, "IDAT", deflate.ToArray());
        WriteChunk(memory, "IEND", []);
        File.WriteAllBytes(path, memory.ToArray());
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

    private static string FindRepositoryRoot()
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
}
