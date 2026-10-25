// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Navigation;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Navigation;

public sealed class NavMeshDynamicTests : RuntimeTestBase
{
    public override void Dispose()
    {
        NavMesh.RemoveAllNavMeshData();
        base.Dispose();
    }

    [Fact]
    public void ObstacleCarve_MakesStraightPathDetour()
    {
        NavMeshData data = NavMeshBuilder.Build(CreatePlane(20f), CreatePlaneIndices(), CarveSettings());
        Assert.True(data.TileCacheData is { Length: > 0 });
        NavMesh.AddNavMeshData(data);

        var open = new NavMeshPath();
        Assert.True(NavMesh.CalculatePath(new Float3(2f, 0f, 2f), new Float3(18f, 0f, 18f), open));
        Assert.True(PathPassesNear(open, new Float3(10f, 0f, 10f), 2.5f));

        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Block");
        go.Transform.Position = new Float3(10f, 0f, 10f);
        var obstacle = go.AddComponent<NavMeshObstacle>();
        obstacle.Shape = NavMeshObstacleShape.Box;
        obstacle.Carve = NavMeshObstacleCarveMode.Carve;
        obstacle.Size = new Float3(4f, 2f, 4f);
        scene.Add(go);
        obstacle.ApplyCarve();

        var detour = new NavMeshPath();
        Assert.True(NavMesh.CalculatePath(new Float3(2f, 0f, 2f), new Float3(18f, 0f, 18f), detour),
            "path should still exist around the carved hole");
        Assert.False(PathPassesNear(detour, new Float3(10f, 0f, 10f), 1.2f),
            $"carved path should avoid the obstacle; corners={Dump(detour)}");
        Assert.True(detour.CornerCount >= 2);
        Assert.InRange(detour[detour.CornerCount - 1].X, 16f, 20f);

        NavMeshTriangulation tri = NavMeshGeometry.Extract(data.RuntimeMesh);
        string png = Path.Combine(FindEvidenceDir(), "2026-08-15-m4-dynamic.png");
        NavMeshPreviewImage.WriteTopDownPng(png, tri, detour.Corners.ToArray(), 640, 640,
            trail: open.Corners.ToArray(), destination: new Float3(18f, 0f, 18f),
            obstacle: obstacle.WorldBounds);
        Assert.True(File.Exists(png));
        Assert.True(new FileInfo(png).Length > 200);
    }

    [Fact]
    public void UpdateNavMesh_RebuildsDirtyTiles_AroundNewBlocker()
    {
        Scene scene = CreateScene(enable: true);
        AddStaticPlane(scene, "Floor", Float3.Zero, 20f);

        var settings = new NavMeshBuildSettings
        {
            TileSize = 32,
            CellSize = 0.3f,
            CellHeight = 0.2f,
            AgentRadius = 0.4f,
            AgentHeight = 2f,
            AgentMaxClimb = 0.4f,
            AgentMaxSlope = 45f,
            CollectStaticOnly = true,
            EnableCarving = false,
        };

        NavMeshData data = NavMesh.BuildNavMesh(scene, settings);
        NavMesh.AddNavMeshData(data);
        var open = new NavMeshPath();
        Assert.True(NavMesh.CalculatePath(new Float3(2f, 0f, 2f), new Float3(18f, 0f, 18f), open));

        AddStaticBox(scene, "Blocker", new Float3(10f, 1f, 10f), 4f);
        NavMesh.UpdateNavMesh(scene, data, new AABB(new Float3(7f, -1f, 7f), new Float3(13f, 3f, 13f)));

        var detour = new NavMeshPath();
        Assert.True(NavMesh.CalculatePath(new Float3(2f, 0f, 2f), new Float3(18f, 0f, 18f), detour));
        Assert.False(PathPassesNear(detour, new Float3(10f, 0f, 10f), 1.2f),
            $"dirty-tile rebuild should route around the blocker; corners={Dump(detour)}");
    }

    private void AddStaticPlane(Scene scene, string name, Float3 position, float size)
    {
        GameObject go = CreateGameObject(name);
        go.IsStatic = true;
        go.Transform.Position = position;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.Mesh = new AssetRef<Mesh>(new Mesh { Vertices = CreatePlane(size), Indices = CreatePlaneIndices() });
        scene.Add(go);
    }

    private void AddStaticBox(Scene scene, string name, Float3 center, float size)
    {
        GameObject go = CreateGameObject(name);
        go.IsStatic = true;
        go.Transform.Position = center;
        float h = size * 0.5f;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.Mesh = new AssetRef<Mesh>(new Mesh
        {
            Vertices =
            [
                new(-h, -h, -h), new(h, -h, -h), new(h, -h, h), new(-h, -h, h),
                new(-h, h, -h), new(h, h, -h), new(h, h, h), new(-h, h, h),
            ],
            Indices =
            [
                0, 1, 2, 0, 2, 3,
                4, 6, 5, 4, 7, 6,
                0, 4, 5, 0, 5, 1,
                3, 2, 6, 3, 6, 7,
                0, 3, 7, 0, 7, 4,
                1, 5, 6, 1, 6, 2,
            ],
        });
        scene.Add(go);
    }

    private static NavMeshBuildSettings CarveSettings() => new()
    {
        TileSize = 48,
        CellSize = 0.3f,
        CellHeight = 0.2f,
        AgentRadius = 0.4f,
        AgentHeight = 2f,
        AgentMaxClimb = 0.4f,
        AgentMaxSlope = 45f,
        EnableCarving = true,
    };

    private static Float3[] CreatePlane(float size) =>
    [
        new(0f, 0f, 0f),
        new(size, 0f, 0f),
        new(size, 0f, size),
        new(0f, 0f, size),
    ];

    private static uint[] CreatePlaneIndices() => [0, 1, 2, 0, 2, 3];

    private static bool PathPassesNear(NavMeshPath path, Float3 point, float radius)
    {
        for (int i = 0; i < path.CornerCount; i++)
        {
            float dx = path[i].X - point.X;
            float dz = path[i].Z - point.Z;
            if (dx * dx + dz * dz <= radius * radius)
                return true;
        }

        for (int i = 1; i < path.CornerCount; i++)
        {
            if (SegmentDistance(path[i - 1], path[i], point) <= radius)
                return true;
        }

        return false;
    }

    private static float SegmentDistance(Float3 a, Float3 b, Float3 p)
    {
        Float3 ab = b - a;
        ab.Y = 0f;
        Float3 ap = p - a;
        ap.Y = 0f;
        float denom = ab.X * ab.X + ab.Z * ab.Z;
        float t = denom < 1e-8f ? 0f : Math.Clamp((ap.X * ab.X + ap.Z * ab.Z) / denom, 0f, 1f);
        float dx = a.X + ab.X * t - p.X;
        float dz = a.Z + ab.Z * t - p.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static string Dump(NavMeshPath path)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < path.CornerCount; i++)
        {
            if (i > 0) sb.Append(" -> ");
            sb.Append(path[i]);
        }

        return sb.ToString();
    }

    private static string FindEvidenceDir()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
            {
                string dir = Path.Combine(current.FullName, "docs", "superpowers", "evidence", "navigation");
                Directory.CreateDirectory(dir);
                return dir;
            }

            current = current.Parent;
        }

        string fallback = Path.Combine(Path.GetTempPath(), "xengine-navmesh-m4");
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
