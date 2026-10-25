// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;

using XEngine.Runtime;
using XEngine.Runtime.Navigation;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Navigation;

public sealed class NavMeshExportTests : RuntimeTestBase
{
    public override void Dispose()
    {
        NavMesh.RemoveAllNavMeshData();
        base.Dispose();
    }

    [Fact]
    public void Export_ObjJsonBin_AndPreviewImage()
    {
        NavMeshData data = NavMeshBuilder.Build(CreatePlane(20f), CreatePlaneIndices(), new NavMeshBuildSettings
        {
            TileSize = 0,
            CellSize = 0.3f,
            CellHeight = 0.2f,
            AgentRadius = 0.4f,
            AgentHeight = 2f,
            AgentMaxClimb = 0.4f,
            AgentMaxSlope = 45f,
        });
        NavMesh.AddNavMeshData(data);

        NavMeshTriangulation tri = NavMeshGeometry.Extract(data.RuntimeMesh);
        Assert.True(tri.Vertices.Length >= 3);
        Assert.True(tri.Indices.Length >= 3);
        Assert.Equal(tri.Indices.Length / 3, tri.Areas.Length);

        string dir = Path.Combine(Path.GetTempPath(), "xengine-navmesh-m2");
        Directory.CreateDirectory(dir);
        string obj = Path.Combine(dir, "plane.obj");
        string json = Path.Combine(dir, "plane.json");
        string bin = Path.Combine(dir, "plane.bin");
        NavMeshExport.WriteObj(data, obj);
        NavMeshExport.WriteJson(data, json);
        NavMeshExport.WriteBin(data, bin);

        string objText = File.ReadAllText(obj);
        Assert.Contains("\nv ", objText);
        Assert.Contains("\nf ", objText);
        string jsonText = File.ReadAllText(json);
        Assert.Contains("\"triangleCount\"", jsonText);
        Assert.Contains("\"vertices\"", jsonText);
        Assert.True(new FileInfo(bin).Length > 0);
        Assert.Equal(data.TileData.Length, new FileInfo(bin).Length);

        var path = new NavMeshPath();
        Assert.True(NavMesh.CalculatePath(new Float3(2f, 0f, 2f), new Float3(18f, 0f, 18f), path));
        Float3[] corners = path.Corners.ToArray();

        string preview = Path.Combine(dir, "preview.bmp");
        NavMeshPreviewImage.WriteTopDownBmp(preview, tri, corners, 640, 640);
        Assert.True(File.Exists(preview));
        Assert.True(new FileInfo(preview).Length > 100);
    }

    [Fact]
    public void CollectFilter_SkipsIgnoredObjects()
    {
        Scene scene = CreateScene(enable: true);
        GameObject kept = CreateGameObject("Keep");
        kept.IsStatic = true;
        var keptRenderer = kept.AddComponent<MeshRenderer>();
        keptRenderer.Mesh = new AssetRef<Mesh>(new Mesh { Vertices = CreatePlane(8f), Indices = CreatePlaneIndices() });
        scene.Add(kept);

        GameObject skipped = CreateGameObject("Skip");
        skipped.IsStatic = true;
        var skippedRenderer = skipped.AddComponent<MeshRenderer>();
        skippedRenderer.Mesh = new AssetRef<Mesh>(new Mesh { Vertices = CreatePlane(8f), Indices = CreatePlaneIndices() });
        skipped.Transform.Position = new Float3(50f, 0f, 50f);
        scene.Add(skipped);

        NavMeshInputGeometry geom = NavMeshBuilder.Collect(scene, new NavMeshBuildSettings { CollectStaticOnly = true, TileSize = 0 },
            LayerMask.Everything, go => go.Name != "Skip");
        Assert.Equal(2, geom.TriangleCount);
    }

    private static Float3[] CreatePlane(float size) =>
    [
        new(0f, 0f, 0f),
        new(size, 0f, 0f),
        new(size, 0f, size),
        new(0f, 0f, size),
    ];

    private static uint[] CreatePlaneIndices() => [0, 1, 2, 0, 2, 3];
}

