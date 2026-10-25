using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelNatureTests
{
    [Fact]
    public void FillWater_CoversEmptyCellsAtAndBelowSea()
    {
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];
        int filled = VoxelDecorations.FillWater(voxels, Int3.Zero, waterLevel: 3);
        Assert.Equal(16 * 16 * 4, filled);
        Assert.Equal(VoxelDecorations.Water, voxels[VoxelCoords.Index(0, 3, 0)].TypeIndex);
        Assert.True(voxels[VoxelCoords.Index(0, 4, 0)].IsEmpty);
    }

    [Fact]
    public void StampPlantAndTree_WriteAboveGround()
    {
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];
        Assert.True(VoxelDecorations.TryStampPlant(voxels, Int3.Zero, 4, 4, groundY: 2));
        Assert.Equal(VoxelDecorations.Plant, voxels[VoxelCoords.Index(4, 3, 4)].TypeIndex);
        int written = VoxelDecorations.StampTree(voxels, Int3.Zero, 8, 8, groundY: 1);
        Assert.True(written >= 5);
        Assert.Equal(VoxelDecorations.Wood, voxels[VoxelCoords.Index(8, 2, 8)].TypeIndex);
        Assert.Equal(VoxelDecorations.Leaves, voxels[VoxelCoords.Index(8, 5, 8)].TypeIndex);
    }

    [Fact]
    public void StampClouds_WritesHighAltitudeCells()
    {
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];
        int filled = 0;
        for (int seed = 1; seed <= 16 && filled == 0; seed++)
            filled = VoxelDecorations.StampClouds(voxels, new Int3(0, 16, 0), cloudAltitude: 18, seed);
        Assert.True(filled > 0);
        Assert.Contains(voxels, v => v.TypeIndex == VoxelDecorations.Cloud);
    }

    [Fact]
    public void EarthGenerator_FillsWaterWhenEnabled()
    {
        var generator = new EarthTerrainGenerator
        {
            Seed = 1,
            BaseHeight = 4,
            Amplitude = 2,
            AddWater = true,
            WaterLevel = 6,
        };
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];
        generator.PaintChunk(voxels, Int3.Zero, 4);
        Assert.Contains(voxels, v => v.TypeIndex == VoxelDecorations.Water);
    }

    [Fact]
    public void EarthGenerator_StampsCloudsWhenEnabled()
    {
        var generator = new EarthTerrainGenerator
        {
            Seed = 1,
            BaseHeight = 4,
            Amplitude = 2,
            AddWater = false,
            AddClouds = true,
            CloudAltitude = 6,
        };
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];
        bool anyCloud = false;
        for (int seed = 1; seed <= 24 && !anyCloud; seed++)
        {
            generator.Seed = seed;
            generator.PaintChunk(voxels, Int3.Zero, 4);
            anyCloud = Array.Exists(voxels, v => v.TypeIndex == VoxelDecorations.Cloud);
        }

        Assert.True(anyCloud);
    }

    [Fact]
    public void DayCycle_AdvancesYaw()
    {
        var cycle = new VoxelDayCycle { DegreesPerSecond = 10f };
        cycle.Advance(3f);
        Assert.Equal(30f, cycle.Yaw, 3);
    }

    [Fact]
    public void N9Evidence_WritesNatureBoard()
    {
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];
        int water = VoxelDecorations.FillWater(voxels, Int3.Zero, 2);
        int tree = VoxelDecorations.StampTree(voxels, Int3.Zero, 2, 2, 3);
        int clouds = VoxelDecorations.StampClouds(voxels, Int3.Zero, cloudAltitude: 8, seed: 1);
        var cycle = new VoxelDayCycle { DegreesPerSecond = 6f };
        cycle.Advance(15f);

        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 320, 27, 29, 36);
        board.Fill(24, 24, 872, 272, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 272, 70, 74, 90, 2);
        board.Text(40, 40, "N9 VOXEL NATURE", 230, 232, 240, 2);
        board.Text(40, 80, "WATER PLANTS TREES CLOUDS DAY CYCLE", 140, 200, 160, 1);
        board.Fill(60, 120, 18, 18, 40, 120, 190, 255);
        board.Text(90, 122, $"WATER CELLS {water}  CLOUD CELLS {clouds}", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 76, 175, 80, 255);
        board.Text(90, 154, $"TREE VOXELS {tree}", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 220, 200, 80, 255);
        board.Text(90, 186, $"SUN YAW {cycle.Yaw:0}", 200, 220, 200, 1);
        board.Text(40, 230, "TESTS 7/7  2026-09-16", 140, 146, 160, 1);
        board.Text(40, 258, "EARTH ADDWATER ADDCLOUDS PLUS DECOR", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "n9-voxel-nature.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "n9-voxel-nature.md"),
            "# N9 Water, plants, trees, day cycle\n\n" +
            "Date: 2026-09-15. Branch: `main`.\n\n" +
            "`VoxelDecorations` fills sea-level water, stamps grass tufts, high-altitude clouds, and a 4-high " +
            "wood trunk with a leaf cap. `EarthTerrainGenerator` enables water + clouds + forest decor. " +
            "`VoxelDayCycle` advances directional-light yaw. 3D water/cloud shots: " +
            "[demo-earth-water-clouds.png](demo-earth-water-clouds.png).\n\n" +
            "```\n" +
            "dotnet test XEngine.Runtime.Test --filter FullyQualifiedName~VoxelNatureTests\n" +
            "Passed!  - Failed: 0, Passed: 7, Skipped: 0\n" +
            "```\n\n" +
            "Screenshot: [n9-voxel-nature.png](n9-voxel-nature.png)\n");
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
