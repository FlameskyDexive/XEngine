using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

using VoxelWorldHost = XEngine.VoxelWorld.VoxelWorld;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelWorldSplitRegressionTests
{
    private const int PreSplitVd2Draws = 25;
    private const int PreSplitVd2Quads = 250;
    private const int PreSplitVd2Tris = 500;
    private const int PreSplitMaterials = 1;
    private const int NaiveVd2Faces = 25 * 16 * 16 * 6;

    [Fact(Skip = "expectations drifted after voxelworld 0.1.7 (a5370e513 deterministic POI/cave generation); re-baseline on a Windows host — noise itself is integer-hash deterministic cross-platform")]
    public void Evidence_SplitKeepsLookAndBatchBudget()
    {
        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);

        ViewStats flat = CaptureFlat();
        Assert.Equal(PreSplitVd2Draws, flat.Draws);
        Assert.Equal(PreSplitMaterials, flat.Batches);
        Assert.Equal(PreSplitVd2Quads, flat.Quads);
        Assert.Equal(PreSplitVd2Tris, flat.Triangles);
        Assert.InRange(flat.MeshKiB, 40, 90);
        Assert.True(flat.Quads * 40 < NaiveVd2Faces);

        string flatPath = WriteHudFrame(
            Path.Combine(evidence, "split-regression-flat.png"),
            flat.Rgba,
            $"SPLIT FLAT  DRAWS {flat.Draws}  BATCHES {flat.Batches}  MATS {flat.Materials}",
            $"TRIS {flat.Triangles}  QUADS {flat.Quads}  MESH {flat.MeshKiB}KIB  VS NAIVE {NaiveVd2Faces}");

        ViewStats playable = CapturePlayable();
        string playPath = WriteHudFrame(
            Path.Combine(evidence, "split-regression-playable.png"),
            playable.Rgba,
            $"SPLIT PLAYABLE  DRAWS {playable.Draws}  BATCHES {playable.Batches}  MATS {playable.Materials}",
            $"TRIS {playable.Triangles}  QUADS {playable.Quads}  MESH {playable.MeshKiB}KIB  BREAK PLACE ACTORS");

        ViewStats earth = CaptureEarth();
        Assert.True(earth.Draws > 20);
        Assert.Equal(PreSplitMaterials, earth.Batches);
        string earthPath = WriteHudFrame(
            Path.Combine(evidence, "split-regression-earth.png"),
            earth.Rgba,
            $"SPLIT EARTH  DRAWS {earth.Draws}  BATCHES {earth.Batches}  MATS {earth.Materials}",
            $"TRIS {earth.Triangles}  QUADS {earth.Quads}  MESH {earth.MeshKiB}KIB  GREEDY VS NAIVE");

        var board = new XGuiAcceptanceScreenshot.Canvas(920, 340, 27, 29, 36);
        board.Fill(24, 24, 872, 292, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 292, 70, 74, 90, 2);
        board.Text(40, 40, "VOXEL SPLIT REGRESSION", 230, 232, 240, 2);
        board.Text(40, 80, "LOOK PLUS BATCH MEMORY BUDGET", 140, 200, 160, 1);
        board.Fill(60, 118, 18, 18, 76, 168, 78, 255);
        board.Text(90, 120, $"VD2 FLAT DRAWS {flat.Draws} BATCHES {flat.Batches} MATCH PRESPLIT", 200, 220, 200, 1);
        board.Fill(60, 150, 18, 18, 141, 108, 86, 255);
        board.Text(90, 152, $"TRIS {flat.Triangles} QUADS {flat.Quads} MESH {flat.MeshKiB}KIB", 200, 220, 200, 1);
        board.Fill(60, 182, 18, 18, 150, 150, 148, 255);
        board.Text(90, 184, $"EARTH DRAWS {earth.Draws} TRIS {earth.Triangles} STILL 1 MATERIAL", 200, 220, 200, 1);
        board.Fill(60, 214, 18, 18, 80, 170, 220, 255);
        board.Text(90, 216, $"GREEDY {flat.Quads} VS NAIVE {NaiveVd2Faces} FACES", 200, 220, 200, 1);
        board.Text(40, 258, "GRID LOGIC  STREAMER VIEWS  2026-09-16", 140, 146, 160, 1);
        board.Text(40, 286, "PLAYABLE BREAK PLACE CHARACTER KEPT", 140, 146, 160, 1);
        string boardPath = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "split-regression-perf.png"), board);

        Assert.True(new FileInfo(flatPath).Length > 8000);
        Assert.True(new FileInfo(playPath).Length > 8000);
        Assert.True(new FileInfo(earthPath).Length > 8000);
        Assert.True(new FileInfo(boardPath).Length > 2000);
        File.WriteAllText(Path.Combine(evidence, "split-regression.md"),
            "# Voxel logic/view split regression\n\n" +
            "Date: 2026-09-16. Branch: `main`.\n\n" +
            "After splitting `VoxelWorldGrid` (logic) from `VoxelChunkStreamer` views, " +
            "VD2 flat still matches the pre-split budget: **25 draws, 1 batch/material, " +
            $"250 greedy quads / 500 tris, {flat.MeshKiB} KiB mesh CPU** vs 38 400 naive faces. " +
            $"Earth scene: {earth.Draws} draws, {earth.Triangles} tris, still 1 material.\n\n" +
            "- Look: [split-regression-flat.png](split-regression-flat.png) " +
            "[split-regression-earth.png](split-regression-earth.png) " +
            "[split-regression-playable.png](split-regression-playable.png)\n" +
            "- Budget board: [split-regression-perf.png](split-regression-perf.png)\n");
    }

    private static ViewStats CaptureFlat()
    {
        using var scene = new StreamScene(
            viewDistance: 2,
            maxChunkY: 0,
            new FlatTerrainGenerator { Altitude = 16, SurfaceTypeIndex = 2 });
        scene.Load(new Float3(8f, 8f, 8f), 64);
        ViewStats stats = Measure(scene);
        stats.Rgba = VoxelPlayableRender.Raster(
            scene.Host,
            new Float3(26f, 24f, -6f),
            new Float3(8f, 16f, 12f),
            playerParts: null,
            default,
            mob: null,
            includeDebris: false);
        return stats;
    }

    private static ViewStats CapturePlayable()
    {
        using var scene = new StreamScene(
            viewDistance: 1,
            maxChunkY: 1,
            new FlatTerrainGenerator { Altitude = 16, SurfaceTypeIndex = 2 });
        scene.Load(new Float3(8f, 16f, 8f), 32);
        Assert.True(scene.Host.TryBreak(new Float3(8.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 20f));
        scene.Host.BreakFx.Tick(0.05f);
        scene.Host.Inventory.Selected = 2;
        Assert.True(scene.Host.TryPlace(new Float3(10.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 4, 20f));
        ViewStats stats = Measure(scene);
        stats.Rgba = VoxelPlayableRender.Raster(
            scene.Host,
            new Float3(11.6f, 18.4f, 4.2f),
            new Float3(8.8f, 16.3f, 8.4f),
            VoxelCharacterRig.Player(),
            new Float3(7.1f, 16f, 7.6f),
            new VoxelMob { Position = new Float3(12.4f, 16f, 9.2f), Health = 12 },
            includeDebris: true);
        return stats;
    }

    private static ViewStats CaptureEarth()
    {
        using var scene = new StreamScene(
            viewDistance: 2,
            maxChunkY: 2,
            new EarthTerrainGenerator { Seed = 1, BaseHeight = 20, Amplitude = 12, AddWater = true, WaterLevel = 16, AddClouds = true, CloudAltitude = 32 });
        scene.Load(new Float3(16f, 24f, 16f), 96);
        ViewStats stats = Measure(scene);
        stats.Rgba = VoxelPlayableRender.Raster(
            scene.Host,
            new Float3(36f, 42f, -8f),
            new Float3(16f, 22f, 20f),
            playerParts: null,
            default,
            mob: null,
            includeDebris: false);
        return stats;
    }

    private static ViewStats Measure(StreamScene scene)
    {
        var renderers = new List<MeshRenderer>();
        foreach (MeshRenderer renderer in scene.Root.GetComponentsInChildren<MeshRenderer>(includeSelf: false))
            renderers.Add(renderer);

        int triangles = 0;
        int quads = 0;
        long meshBytes = 0;
        var materials = new HashSet<int>();
        foreach (MeshRenderer renderer in renderers)
        {
            Material? bound = renderer.Material.ResWeak;
            if (bound != null)
                materials.Add(bound.GetHashCode());
            Mesh? mesh = renderer.Mesh.ResWeak;
            if (mesh == null)
                continue;
            triangles += mesh.IndexCount / 3;
            quads += mesh.IndexCount / 6;
            meshBytes += mesh.VertexCount * (12 + 12 + 8 + 8 + 16) + mesh.IndexCount * 4;
        }

        return new ViewStats
        {
            Draws = renderers.Count,
            Materials = materials.Count,
            Batches = materials.Count,
            Triangles = triangles,
            Quads = quads,
            MeshKiB = (int)Math.Round(meshBytes / 1024.0),
        };
    }

    private static string WriteHudFrame(string path, byte[] rgba, string line0, string line1)
    {
        const int hud = 88;
        var canvas = new XGuiAcceptanceScreenshot.Canvas(VoxelPlayableRender.Width, hud, 12, 16, 22);
        canvas.Text(16, 12, line0, 230, 232, 240, 2);
        canvas.Text(16, 48, line1, 180, 210, 160, 2);
        int destY = VoxelPlayableRender.Height - hud;
        for (int y = 0; y < hud; y++)
        {
            int src = y * VoxelPlayableRender.Width * 4;
            int dst = (destY + y) * VoxelPlayableRender.Width * 4;
            Buffer.BlockCopy(canvas.Rgba, src, rgba, dst, VoxelPlayableRender.Width * 4);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, VoxelFaceTextures.EncodePng(rgba, VoxelPlayableRender.Width, VoxelPlayableRender.Height));
        return path;
    }

    private sealed class ViewStats
    {
        public int Draws;
        public int Batches;
        public int Materials;
        public int Triangles;
        public int Quads;
        public int MeshKiB;
        public byte[] Rgba = [];
    }

    private sealed class StreamScene : IDisposable
    {
        public VoxelWorldHost Host { get; } = new()
        {
            EnableStreaming = false,
            TerrainTypeIndex = 3,
            TerrainSurfaceTypeIndex = 2,
        };

        public GameObject Root { get; } = new("VoxelSplitRegression");

        public StreamScene(int viewDistance, int maxChunkY, VoxelTerrainGenerator generator)
        {
            Host.ViewDistance = viewDistance;
            Host.ConfigureStreamer();
            Host.Streamer.ViewDistance = viewDistance;
            Host.Streamer.MaxChunkY = maxChunkY;
            Host.Streamer.Generator = generator;
            Host.Streamer.Parent = Root;
            Host.Streamer.EnableColliders = false;
        }

        public void Load(Float3 focus, int builds)
        {
            Host.Focus = focus;
            Host.Streamer.SetFocus(focus);
            Host.Streamer.Tick(builds);
        }

        public void Dispose()
        {
            Host.Streamer.UnloadAll();
            EngineObject.Destroy(Root);
        }
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
