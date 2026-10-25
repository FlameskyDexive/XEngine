using System;
using XEngine.Vector;
using XEngine.VoxelWorld;
using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelRepairRegressionTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public VoxelRepairRegressionTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public void HudChildDoesNotSuppressCharacterAssembly()
    {
        var player = new XEngine.Runtime.GameObject("Player with HUD");
        new XEngine.Runtime.GameObject("HUD").SetParent(player, false);
        var view = player.AddComponent<VoxelCharacterView>();
        view.Start();
        Assert.Equal(7, player.ChildCount);
        view.Start();
        Assert.Equal(7, player.ChildCount);
    }

    [Fact]
    public void EmptyPresentationIsNotRemeshedEveryTick()
    {
        var streamer = new VoxelChunkStreamer { ViewDistance = 0, MaxChunkY = 0,
            Parent = new XEngine.Runtime.GameObject("Empty terrain test"), EnableColliders = false,
            Generator = new FlatTerrainGenerator { Altitude = 0 } };
        streamer.SetFocus(new Float3(8, 8, 8));
        streamer.Tick(1);
        int builds = streamer.MeshBuildCount;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long allocated = GC.GetTotalAllocatedBytes(true);
        for (int i = 0; i < 120; i++) streamer.Tick(0);
        long candidateBytes = GC.GetTotalAllocatedBytes(true) - allocated;
        double candidateMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Assert.Equal(1, builds);
        Assert.Equal(builds, streamer.MeshBuildCount);
        Assert.True(streamer.Grid.TryGetChunk(Int3.Zero, out var empty));
        // The old RefreshView rebuilt an empty chunk every frame because no view
        // existed. Measure precisely that eliminated work, not end-to-end FPS.
        empty.RebuildMesh();
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        allocated = GC.GetTotalAllocatedBytes(true);
        for (int i = 0; i < 120; i++) empty.RebuildMesh();
        long legacyBytes = GC.GetTotalAllocatedBytes(true) - allocated;
        double legacyMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _output.WriteLine($"120 empty-chunk frames: legacy={legacyMs:F3}ms/{legacyBytes}B, cached={candidateMs:F3}ms/{candidateBytes}B");
        Assert.True(candidateBytes < legacyBytes);
        streamer.UnloadAll();
    }
    [Fact]
    public void AllSixFacesHaveOutwardTriangleWinding()
    {
        var cells = new Voxel[VoxelCoords.ChunkVoxelCount];
        cells[VoxelCoords.Index(3, 4, 5)] = Voxel.Solid(1);
        var mesh = VoxelGreedyMesher.Build(cells);
        Assert.Equal(6, mesh.QuadCount);
        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            int a = (int)mesh.Indices[i], b = (int)mesh.Indices[i + 1], c = (int)mesh.Indices[i + 2];
            Assert.True(Float3.Dot(Float3.Cross(mesh.Vertices[b] - mesh.Vertices[a], mesh.Vertices[c] - mesh.Vertices[a]), mesh.Normals[a]) > 0);
        }
    }

    [Fact]
    public void RectangularGreedyFacesPreserveTextureScaleWhenWindingFlips()
    {
        var cells = new Voxel[VoxelCoords.ChunkVoxelCount];
        for (int x = 0; x < 8; x++)
            for (int y = 0; y < 3; y++)
                for (int z = 0; z < 2; z++) cells[VoxelCoords.Index(x, y, z)] = Voxel.Solid(1);
        var mesh = VoxelGreedyMesher.Build(cells);
        Assert.Equal(6, mesh.QuadCount);
        for (int i = 0; i < mesh.Vertices.Count; i += 4)
        {
            Assert.Equal(Float3.Length(mesh.Vertices[i + 1] - mesh.Vertices[i]), mesh.UV[i + 1].X);
            Assert.Equal(Float3.Length(mesh.Vertices[i + 3] - mesh.Vertices[i]), mesh.UV[i + 3].Y);
        }
    }

    [Fact]
    public void BoundaryEditAndUnloadInvalidateRemainingNeighbor()
    {
        var grid = new VoxelWorldGrid { ViewDistance = 1, MaxChunkY = 0 };
        grid.SetFocus(new Float3(8, 8, 8));
        while (grid.LoadedCount < 9) grid.Tick(32);
        Assert.True(grid.TryGetChunk(new Int3(1, 0, 0), out var neighbor));
        neighbor.Dirty = false;
        grid.TrySetVoxel(new Int3(15, 4, 8), Voxel.Empty);
        Assert.True(neighbor.Dirty);
        Assert.True(grid.TryGetChunk(Int3.Zero, out var center));
        center.Dirty = false;
        grid.SetFocus(new Float3(-8, 8, 8));
        grid.Tick(0);
        Assert.True(center.Dirty);
        grid.UnloadAll();
    }

    [Fact]
    public void SweptVoxelCollisionStopsFastFallsWallsAndUnloadedBorders()
    {
        var grid = new VoxelWorldGrid { ViewDistance = 0, MaxChunkY = 0,
            Generator = new FlatTerrainGenerator { Altitude = 4 } };
        grid.SetFocus(new Float3(8, 8, 8));
        grid.Tick(1);
        var landed = VoxelCharacterCollision.Move(grid, new Float3(8, 14, 8), new Float3(0, -30, 0), .4f, 1.8f, out bool grounded);
        Assert.True(grounded);
        Assert.InRange(landed.Y, 4, 4.01f);
        var edge = VoxelCharacterCollision.Move(grid, landed, new Float3(40, 0, 0), .4f, 1.8f, out _);
        Assert.InRange(edge.X, 15.59f, 15.61f);
        grid.TrySetVoxel(new Int3(10, 4, 8), Voxel.Solid(1));
        var wall = VoxelCharacterCollision.Move(grid, landed, new Float3(4, 0, 0), .4f, 1.8f, out _);
        Assert.InRange(wall.X, 9.59f, 9.61f);
        grid.UnloadAll();
    }
}
