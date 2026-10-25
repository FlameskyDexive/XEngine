using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelRegionStoreTests
{
    [Fact]
    public void Store_SkipsUnedited_GeneratedColumn()
    {
        string root = Path.Combine(Path.GetTempPath(), "vw-world-" + Guid.NewGuid());
        var store = new VoxelRegionStore(root);
        Assert.False(store.TryLoadSection(Int3.Zero, out _));
        Assert.False(Directory.Exists(store.RegionDirectory));
    }

    [Fact]
    public void Store_PersistsEditedSection_ReloadAfterNewStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "vw-world-" + Guid.NewGuid());
        var palette = VoxelPalette.Fill(3);
        palette.Set(VoxelCoords.Index(8, 15, 8), 0);
        var store = new VoxelRegionStore(root) { MinChunkY = 0, MaxChunkY = 0 };
        store.SaveSection(Int3.Zero, palette);
        var again = new VoxelRegionStore(root) { MinChunkY = 0, MaxChunkY = 0 };
        Assert.True(again.TryLoadSection(Int3.Zero, out VoxelPalette loaded));
        Assert.Equal((ushort)0, loaded.Get(VoxelCoords.Index(8, 15, 8)));
        Assert.Equal((ushort)3, loaded.Get(0));
    }

    [Fact]
    public void Store_MergesTwoYSectionsInSameColumn()
    {
        string root = Path.Combine(Path.GetTempPath(), "vw-world-" + Guid.NewGuid());
        var store = new VoxelRegionStore(root) { MinChunkY = 0, MaxChunkY = 1 };
        var low = VoxelPalette.Fill(3);
        low.Set(0, 0);
        var high = VoxelPalette.Fill(4);
        high.Set(1, 0);
        store.SaveSection(new Int3(0, 0, 0), low);
        store.SaveSection(new Int3(0, 1, 0), high);

        Assert.True(store.TryLoadSection(new Int3(0, 0, 0), out VoxelPalette loadedLow));
        Assert.True(store.TryLoadSection(new Int3(0, 1, 0), out VoxelPalette loadedHigh));
        Assert.Equal((ushort)0, loadedLow.Get(0));
        Assert.Equal((ushort)3, loadedLow.Get(1));
        Assert.Equal((ushort)0, loadedHigh.Get(1));
        Assert.Equal((ushort)4, loadedHigh.Get(0));
    }

    [Fact]
    public void ColumnCodec_RoundTrip_NullSections()
    {
        var sections = new List<VoxelPalette?>
        {
            VoxelPalette.Fill(2),
            null,
            VoxelPalette.Fill(5),
        };
        byte[] payload = VoxelColumnCodec.Encode(sections, minY: -1);
        List<VoxelPalette?> decoded = VoxelColumnCodec.Decode(payload, out int minY);
        Assert.Equal(-1, minY);
        Assert.Equal(3, decoded.Count);
        Assert.NotNull(decoded[0]);
        Assert.Null(decoded[1]);
        Assert.NotNull(decoded[2]);
        Assert.Equal((ushort)2, decoded[0]!.Get(0));
        Assert.Equal((ushort)5, decoded[2]!.Get(0));
    }

    [Fact]
    public void Grid_ReloadAfterUnload_KeepsEditedHole()
    {
        string root = Path.Combine(Path.GetTempPath(), "vw-grid-" + Guid.NewGuid());
        var store = new VoxelRegionStore(root) { MinChunkY = 0, MaxChunkY = 0 };
        var grid = new VoxelWorldGrid
        {
            ViewDistance = 0,
            MinChunkY = 0,
            MaxChunkY = 0,
            TerrainTypeIndex = 3,
            Store = store,
            Generator = new FlatTerrainGenerator { Altitude = 16, SurfaceTypeIndex = 2 },
        };
        grid.SetFocus(new Float3(8, 8, 8));
        Assert.Equal(1, grid.Tick(8));
        Assert.True(grid.TrySetVoxel(new Int3(8, 15, 8), Voxel.Empty));
        grid.UnloadAll();
        Assert.Equal(0, grid.LoadedCount);
        grid.SetFocus(new Float3(8, 8, 8));
        Assert.Equal(1, grid.Tick(8));
        Assert.True(grid.TryGetVoxel(new Int3(8, 15, 8), out Voxel hole) && hole.IsEmpty);
    }

    [Fact]
    public void Grid_UnloadWithoutEdits_WritesNoRegionFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "vw-grid-clean-" + Guid.NewGuid());
        var store = new VoxelRegionStore(root) { MinChunkY = 0, MaxChunkY = 0 };
        var grid = new VoxelWorldGrid
        {
            ViewDistance = 0,
            MinChunkY = 0,
            MaxChunkY = 0,
            TerrainTypeIndex = 3,
            Store = store,
            Generator = new FlatTerrainGenerator { Altitude = 16, SurfaceTypeIndex = 2 },
        };
        grid.SetFocus(new Float3(8, 8, 8));
        Assert.Equal(1, grid.Tick(8));
        grid.UnloadAll();
        Assert.False(Directory.Exists(store.RegionDirectory) &&
                     Directory.EnumerateFiles(store.RegionDirectory, "*.vwr", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void Store_ShiftWorld_MovesEditedColumnSlot()
    {
        string root = Path.Combine(Path.GetTempPath(), "vw-shift-" + Guid.NewGuid());
        var store = new VoxelRegionStore(root) { MinChunkY = 0, MaxChunkY = 0 };
        var palette = VoxelPalette.Fill(3);
        palette.Set(VoxelCoords.Index(8, 15, 8), 0);
        store.SaveSection(Int3.Zero, palette);
        Assert.True(store.TryLoadSection(Int3.Zero, out _));

        VoxelOriginShift.ShiftStore(store, new Int3(16, 0, 0));
        Assert.False(store.TryLoadSection(Int3.Zero, out _));
        Assert.True(store.TryLoadSection(new Int3(-1, 0, 0), out VoxelPalette moved));
        Assert.Equal((ushort)0, moved.Get(VoxelCoords.Index(8, 15, 8)));
    }

    [Fact]
    public void Evidence_RegionStore_WritesSizeComparison()
    {
        string root = Path.Combine(Path.GetTempPath(), "vw-evidence-" + Guid.NewGuid());
        var store = new VoxelRegionStore(root) { MinChunkY = 0, MaxChunkY = 0 };
        var palette = VoxelPalette.Fill(3);
        for (int i = 0; i < 100; i++)
            palette.Set(i, 0);
        store.SaveSection(Int3.Zero, palette);

        string vwr = Directory.EnumerateFiles(store.RegionDirectory, "*.vwr").First();
        long vwrBytes = new FileInfo(vwr).Length;

        var echo = new VoxelWorldSave();
        for (int i = 0; i < 100; i++)
        {
            Int3 local = VoxelCoords.LocalFromIndex(i);
            echo.Record(local, 0);
        }
        string echoPath = Path.Combine(root, "legacy.echo");
        VoxelWorldSave.WriteFile(echoPath, echo);
        long echoBytes = new FileInfo(echoPath).Length;

        using var single = new MemoryStream();
        VoxelPalette.Fill(3).WriteTo(single);

        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        string md = Path.Combine(evidence, "region-store.md");
        File.WriteAllText(md,
            "# Region store (Anvil-aligned)\n\n" +
            "Date: 2026-09-16. Branch: `main`.\n\n" +
            "Pure-client terrain storage: 16³ `VoxelPalette` (Minecraft padded longs) + " +
            "`region/r.rx.rz.vwr` Anvil shell (4 KiB sectors, zlib). Payload magic `VW01` " +
            "(not Mojang NBT / `.mca`). Unedited columns are omitted and regenerated from seed.\n\n" +
            "## Size comparison\n\n" +
            $"- Dense `ushort[4096]` = {4096 * 2} B\n" +
            $"- Single-valued palette payload = {single.Length} B\n" +
            $"- 100-hole legacy Echo list = {echoBytes} B\n" +
            $"- Same 100-hole zlib `.vwr` column = {vwrBytes} B (sector-aligned)\n\n" +
            "Rendering budget after palette SoT is unchanged (see split-regression). Storage change is logic-only.\n\n" +
            "```\n" +
            "dotnet test XEngine.Runtime.Test --filter FullyQualifiedName~Voxel\n" +
            "```\n");
        Assert.True(File.Exists(md));
        Assert.True(single.Length < 32);
        Assert.True(vwrBytes % 4096 == 0);
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
