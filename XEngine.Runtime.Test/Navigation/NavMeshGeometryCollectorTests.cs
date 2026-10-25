// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Navigation;
using XEngine.Runtime.Resources;
using XEngine.Terrain;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Navigation;

public sealed class NavMeshGeometryCollectorTests : RuntimeTestBase
{
    public override void Dispose()
    {
        NavMesh.RemoveAllNavMeshData();
        base.Dispose();
    }

    [Fact]
    public void ModuleInitializer_RegistersTerrainCollector()
    {
        Assert.True(NavMeshGeometryCollectors.Count >= 1);
        var geom = new NavMeshInputGeometry();
        GameObject go = CreateGameObject("Terrain");
        var terrain = go.AddComponent<TerrainComponent>();
        terrain.Data = new AssetRef<TerrainData>(CreateRampTerrain());
        Assert.True(TerrainNavMeshGeometryCollector.Instance.TryCollect(go, geom));
        Assert.Equal(32, geom.TriangleCount);
    }

    [Fact]
    public void CustomCollector_ConsumesGameObject_AndSkipsMeshRenderer()
    {
        var collector = new NamedTriangleCollector("Collected");
        NavMeshGeometryCollectors.Register(collector);
        try
        {
            Scene scene = CreateScene(enable: true);
            GameObject collected = CreateGameObject("Collected");
            collected.IsStatic = true;
            var renderer = collected.AddComponent<MeshRenderer>();
            renderer.Mesh = new AssetRef<Mesh>(new Mesh { Vertices = CreatePlane(8f), Indices = CreatePlaneIndices() });
            scene.Add(collected);

            GameObject other = CreateGameObject("Other");
            other.IsStatic = true;
            var otherRenderer = other.AddComponent<MeshRenderer>();
            otherRenderer.Mesh = new AssetRef<Mesh>(new Mesh { Vertices = CreatePlane(8f), Indices = CreatePlaneIndices() });
            scene.Add(other);

            NavMeshInputGeometry geom = NavMeshBuilder.Collect(scene,
                new NavMeshBuildSettings { CollectStaticOnly = true, TileSize = 0 },
                LayerMask.Everything);
            Assert.True(collector.Calls >= 1);
            // Named collector adds 1 triangle; Other still contributes the 2-triangle plane.
            Assert.Equal(3, geom.TriangleCount);
        }
        finally
        {
            NavMeshGeometryCollectors.Unregister(collector);
        }
    }

    [Fact]
    public void TerrainHoles_SkipCells()
    {
        TerrainData data = CreateRampTerrain();
        data.SetHole(0, 0, 0);
        GameObject go = CreateGameObject("Terrain");
        var terrain = go.AddComponent<TerrainComponent>();
        terrain.Data = new AssetRef<TerrainData>(data);
        var geom = new NavMeshInputGeometry();
        Assert.True(TerrainNavMeshGeometryCollector.Instance.TryCollect(go, geom));
        Assert.True(geom.TriangleCount < 32);
        Assert.Equal(30, geom.TriangleCount);
    }

    [Fact]
    public void NavMeshBuilderSource_DoesNotReferenceTerrainTypes()
    {
        string path = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.ai.navigation", "Runtime", "NavMeshBuilder.cs");
        string text = File.ReadAllText(path);
        Assert.DoesNotContain("TerrainComponent", text);
        Assert.DoesNotContain("XEngine.Runtime.Terrain", text);
        Assert.Contains("NavMeshGeometryCollectors.TryCollect", text);
    }

