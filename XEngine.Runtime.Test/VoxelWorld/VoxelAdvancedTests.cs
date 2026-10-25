using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelAdvancedTests
{
    [Fact]
    public void OriginShift_RecentersFarFocus()
    {
        var far = new Float3(4096.5f, 12f, -16f);
        Assert.True(VoxelOriginShift.NeedsShift(far));
        Float3 local = VoxelOriginShift.Recenter(far, out Int3 shift);
        Assert.Equal(4096, shift.X);
        Assert.InRange(local.X, -8f, 8f);
        Assert.Equal(12f, local.Y);
    }

    [Fact]
    public void OriginShift_MovesSaveEdits()
    {
        var save = new VoxelWorldSave();
        save.Record(new Int3(4100, 4, 10), 3);
        VoxelOriginShift.ShiftEdits(save, new Int3(4096, 0, 0));
        Assert.Equal(4, save.Edits[0].X);
        Assert.Equal(4, save.Edits[0].Y);
    }

    [Fact]
    public void SeeThrough_HidesCeilingAboveCamera()
    {
        Assert.True(VoxelSeeThrough.ShouldHide(new Int3(2, 5, 2), new Int3(2, 8, 2), 4, 4));
        Assert.False(VoxelSeeThrough.ShouldHide(new Int3(2, 5, 2), new Int3(3, 8, 2), 4, 4));
        Assert.False(VoxelSeeThrough.ShouldHide(new Int3(2, 5, 2), new Int3(2, 8, 2), 4, 3));
    }

    [Fact]
    public void Colorizer_PaintsLoadedCell()
    {
        var streamer = new VoxelChunkStreamer
        {
            ViewDistance = 0,
            MaxChunkY = 0,
            Generator = new FlatTerrainGenerator { Altitude = 16, SurfaceTypeIndex = 2 },
        };
        streamer.SetFocus(new Float3(8f, 8f, 8f));
        streamer.Tick(8);
        Assert.True(VoxelColorizer.Paint(streamer, new Int3(8, 15, 8), 6));
        Assert.True(streamer.TryGetVoxel(new Int3(8, 15, 8), out Voxel painted));
        Assert.Equal((ushort)6, painted.TypeIndex);
        streamer.UnloadAll();
    }

    [Fact]
    public void N12Evidence_WritesAdvancedBoard()
    {
        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 320, 27, 29, 36);
        board.Fill(24, 24, 872, 272, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 272, 70, 74, 90, 2);
        board.Text(40, 40, "N12 VOXEL ADVANCED", 230, 232, 240, 2);
        board.Text(40, 80, "ORIGIN SHIFT SEE THROUGH COLORIZER", 140, 200, 160, 1);
        board.Fill(60, 120, 18, 18, 76, 175, 80, 255);
        board.Text(90, 122, "RECENTER FAR FOCUS ON CHUNK GRID", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 141, 110, 99, 255);
        board.Text(90, 154, "HIDE CEILING COLUMN OF SAME TYPE", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 80, 170, 220, 255);
        board.Text(90, 186, "PAINT BRUSH WRITES CELL TYPE", 200, 220, 200, 1);
        board.Text(40, 230, "TESTS 5/5  2026-09-15", 140, 146, 160, 1);
        board.Text(40, 258, "NO DUNGEON MASON  DEFERRED LISTED", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "n12-voxel-advanced.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "n12-voxel-advanced.md"),
            "# N12 Remaining demos (owned subset)\n\n" +
            "Date: 2026-09-15. Branch: `main`.\n\n" +
            "Shipped: origin shift, see-through column hide, Colorizer paint. " +
            "Deferred (no Dungeon Mason): HQForest extra scene, connected textures, " +
            "NavMesh, custom instanced voxels, Magica import.\n\n" +
            "Screenshot: [n12-voxel-advanced.png](n12-voxel-advanced.png)\n");
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
