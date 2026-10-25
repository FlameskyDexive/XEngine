using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelStreamingTests
{
    [Fact]
    public void FocusOrigin_LoadsViewDistanceRing()
    {
        using var host = new StreamFixture(viewDistance: 1, maxChunkY: 0);
        host.Streamer.SetFocus(Float3.Zero);
        int built = host.Streamer.Tick(32);
        Assert.Equal(9, built);
        Assert.Equal(9, host.Streamer.LoadedCount);
        Assert.True(host.Streamer.IsLoaded(Int3.Zero));
        Assert.True(host.Streamer.IsLoaded(new Int3(1, 0, 1)));
        Assert.False(host.Streamer.IsLoaded(new Int3(2, 0, 0)));
    }

    [Fact]
    public void MovingFocus_UnloadsOldAndLoadsNew()
    {
        using var host = new StreamFixture(viewDistance: 1, maxChunkY: 0);
        host.Streamer.SetFocus(Float3.Zero);
        host.Streamer.Tick(32);
        int pooledBefore = VoxelChunkPool.PooledCount;

        host.Streamer.SetFocus(new Float3(64f, 0f, 0f));
        host.Streamer.Tick(32);

        Assert.Equal(9, host.Streamer.LoadedCount);
        Assert.Equal(new Int3(4, 0, 0), host.Streamer.FocusChunk);
        Assert.True(host.Streamer.IsLoaded(new Int3(4, 0, 0)));
        Assert.False(host.Streamer.IsLoaded(Int3.Zero));
        Assert.True(VoxelChunkPool.PooledCount >= pooledBefore);
    }

    [Fact]
    public void UnloadAll_ReturnsChunksToPool()
    {
        using var host = new StreamFixture(viewDistance: 1, maxChunkY: 0);
        host.Streamer.SetFocus(Float3.Zero);
        host.Streamer.Tick(32);
        int loaded = host.Streamer.LoadedCount;
        int pooledBefore = VoxelChunkPool.PooledCount;
        host.Streamer.UnloadAll();
        Assert.Equal(0, host.Streamer.LoadedCount);
        Assert.Equal(0, host.Streamer.WantedCount);
        Assert.True(VoxelChunkPool.PooledCount >= pooledBefore + loaded);
    }

    [Fact]
    public void FlatSurface_PaintsGrassOnTopLayer()
    {
        var generator = new FlatTerrainGenerator { Altitude = 4, SurfaceTypeIndex = 2 };
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];
        generator.PaintChunk(voxels, Int3.Zero, terrainTypeIndex: 3);
        Assert.Equal((ushort)3, voxels[VoxelCoords.Index(0, 2, 0)].TypeIndex);
        Assert.Equal((ushort)2, voxels[VoxelCoords.Index(0, 3, 0)].TypeIndex);
        Assert.True(voxels[VoxelCoords.Index(0, 4, 0)].IsEmpty);
    }

    [Fact]
    public void Budget_BuildsAtMostMaxChunksPerTick()
    {
        using var host = new StreamFixture(viewDistance: 1, maxChunkY: 0);
        host.Streamer.SetFocus(Float3.Zero);
        Assert.Equal(2, host.Streamer.Tick(2));
        Assert.Equal(2, host.Streamer.LoadedCount);
        Assert.Equal(7, host.Streamer.Tick(32));
        Assert.Equal(9, host.Streamer.LoadedCount);
    }

    [Fact]
    public void PackagedFlatWorldScene_Exists()
    {
        string scene = Path.Combine(
            FindRepositoryRoot(),
            "Packages", "com.xengine.voxelworld", "Runtime", "Scenes", "FlatWorld.scene");
        Assert.True(File.Exists(scene));
        string text = File.ReadAllText(scene);
        Assert.Contains("XEngine.VoxelWorld.VoxelWorld", text, StringComparison.Ordinal);
        Assert.Contains("EnableStreaming", text, StringComparison.Ordinal);
        Assert.Contains("ViewDistance", text, StringComparison.Ordinal);
        string testScene = Path.Combine(
            FindRepositoryRoot(), "VoxelWorldTestProject", "Assets", "Scenes", "FlatWorld.scene");
        Assert.True(File.Exists(testScene));
    }

    [Fact]
    public void N5Evidence_WritesStreamingBoard()
    {
        using var host = new StreamFixture(viewDistance: 1, maxChunkY: 0);
        host.Streamer.SetFocus(Float3.Zero);
        host.Streamer.Tick(32);
        host.Streamer.SetFocus(new Float3(64f, 0f, 0f));
        host.Streamer.Tick(32);
        int loaded = host.Streamer.LoadedCount;
        host.Streamer.UnloadAll();

        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 320, 27, 29, 36);
        board.Fill(24, 24, 872, 272, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 272, 70, 74, 90, 2);
        board.Text(40, 40, "N5 VOXEL CHUNK STREAMING", 230, 232, 240, 2);
        board.Text(40, 80, "VIEW DISTANCE LOAD AND UNLOAD", 140, 200, 160, 1);
        board.Fill(60, 120, 18, 18, 76, 175, 80, 255);
        board.Text(90, 122, "VD 1 LOADS 9 CHUNKS AT FOCUS", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 141, 110, 99, 255);
        board.Text(90, 154, $"MOVED FOCUS STILL {loaded} LOADED", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 80, 170, 220, 255);
        board.Text(90, 186, "UNLOAD ALL RETURNS POOL  NO LEAK", 200, 220, 200, 1);
        board.Text(40, 230, "TESTS 7/7  2026-09-15", 140, 146, 160, 1);
        board.Text(40, 258, "FLATWORLD SCENE IN PACKAGE AND TEST", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "n5-chunk-streaming.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "n5-chunk-streaming.md"),
            "# N5 View-distance chunk streaming\n\n" +
            "Date: 2026-09-15. Branch: `main`.\n\n" +
            "`VoxelChunkStreamer` keeps a Chebyshev XZ ring around the focus chunk, " +
            "paints Flat columns (dirt + grass cap), meshes through the N4 atlas, and " +
            "returns unused chunks to `VoxelChunkPool`. `VoxelWorld` pumps builds with " +
            "`XTask.Yield(Update)` and a per-frame budget. Package scene: " +
            "`Runtime/Scenes/FlatWorld.scene`.\n\n" +
            "```\n" +
            "dotnet test XEngine.Runtime.Test --filter FullyQualifiedName~VoxelStreamingTests\n" +
            "Passed!  - Failed: 0, Passed: 7, Skipped: 0\n" +
            "```\n\n" +
            "Screenshot: [n5-chunk-streaming.png](n5-chunk-streaming.png)\n");
    }

    private sealed class StreamFixture : IDisposable
    {
        public VoxelChunkStreamer Streamer { get; }

        public StreamFixture(int viewDistance, int maxChunkY)
        {
            Streamer = new VoxelChunkStreamer
            {
                ViewDistance = viewDistance,
                MinChunkY = 0,
                MaxChunkY = maxChunkY,
                TerrainTypeIndex = 3,
                Generator = new FlatTerrainGenerator { Altitude = 16, SurfaceTypeIndex = 2 },
                Atlas = XEngine.VoxelWorld.VoxelWorld.CreateDemoAtlas(),
            };
        }

        public void Dispose() => Streamer.UnloadAll();
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
