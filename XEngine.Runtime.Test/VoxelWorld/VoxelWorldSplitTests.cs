using XEngine.Runtime;
using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelWorldSplitTests
{
    [Fact]
    public void Grid_SetVoxel_DoesNotUploadMesh()
    {
        using var world = new GridWorld();
        world.Load();
        Assert.True(world.Grid.TryGetChunk(Int3.Zero, out VoxelChunk chunk));
        Assert.Null(chunk.Mesh);
        Assert.True(world.Grid.TrySetVoxel(new Int3(8, 15, 8), Voxel.Empty));
        Assert.Equal((ushort)0, chunk.GetVoxel(VoxelCoords.Index(8, 15, 8)).TypeIndex);
        Assert.True(chunk.Edited);
        Assert.Null(chunk.Mesh);
        Assert.True(chunk.Dirty);
    }

    [Fact]
    public void Streamer_WithoutParent_WritesGridOnly()
    {
        using var world = new GridWorld();
        world.Load();
        Assert.True(world.Streamer.TrySetVoxel(new Int3(8, 15, 8), Voxel.Empty));
        Assert.True(world.Grid.TryGetVoxel(new Int3(8, 15, 8), out Voxel hole) && hole.IsEmpty);
        Assert.True(world.Grid.TryGetChunk(Int3.Zero, out VoxelChunk chunk));
        Assert.Null(chunk.Mesh);
    }

    [Fact]
    public void Streamer_WithParent_RemeshesDirtyChunk()
    {
        VoxelAtlas atlas = XEngine.VoxelWorld.VoxelWorld.CreateDemoAtlas();
        var root = new GameObject("VoxelSplitView");
        var streamer = new VoxelChunkStreamer
        {
            ViewDistance = 0,
            MinChunkY = 0,
            MaxChunkY = 0,
            TerrainTypeIndex = 3,
            Generator = new FlatTerrainGenerator { Altitude = 16, SurfaceTypeIndex = 2 },
            Atlas = atlas,
            Material = VoxelShaderLibrary.CreateOpaqueMaterial(atlas),
            Parent = root,
        };
        streamer.SetFocus(new Float3(8f, 8f, 8f));
        Assert.Equal(1, streamer.Tick(8));
        Assert.True(streamer.TrySetVoxel(new Int3(8, 15, 8), Voxel.Empty));
        Assert.True(streamer.Grid.TryGetChunk(Int3.Zero, out VoxelChunk chunk));
        Assert.NotNull(chunk.Mesh);
        Assert.False(chunk.Dirty);
        Assert.Contains(root.GetComponentsInChildren<MeshRenderer>(includeSelf: false), _ => true);
        streamer.UnloadAll();
        EngineObject.Destroy(root);
    }

    [Fact]
    public void Save_Apply_WritesGridWithoutMeshing()
    {
        using var world = new GridWorld();
        world.Load();
        var save = new VoxelWorldSave();
        save.Record(new Int3(8, 15, 8), 0);
        save.Apply(world.Grid);
        Assert.True(world.Grid.TryGetVoxel(new Int3(8, 15, 8), out Voxel after) && after.IsEmpty);
        Assert.True(world.Grid.TryGetChunk(Int3.Zero, out VoxelChunk chunk));
        Assert.Null(chunk.Mesh);
    }

    [Fact]
    public void World_ExposesGridUsedByStreamer()
    {
        using var world = new GridWorld();
        Assert.Same(world.Host.Streamer.Grid, world.Host.Grid);
        world.Load();
        Assert.Equal(world.Streamer.LoadedCount, world.Grid.LoadedCount);
    }

    [Fact]
    public void DummyMob_ViewFollowsLogicState()
    {
        var go = new GameObject("DummyMobLogic");
        go.Transform.Position = new Float3(3f, 16f, 4f);
        var dummy = go.AddComponent<VoxelDummyMob>();
        dummy.Start();
        dummy.State.Position = new Float3(9f, 16f, 5f);
        dummy.Update();
        Assert.Equal(9f, go.Transform.Position.X, 3);
        Assert.Equal(5f, go.Transform.Position.Z, 3);
        EngineObject.Destroy(go);
    }

    private sealed class GridWorld : IDisposable
    {
        public XEngine.VoxelWorld.VoxelWorld Host { get; } = new()
        {
            EnableStreaming = false,
            ViewDistance = 0,
            TerrainTypeIndex = 3,
            TerrainSurfaceTypeIndex = 2,
        };

        public VoxelChunkStreamer Streamer => Host.Streamer;
        public VoxelWorldGrid Grid => Host.Grid;

        public GridWorld()
        {
            Host.ConfigureStreamer();
            Streamer.ViewDistance = 0;
            Streamer.MaxChunkY = 0;
            Streamer.Generator = new FlatTerrainGenerator { Altitude = 16, SurfaceTypeIndex = 2 };
        }

        public void Load()
        {
            Streamer.SetFocus(new Float3(8f, 8f, 8f));
            Streamer.Tick(8);
        }

        public void Dispose() => Streamer.UnloadAll();
    }
}
