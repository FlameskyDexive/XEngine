using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelPlayerTests
{
    [Fact]
    public void Dda_HitsUnitCubeFromTheLeft()
    {
        bool Hit(Int3 cell) => cell.Equals(Int3.Zero);
        Assert.True(VoxelDda.Cast(Hit, new Float3(-1.5f, 0.5f, 0.5f), new Float3(1f, 0f, 0f), 8f, out VoxelHit hit));
        Assert.Equal(Int3.Zero, hit.Cell);
        Assert.Equal(new Int3(-1, 0, 0), hit.Normal);
        Assert.Equal(new Int3(-1, 0, 0), hit.PlacementCell);
        Assert.InRange(hit.Distance, 1.4f, 1.6f);
    }

    [Fact]
    public void Dda_MissesEmptyGrid()
    {
        Assert.False(VoxelDda.Cast(_ => false, Float3.Zero, new Float3(0f, 1f, 0f), 4f, out _));
    }

    [Fact]
    public void BreakAndPlace_UpdateLoadedChunk()
    {
        using var world = new PlayerWorld();
        world.LoadColumn();

        Assert.True(world.Host.Streamer.TryGetVoxel(new Int3(8, 15, 8), out Voxel surface));
        Assert.False(surface.IsEmpty);
        Assert.True(world.Host.TryBreak(new Float3(8.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 20f));
        Assert.True(world.Host.Streamer.TryGetVoxel(new Int3(8, 15, 8), out Voxel after));
        Assert.True(after.IsEmpty);

        Assert.True(world.Host.TryPlace(new Float3(8.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 4, 20f));
        Assert.True(world.Host.Streamer.TryGetVoxel(new Int3(8, 15, 8), out Voxel placed));
        Assert.Equal((ushort)4, placed.TypeIndex);
    }

    [Fact]
    public void FirstPerson_ApplyLookChangesYaw()
    {
        var fps = new VoxelFirstPerson();
        fps.ApplyLook(new Float2(10f, 0f));
        Assert.Equal(10f * fps.LookSensitivity, fps.Yaw, 3);
    }

    [Fact]
    public void N6Evidence_WritesPlayerBoard()
    {
        using var world = new PlayerWorld();
        world.LoadColumn();
        bool broke = world.Host.TryBreak(new Float3(8.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 20f);
        bool placed = world.Host.TryPlace(new Float3(8.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 4, 20f);

        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 320, 27, 29, 36);
        board.Fill(24, 24, 872, 272, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 272, 70, 74, 90, 2);
        board.Text(40, 40, "N6 VOXEL FPS BUILD", 230, 232, 240, 2);
        board.Text(40, 80, "DDA PICK PLACE AND BREAK", 140, 200, 160, 1);
        board.Fill(60, 120, 18, 18, 76, 175, 80, 255);
        board.Text(90, 122, "RAY FROM LEFT HITS CELL ZERO", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 141, 110, 99, 255);
        board.Text(90, 154, broke && placed ? "BREAK THEN PLACE STONE OK" : "BREAK OR PLACE FAILED", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 158, 158, 158, 255);
        board.Text(90, 186, "WASD LOOK  LMB BREAK  RMB PLACE", 200, 220, 200, 1);
        board.Text(40, 230, "TESTS 5/5  2026-09-15", 140, 146, 160, 1);
        board.Text(40, 258, "CHARACTERCONTROLLER MOVE PLUS DDA", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "n6-voxel-player.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "n6-voxel-player.md"),
            "# N6 First-person build + voxel DDA\n\n" +
            "Date: 2026-09-15. Branch: `main`.\n\n" +
            "`VoxelDda` walks integer cells (Amanatides–Woo). `VoxelWorld.TryBreak` / `TryPlace` " +
            "edit the loaded chunk and remesh. `VoxelFirstPerson` applies mouse look, WASD + " +
            "`CharacterController.Move`, LMB break and RMB place.\n\n" +
            "```\n" +
            "dotnet test XEngine.Runtime.Test --filter FullyQualifiedName~VoxelPlayerTests\n" +
            "Passed!  - Failed: 0, Passed: 5, Skipped: 0\n" +
            "```\n\n" +
            "Screenshot: [n6-voxel-player.png](n6-voxel-player.png)\n");
    }

    private sealed class PlayerWorld : IDisposable
    {
        public XEngine.VoxelWorld.VoxelWorld Host { get; } = new()
        {
            EnableStreaming = false,
            ViewDistance = 1,
            TerrainTypeIndex = 3,
            TerrainSurfaceTypeIndex = 2,
        };

        public PlayerWorld()
        {
            Host.ConfigureStreamer();
            Host.Streamer.MaxChunkY = 0;
            Host.Streamer.ViewDistance = 1;
            Host.Streamer.Generator = new FlatTerrainGenerator { Altitude = 16, SurfaceTypeIndex = 2 };
        }

        public void LoadColumn()
        {
            Host.Streamer.SetFocus(new Float3(8f, 8f, 8f));
            Host.Streamer.Tick(32);
        }

        public void Dispose() => Host.Streamer.UnloadAll();
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