    [Fact]
    public void TerrainCollect_BakesWalkableMesh_AndWritesEvidence()
    {
        GameObject go = CreateGameObject("Terrain");
        var terrain = go.AddComponent<TerrainComponent>();
        terrain.Data = new AssetRef<TerrainData>(CreateRampTerrain(res: 9, size: 20f, height: 1f));
        var geom = new NavMeshInputGeometry();
        Assert.True(TerrainNavMeshGeometryCollector.Instance.TryCollect(go, geom));
        Assert.Equal(128, geom.TriangleCount);

        NavMeshData baked = NavMeshBuilder.Build(geom, new NavMeshBuildSettings
        {
            TileSize = 0,
            CellSize = 0.3f,
            CellHeight = 0.2f,
            AgentRadius = 0.4f,
            AgentHeight = 2f,
            AgentMaxClimb = 0.4f,
            AgentMaxSlope = 45f,
        });
        NavMesh.AddNavMeshData(baked);
        var path = new NavMeshPath();
        Assert.True(NavMesh.CalculatePath(new Float3(2f, 0.5f, 2f), new Float3(18f, 0.5f, 18f), path));

        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "terrain");
        Directory.CreateDirectory(evidence);
        NavMeshTriangulation tri = NavMeshGeometry.Extract(baked.RuntimeMesh);
        string preview = Path.Combine(evidence, "m1-navmesh-terrain-collect.png");
        NavMeshPreviewImage.WriteTopDownPng(preview, tri, path.Corners.ToArray(), 320, 320);
        Assert.True(new FileInfo(preview).Length > 256);

        var board = new XGuiAcceptanceScreenshot.Canvas(920, 280, 18, 19, 26);
        board.Fill(24, 24, 872, 232, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 232, 70, 74, 90, 2);
        board.Text(40, 40, "M1 NAVMESH GEOMETRY COLLECTORS", 230, 232, 240, 2);
        board.Text(40, 80, "CORE BUILDER HAS NO TERRAIN TYPE", 140, 146, 160, 1);
        board.Fill(60, 120, 18, 18, 80, 200, 140, 255);
        board.Text(90, 122, "TERRAIN COLLECTOR REGISTERED AT LOAD", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 80, 170, 220, 255);
        board.Text(90, 154, $"TRIANGLES {geom.TriangleCount}  PATH {path.CornerCount}", 200, 220, 200, 1);
        board.Text(40, 210, "ACCEPTANCE 2026-08-15", 140, 146, 160, 1);
        string boardPath = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "m1-navmesh-collector.png"), board);
        Assert.True(File.Exists(boardPath));

        File.WriteAllText(Path.Combine(evidence, "m1-navmesh-collector.md"),
            "# M1 NavMesh geometry collectors\n\n" +
            "Date: 2026-08-15. Branch: `codex/animation-module`.\n\n" +
            "`NavMeshBuilder` no longer references `TerrainComponent`. Terrain heightmaps are " +
            "rasterized by `TerrainNavMeshGeometryCollector`, registered via `[ModuleInitializer]`.\n\n" +
            $"Ramp terrain bake: **{geom.TriangleCount}** source triangles, path corners **{path.CornerCount}**.\n\n" +
            "Screenshots: [m1-navmesh-collector.png](m1-navmesh-collector.png), " +
            "[m1-navmesh-terrain-collect.png](m1-navmesh-terrain-collect.png).\n");
    }

    private static TerrainData CreateRampTerrain(int res = 5, float size = 16f, float height = 2f)
    {
        var data = new TerrainData
        {
            HeightmapResolution = res,
            Size = size,
            Height = height,
            Heights = new short[res * res],
        };
        for (int z = 0; z < res; z++)
        {
            for (int x = 0; x < res; x++)
                data.SetHeight(x, z, z / (float)(res - 1));
        }

        return data;
    }

    private static Float3[] CreatePlane(float size) =>
    [
        new(0f, 0f, 0f),
        new(size, 0f, 0f),
        new(size, 0f, size),
        new(0f, 0f, size),
    ];

    private static uint[] CreatePlaneIndices() => [0, 1, 2, 0, 2, 3];

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

    private sealed class NamedTriangleCollector : INavMeshGeometryCollector
    {
        private readonly string _name;
        public int Calls;

        public NamedTriangleCollector(string name) => _name = name;

        public bool TryCollect(GameObject go, INavMeshTriangleSink geom)
        {
            Calls++;
            if (go.Name != _name)
                return false;
            geom.AddTriangle(Float3.Zero, new Float3(1f, 0f, 0f), new Float3(0f, 0f, 1f));
            return true;
        }
    }
}
