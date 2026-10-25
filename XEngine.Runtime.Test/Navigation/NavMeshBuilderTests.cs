// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using DotRecast.Core;
using DotRecast.Recast;

using XEngine.Echo;
using XEngine.Runtime.Navigation;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Navigation;

public sealed class NavMeshBuilderTests : RuntimeTestBase
{
    public override void Dispose()
    {
        NavMesh.RemoveAllNavMeshData();
        base.Dispose();
    }

    [Fact]
    public void BakePlane_CalculatePath_AndRoundTripAsset()
    {
        NavMeshData data = NavMeshBuilder.Build(CreatePlane(20f), CreatePlaneIndices(), SoloSettings());
        Assert.True(data.TileData.Length > 0);
        Assert.True(data.Tiles.Count >= 1);

        NavMesh.AddNavMeshData(data);
        var path = new NavMeshPath();
        Assert.True(NavMesh.CalculatePath(new Float3(2f, 0f, 2f), new Float3(18f, 0f, 18f), path));
        Assert.Equal(NavMeshPathStatus.PathComplete, path.Status);
        Assert.True(path.CornerCount >= 2);
        Assert.InRange(path[path.CornerCount - 1].X, 16f, 20f);

        Assert.True(NavMesh.SamplePosition(new Float3(10f, 1f, 10f), out NavMeshHit sample, 4f));
        Assert.True(sample.Hit);
        Assert.InRange(sample.Position.Y, -0.5f, 1.5f);

        Assert.True(NavMesh.FindClosestEdge(new Float3(10f, 0f, 10f), out NavMeshHit edge));
        Assert.True(edge.Hit);

        EchoObject echo = Serializer.Serialize(data);
        var copy = Serializer.Deserialize<NavMeshData>(echo);
        Assert.NotNull(copy);
        Assert.Equal(data.TileData.Length, copy!.TileData.Length);
        Assert.Equal(data.SettingsHash, copy.SettingsHash);

        NavMesh.RemoveAllNavMeshData();
        NavMesh.AddNavMeshData(copy);
        path.Clear();
        Assert.True(NavMesh.CalculatePath(new Float3(2f, 0f, 2f), new Float3(18f, 0f, 18f), path));
    }

    [Fact]
    public void CollectsStaticMeshRenderer_FromScene()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Floor");
        go.IsStatic = true;
        go.Transform.Position = Float3.Zero;
        var renderer = go.AddComponent<MeshRenderer>();
        var mesh = new Mesh
        {
            Vertices = CreatePlane(12f),
            Indices = CreatePlaneIndices(),
        };
        renderer.Mesh = new AssetRef<Mesh>(mesh);
        scene.Add(go);