internal static class NavMeshPreviewImage
{
    public static void WriteTopDownPng(string path, NavMeshTriangulation tri, Float3[] pathCorners, int width, int height,
        Float3[]? trail = null, Float3? agent = null, Float3? destination = null, AABB? obstacle = null)
    {
        byte[] bgr = RenderTopDownBgr(tri, pathCorners, width, height, trail, agent, destination, obstacle);
        byte[] rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            int s = i * 3;
            int d = i * 4;
            rgba[d] = bgr[s + 2];
            rgba[d + 1] = bgr[s + 1];
            rgba[d + 2] = bgr[s];
            rgba[d + 3] = 255;
        }

        File.WriteAllBytes(path, XGuiAcceptanceScreenshot.EncodeRgbaPng(rgba, width, height));
    }

    public static void WriteTopDownBmp(string path, NavMeshTriangulation tri, Float3[] pathCorners, int width, int height,
        Float3[]? trail = null, Float3? agent = null, Float3? destination = null, AABB? obstacle = null)
    {
        byte[] pixels = RenderTopDownBgr(tri, pathCorners, width, height, trail, agent, destination, obstacle);
        WriteBmp(path, pixels, width, height);
    }

    private static byte[] RenderTopDownBgr(NavMeshTriangulation tri, Float3[] pathCorners, int width, int height,
        Float3[]? trail, Float3? agent, Float3? destination, AABB? obstacle = null)
    {
        byte[] pixels = new byte[width * height * 3];
        Fill(pixels, width, height, 18, 22, 30);

        AABB b = tri.Bounds;
        float minX = b.Min.X, maxX = b.Max.X, minZ = b.Min.Z, maxZ = b.Max.Z;
        if (trail != null)
        {
            for (int i = 0; i < trail.Length; i++)
            {
                minX = Math.Min(minX, trail[i].X);
                maxX = Math.Max(maxX, trail[i].X);
                minZ = Math.Min(minZ, trail[i].Z);
                maxZ = Math.Max(maxZ, trail[i].Z);
            }
        }

        float spanX = Math.Max(1e-3f, maxX - minX);
        float spanZ = Math.Max(1e-3f, maxZ - minZ);
        float pad = 0.08f;

        int ProjectX(float x) => (int)(((x - minX) / spanX * (1f - pad * 2f) + pad) * (width - 1));
        int ProjectZ(float z) => (int)(((z - minZ) / spanZ * (1f - pad * 2f) + pad) * (height - 1));

        int triCount = tri.Indices.Length / 3;
        for (int i = 0; i < triCount; i++)
        {
            int i0 = tri.Indices[i * 3], i1 = tri.Indices[i * 3 + 1], i2 = tri.Indices[i * 3 + 2];
            Float3 a = tri.Vertices[i0], c = tri.Vertices[i1], d = tri.Vertices[i2];
            int area = i < tri.Areas.Length ? tri.Areas[i] : NavMeshAreas.Walkable;
            Color col = NavMeshGeometry.AreaColor(area, 1f);
            FillTriangle(pixels, width, height,
                ProjectX(a.X), ProjectZ(a.Z),
                ProjectX(c.X), ProjectZ(c.Z),
                ProjectX(d.X), ProjectZ(d.Z),
                (byte)(col.R * 255), (byte)(col.G * 255), (byte)(col.B * 255));
        }

        if (obstacle is AABB box)
        {
            FillTriangle(pixels, width, height,
                ProjectX(box.Min.X), ProjectZ(box.Min.Z),
                ProjectX(box.Max.X), ProjectZ(box.Min.Z),
                ProjectX(box.Max.X), ProjectZ(box.Max.Z),
                220, 50, 40);
            FillTriangle(pixels, width, height,
                ProjectX(box.Min.X), ProjectZ(box.Min.Z),
                ProjectX(box.Max.X), ProjectZ(box.Max.Z),
                ProjectX(box.Min.X), ProjectZ(box.Max.Z),
                220, 50, 40);
        }

        for (int i = 1; i < pathCorners.Length; i++)
        {
            DrawLine(pixels, width, height,
                ProjectX(pathCorners[i - 1].X), ProjectZ(pathCorners[i - 1].Z),
                ProjectX(pathCorners[i].X), ProjectZ(pathCorners[i].Z),
                255, 220, 40);
        }

        if (trail != null)
        {
            for (int i = 1; i < trail.Length; i++)
            {
                DrawLine(pixels, width, height,
                    ProjectX(trail[i - 1].X), ProjectZ(trail[i - 1].Z),
                    ProjectX(trail[i].X), ProjectZ(trail[i].Z),
                    255, 90, 200);
            }
        }

        if (destination is Float3 dest)
            DrawDot(pixels, width, height, ProjectX(dest.X), ProjectZ(dest.Z), 5, 255, 70, 70);
        if (pathCorners.Length > 0)
            DrawDot(pixels, width, height, ProjectX(pathCorners[0].X), ProjectZ(pathCorners[0].Z), 4, 80, 220, 120);
        if (agent is Float3 pos)
            DrawDot(pixels, width, height, ProjectX(pos.X), ProjectZ(pos.Z), 6, 255, 255, 255);

        return pixels;
    }

    private static void DrawDot(byte[] px, int w, int h, int cx, int cy, int radius, byte r, byte g, byte b)
    {
        int r2 = radius * radius;
        for (int y = cy - radius; y <= cy + radius; y++)
        {
            for (int x = cx - radius; x <= cx + radius; x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > r2) continue;
                if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
                int o = (y * w + x) * 3;
                px[o] = b;
                px[o + 1] = g;
                px[o + 2] = r;
            }
        }
    }

    private static void Fill(byte[] px, int w, int h, byte r, byte g, byte b)
    {
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 3;
            px[o] = b;
            px[o + 1] = g;
            px[o + 2] = r;
        }
    }

    private static void FillTriangle(byte[] px, int w, int h, int x0, int y0, int x1, int y1, int x2, int y2, byte r, byte g, byte b)
    {
        int minX = Math.Clamp(Math.Min(x0, Math.Min(x1, x2)), 0, w - 1);
        int maxX = Math.Clamp(Math.Max(x0, Math.Max(x1, x2)), 0, w - 1);
        int minY = Math.Clamp(Math.Min(y0, Math.Min(y1, y2)), 0, h - 1);
        int maxY = Math.Clamp(Math.Max(y0, Math.Max(y1, y2)), 0, h - 1);
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!Inside(x, y, x0, y0, x1, y1, x2, y2)) continue;
                int o = (y * w + x) * 3;
                px[o] = b;
                px[o + 1] = g;
                px[o + 2] = r;
            }
        }
    }

    private static bool Inside(int x, int y, int x0, int y0, int x1, int y1, int x2, int y2)
    {
        float d0 = Edge(x, y, x0, y0, x1, y1);
        float d1 = Edge(x, y, x1, y1, x2, y2);
        float d2 = Edge(x, y, x2, y2, x0, y0);
        bool hasNeg = d0 < 0 || d1 < 0 || d2 < 0;
        bool hasPos = d0 > 0 || d1 > 0 || d2 > 0;
        return !(hasNeg && hasPos);
    }

    private static float Edge(int x, int y, int x0, int y0, int x1, int y1)
        => (x - x0) * (y1 - y0) - (y - y0) * (x1 - x0);

    private static void DrawLine(byte[] px, int w, int h, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            if ((uint)x0 < (uint)w && (uint)y0 < (uint)h)
            {
                int o = (y0 * w + x0) * 3;
                px[o] = b; px[o + 1] = g; px[o + 2] = r;
            }
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private static void WriteBmp(string path, byte[] bgr, int w, int h)
    {
        int rowStride = ((w * 3 + 3) / 4) * 4;
        int pixelSize = rowStride * h;
        byte[] file = new byte[14 + 40 + pixelSize];
        file[0] = (byte)'B'; file[1] = (byte)'M';
        WriteInt(file, 2, file.Length);
        WriteInt(file, 10, 54);
        WriteInt(file, 14, 40);
        WriteInt(file, 18, w);
        WriteInt(file, 22, h);
        file[26] = 1;
        file[28] = 24;
        WriteInt(file, 34, pixelSize);
        for (int y = 0; y < h; y++)
        {
            int srcY = h - 1 - y;
            int dest = 54 + y * rowStride;
            Buffer.BlockCopy(bgr, srcY * w * 3, file, dest, w * 3);
        }

        File.WriteAllBytes(path, file);
    }

    private static void WriteInt(byte[] d, int o, int v)
    {
        d[o] = (byte)v;
        d[o + 1] = (byte)(v >> 8);
        d[o + 2] = (byte)(v >> 16);
        d[o + 3] = (byte)(v >> 24);
    }
}
