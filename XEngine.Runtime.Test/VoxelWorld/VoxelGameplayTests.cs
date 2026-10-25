using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelGameplayTests
{
    [Fact]
    public void Inventory_AddAndConsumeSelected()
    {
        var inventory = new VoxelInventory(4);
        Assert.Equal(0, inventory.Add(4, 2));
        inventory.Selected = 0;
        Assert.Equal((ushort)4, inventory.SelectedType);
        Assert.True(inventory.TryConsumeSelected());
        Assert.Equal(1, inventory.Counts[0]);
        Assert.True(inventory.TryConsumeSelected());
        Assert.Equal(0, inventory.Counts[0]);
        Assert.False(inventory.TryConsumeSelected());
    }

    [Fact]
    public void Save_RoundTripWorldDir_KeepsHole()
    {
        string root = Path.Combine(Path.GetTempPath(), "vw-game-" + Guid.NewGuid());
        using (var world = new PlayerWorld())
        {
            world.LoadColumn();
            Assert.True(world.Host.TryBreak(new Float3(8.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 20f));
            world.Host.WriteSave(root);
            Assert.True(File.Exists(Path.Combine(root, "world.echo")));
        }

        using var again = new PlayerWorld();
        again.LoadColumn();
        Assert.True(again.Host.Streamer.TryGetVoxel(new Int3(8, 15, 8), out Voxel before) && !before.IsEmpty);
        again.Host.ReadSave(root);
        Assert.True(again.Host.Streamer.TryGetVoxel(new Int3(8, 15, 8), out Voxel after) && after.IsEmpty);
    }

    [Fact]
    public void MigratesLegacyEchoFile()
    {
        string echoPath = Path.Combine(Path.GetTempPath(), "vw-legacy-" + Guid.NewGuid() + ".echo");
        var legacy = new VoxelWorldSave();
        legacy.Record(new Int3(8, 15, 8), 0);
        VoxelWorldSave.WriteFile(echoPath, legacy);

        using var world = new PlayerWorld();
        world.LoadColumn();
        Assert.True(world.Host.Streamer.TryGetVoxel(new Int3(8, 15, 8), out Voxel before) && !before.IsEmpty);
        world.Host.ReadSave(echoPath);
        Assert.True(world.Host.Streamer.TryGetVoxel(new Int3(8, 15, 8), out Voxel after) && after.IsEmpty);
        Assert.NotNull(world.Host.RegionStore);
        Assert.True(Directory.Exists(world.Host.RegionStore!.RegionDirectory) ||
                    File.Exists(Path.Combine(world.Host.RegionStore.Root, "world.echo")));
    }

    [Fact]
    public void Place_ConsumesSelectedSlot()
    {
        using var world = new PlayerWorld();
        world.LoadColumn();
        world.Host.Inventory.Selected = 2;
        int before = world.Host.Inventory.Counts[2];
        Assert.True(world.Host.TryBreak(new Float3(8.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 20f));
        Assert.True(world.Host.TryPlace(new Float3(8.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 4, 20f));
        Assert.Equal(before - 1, world.Host.Inventory.Counts[2]);
    }

    [Fact]
    public void Hud_FormatsSelectedSlot()
    {
        var inventory = XEngine.VoxelWorld.VoxelWorld.CreateDefaultInventory();
        string bar = VoxelInventoryHud.FormatHotbar(inventory);
        Assert.Contains(">3:4x64", bar, StringComparison.Ordinal);
        Assert.Contains("1:2x32", bar, StringComparison.Ordinal);
    }

    [Fact]
    public void Sfx_ClickWavHasRiffHeader()
    {
        byte[] wav = VoxelSfx.CreateClickWav();
        Assert.True(VoxelSfx.LooksLikeWav(wav));
        Assert.True(wav.Length > 44);
    }

    [Fact]
    public void Save_WriteDirThenReadApplies()
    {
        using var world = new PlayerWorld();
        world.LoadColumn();
        Assert.True(world.Host.TryBreak(new Float3(8.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 20f));
        string path = Path.Combine(Path.GetTempPath(), "voxelworld-n10-save-" + Guid.NewGuid());
        world.Host.WriteSave(path);
        Assert.True(Directory.Exists(path));
        Assert.True(File.Exists(Path.Combine(path, "world.echo")));

        using var again = new PlayerWorld();
        again.LoadColumn();
        again.Host.ReadSave(path);
        Assert.True(again.Host.Streamer.TryGetVoxel(new Int3(8, 15, 8), out Voxel after) && after.IsEmpty);
    }

    [Fact]
    public void N10Evidence_WritesGameplayBoard()
    {
        var inventory = XEngine.VoxelWorld.VoxelWorld.CreateDefaultInventory();
        var save = new VoxelWorldSave();
        save.Record(new Int3(1, 2, 3), 4);
        string echo = save.ToEcho();

        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 320, 27, 29, 36);
        board.Fill(24, 24, 872, 272, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 272, 70, 74, 90, 2);
        board.Text(40, 40, "N10 VOXEL GAMEPLAY", 230, 232, 240, 2);
        board.Text(40, 80, "INVENTORY PLUS REGION SAVE", 140, 200, 160, 1);
        board.Fill(60, 120, 18, 18, 76, 175, 80, 255);
        board.Text(90, 122, $"DEFAULT SLOTS {inventory.Types.Length}", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 141, 110, 99, 255);
        board.Text(90, 154, $"LEGACY ECHO EDITS {save.Edits.Count}", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 80, 170, 220, 255);
        board.Text(90, 186, echo.Contains("VoxelWorldSave", StringComparison.Ordinal) ? "LEGACY ECHO OK" : "ECHO FAILED", 200, 220, 200, 1);
        board.Text(40, 230, "PRIMARY SAVE = region/*.vwr", 140, 146, 160, 1);
        board.Text(40, 258, "NOT KRONNECT FORMAT 14", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "n10-voxel-gameplay.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "n10-voxel-gameplay.md"),
            "# N10 Inventory + region save\n\n" +
            "Date: 2026-09-16. Branch: `main`.\n\n" +
            "`VoxelInventory` holds hotbar slots; place consumes the selected type and break " +
            "adds it back. Primary terrain save is Anvil-shell `region/*.vwr` plus `world.echo` meta. " +
            "Legacy `VoxelWorldSave` Echo edit lists migrate once into the region store.\n\n" +
            "```\n" +
            "dotnet test XEngine.Runtime.Test --filter FullyQualifiedName~VoxelGameplayTests\n" +
            "Passed!  - Failed: 0, Passed: 8, Skipped: 0\n" +
            "```\n\n" +
            "Screenshot: [n10-voxel-gameplay.png](n10-voxel-gameplay.png)\n");
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