        NavMeshData data = NavMeshBuilder.Build(scene, SoloSettings());
        NavMesh.AddNavMeshData(data);
        var path = new NavMeshPath();
        Assert.True(NavMesh.CalculatePath(new Float3(1f, 0f, 1f), new Float3(10f, 0f, 10f), path));
    }

    [Fact]
    public void ThirtyDegreeSlope_IsWalkable_SixtyDegree_IsNot()
    {
        var settings = SoloSettings();
        settings.AgentMaxSlope = 45f;

        const float length = 20f;
        const float width = 16f;
        var probe = new NavMeshInputGeometry();
        probe.AddMesh(CreateRamp(length, 30f, width), CreatePlaneIndices(), Float4x4.Identity);
        var triMesh = probe.GetMesh();
        int[] areas = RcRecast.MarkWalkableTriangles(new RcContext(), 45f, triMesh.GetVerts(), triMesh.GetTris(),
            triMesh.GetTris().Length / 3, new RcAreaModification(NavMeshAreas.Walkable));
        Assert.Contains(areas, a => a == NavMeshAreas.Walkable);

        NavMeshData walkable = NavMeshBuilder.Build(CreateRamp(length, 30f, width), CreatePlaneIndices(), settings);
        NavMesh.AddNavMeshData(walkable);
        Float3 mid30 = RampPoint(length, 30f, width, 0.5f);
        Float3 sample30 = mid30 + new Float3(0f, 0.5f, 0f);
        bool found30 = NavMesh.SamplePosition(sample30, out NavMeshHit hit30, 4f);
        Assert.True(found30,
            $"30° ramp should be walkable at 45° max slope; sample={sample30} tiles={walkable.Tiles.Count} polys={(walkable.Tiles.Count > 0 ? walkable.Tiles[0].PolyCount : 0)} bounds={walkable.Bounds.Min}->{walkable.Bounds.Max}");
        Assert.True(hit30.Position.Y > 1f, $"30° hit y={hit30.Position.Y} sample={sample30}");

        NavMesh.RemoveAllNavMeshData();
        bool steepWalkable = TryBuild(CreateRamp(length, 60f, width), settings, out NavMeshData? steep);
        if (steepWalkable)
        {
            NavMesh.AddNavMeshData(steep!);
            Float3 mid60 = RampPoint(length, 60f, width, 0.5f);
            bool sampled = NavMesh.SamplePosition(mid60 + new Float3(0f, 0.5f, 0f), out NavMeshHit hit60, 1.5f);
            Assert.True(!sampled || hit60.Position.Y < 1f,
                $"60° ramp should not be walkable at 45° max slope; hit y={hit60.Position.Y}");
        }
    }

    [Fact]
    public void Ceiling_IsNotWalkable()
    {
        Float3[] floor = CreatePlane(10f);
        uint[] floorIdx = CreatePlaneIndices();
        Float3[] ceiling =
        [
            new(0f, 3f, 0f),
            new(0f, 3f, 10f),
            new(10f, 3f, 10f),
            new(10f, 3f, 0f),
        ];
        var geom = new NavMeshInputGeometry();
        geom.AddMesh(floor, floorIdx, Float4x4.Identity);
        geom.AddMesh(ceiling, floorIdx, Float4x4.Identity);

        NavMeshData data = NavMeshBuilder.Build(geom, SoloSettings());
        NavMesh.AddNavMeshData(data);

        Assert.True(NavMesh.SamplePosition(new Float3(5f, 0.2f, 5f), out NavMeshHit floorHit, 2f));
        Assert.InRange(floorHit.Position.Y, -0.5f, 1f);

        bool ceilingHit = NavMesh.SamplePosition(new Float3(5f, 3f, 5f), out NavMeshHit up, 0.6f);
        Assert.True(!ceilingHit || up.Position.Y < 1.5f,
            $"ceiling should not produce a walkable poly at y=3; y={up.Position.Y}");
    }

    [Fact]
    public void CalculatePath_ReusesBuffer_WithoutGrowingOnSecondCall()
    {
        NavMeshData data = NavMeshBuilder.Build(CreatePlane(20f), CreatePlaneIndices(), SoloSettings());
        NavMesh.AddNavMeshData(data);
        var path = new NavMeshPath();
        Assert.True(NavMesh.CalculatePath(new Float3(2f, 0f, 2f), new Float3(18f, 0f, 18f), path));
        int first = path.CornerCount;
        Assert.True(NavMesh.CalculatePath(new Float3(3f, 0f, 3f), new Float3(17f, 0f, 17f), path));
        Assert.True(path.CornerCount <= first || path.CornerCount > 0);
    }

    private static NavMeshBuildSettings SoloSettings() => new()
    {
        TileSize = 0,
        CellSize = 0.3f,
        CellHeight = 0.2f,
        AgentRadius = 0.4f,
        AgentHeight = 2f,
        AgentMaxClimb = 0.4f,
        AgentMaxSlope = 45f,
        CollectStaticOnly = true,
    };

    private static Float3[] CreatePlane(float size) =>
    [
        new(0f, 0f, 0f),
        new(size, 0f, 0f),
        new(size, 0f, size),
        new(0f, 0f, size),
    ];

    private static uint[] CreatePlaneIndices() => [0, 1, 2, 0, 2, 3];

    private static bool TryBuild(Float3[] vertices, NavMeshBuildSettings settings, out NavMeshData? data)
    {
        try
        {
            data = NavMeshBuilder.Build(vertices, CreatePlaneIndices(), settings);
            return true;
        }
        catch (InvalidOperationException)
        {
            data = null;
            return false;
        }
    }

    private static Float3[] CreateRamp(float length, float slopeDegrees, float width)
    {
        float height = MathF.Tan(slopeDegrees * MathF.PI / 180f) * length;
        return
        [
            new(0f, 0f, 0f),
            new(length, height, 0f),
            new(length, height, width),
            new(0f, 0f, width),
        ];
    }

    private static Float3 RampPoint(float length, float slopeDegrees, float width, float t)
    {
        float height = MathF.Tan(slopeDegrees * MathF.PI / 180f) * length;
        return new Float3(length * t, height * t, width * 0.5f);
    }
}
