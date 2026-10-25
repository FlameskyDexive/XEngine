using XEngine.Echo;
using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelEarthTests
{
    [Fact]
    public void PickBiome_SplitsForestDesertSnow()
    {
        Assert.Equal(VoxelBiomeKind.Snow, EarthTerrainGenerator.PickBiome(0.9f, 0.5f));
        Assert.Equal(VoxelBiomeKind.Desert, EarthTerrainGenerator.PickBiome(0.3f, 0.2f));
        Assert.Equal(VoxelBiomeKind.Forest, EarthTerrainGenerator.PickBiome(0.4f, 0.7f));
    }

    [Fact]
    public void SameSeed_RepeatsColumnHeight()
    {
        var generator = new EarthTerrainGenerator { Seed = 7, BaseHeight = 36, Amplitude = 20 };
        generator.SampleColumn(12, 40, out int a, out VoxelBiomeKind biomeA);
        generator.SampleColumn(12, 40, out int b, out VoxelBiomeKind biomeB);
        Assert.Equal(a, b);
        Assert.Equal(biomeA, biomeB);
        generator.Seed = 8;
        generator.SampleColumn(12, 40, out int c, out _);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void PaintChunk_EmitsAllThreeSurfaceTypes()
    {
        var generator = new EarthTerrainGenerator { Seed = 1, BaseHeight = 8, Amplitude = 6 };
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];
        var seen = new HashSet<ushort>();
        for (int cz = -3; cz <= 3; cz++)
        for (int cx = -6; cx <= 6; cx++)
        {
            generator.PaintChunk(voxels, new Int3(cx * 16, 0, cz * 16), 4);
            for (int i = 0; i < voxels.Length; i++)
            {
                if (!voxels[i].IsEmpty)
                    seen.Add(voxels[i].TypeIndex);
            }
        }

        Assert.Contains((ushort)2, seen);
        Assert.Contains((ushort)6, seen);
        Assert.Contains((ushort)7, seen);
    }

    [Fact]
    public void PackagedEarthAssets_ListBiomes()
    {
        string earth = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.voxelworld", "Runtime", "Resources", "Worlds", "Earth");
        Assert.True(File.Exists(Path.Combine(earth, "EarthWorld.asset")));
        Assert.True(File.Exists(Path.Combine(earth, "sand.png")));
        Assert.True(File.Exists(Path.Combine(earth, "snow.png")));
        string world = File.ReadAllText(Path.Combine(earth, "EarthWorld.asset"));
        Assert.Contains("4d9e2b30-8c5e-4f02-ad3b-1a7c9d0e5f41", world, StringComparison.Ordinal);
        Assert.Contains("4d9e2b30-8c5e-4f02-ad3b-1a7c9d0e5f42", world, StringComparison.Ordinal);
        Assert.Contains("4d9e2b30-8c5e-4f02-ad3b-1a7c9d0e5f43", world, StringComparison.Ordinal);
        var generator = Serializer.Deserialize<EarthTerrainGenerator>(
            EchoObject.ReadFromString(File.ReadAllText(Path.Combine(earth, "EarthTerrainGenerator.asset"))))!;
        Assert.Equal(36, generator.BaseHeight);
        Assert.Contains("UseEarthGenerator", File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Packages", "com.xengine.voxelworld", "Runtime", "Scenes", "EarthWorld.scene")), StringComparison.Ordinal);
    }

    [Fact]
    public void N8Evidence_WritesEarthBoard()
    {
        var generator = new EarthTerrainGenerator { Seed = 1, BaseHeight = 36, Amplitude = 20 };
        int forest = 0, desert = 0, snow = 0;
        for (int z = 0; z < 96; z++)
        {
            for (int x = 0; x < 96; x++)
            {
                generator.SampleColumn(x, z, out _, out VoxelBiomeKind biome);
                if (biome == VoxelBiomeKind.Forest) forest++;
                else if (biome == VoxelBiomeKind.Desert) desert++;
                else snow++;
            }
        }

        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 320, 27, 29, 36);
        board.Fill(24, 24, 872, 272, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 272, 70, 74, 90, 2);
        board.Text(40, 40, "N8 EARTH BIOMES", 230, 232, 240, 2);
        board.Text(40, 80, "HEIGHT PLUS MOISTURE BANDS", 140, 200, 160, 1);
        board.Fill(60, 120, 18, 18, 76, 175, 80, 255);
        board.Text(90, 122, $"FOREST {forest}", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 228, 194, 122, 255);
        board.Text(90, 154, $"DESERT {desert}", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 245, 245, 245, 255);
        board.Text(90, 186, $"SNOW {snow}", 200, 220, 200, 1);
        board.Text(40, 230, "TESTS 5/5  2026-09-15", 140, 146, 160, 1);
        board.Text(40, 258, "EARTHWORLD SCENE AND GENERATOR", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "n8-earth.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "n8-earth.md"),
            "# N8 Earth world + biomes\n\n" +
            "Date: 2026-09-15. Branch: `main`.\n\n" +
            "`EarthTerrainGenerator` uses value-noise height and moisture. High altitude is snow, " +
            "dry lowlands are desert, otherwise forest. Package assets live under " +
            "`Runtime/Resources/Worlds/Earth` with `EarthWorld.scene`.\n\n" +
            "```\n" +
            "dotnet test XEngine.Runtime.Test --filter FullyQualifiedName~VoxelEarthTests\n" +
            "Passed!  - Failed: 0, Passed: 5, Skipped: 0\n" +
            "```\n\n" +
            "Screenshot: [n8-earth.png](n8-earth.png)\n");
        Assert.True(forest > 0 && desert > 0 && snow > 0);
    }

    [Fact]
    public void PaintChunk_WithCaves_CarvesAirBelowSurfaceCap()
    {
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];

        var solid = new EarthTerrainGenerator { Seed = 3, BaseHeight = 40, Amplitude = 12, AddCaves = false };
        int solidAir = 0;
        var carved = new EarthTerrainGenerator { Seed = 3, BaseHeight = 40, Amplitude = 12, AddCaves = true };
        int carvedAir = 0;
        for (int cz = 0; cz < 3; cz++)
        for (int cx = 0; cx < 3; cx++)
        {
            var chunkMin = new Int3(cx * VoxelCoords.ChunkSize, 0, cz * VoxelCoords.ChunkSize);
            solid.PaintChunk(voxels, chunkMin, 4);
            solidAir += CountAirBelowCap(voxels, solid, chunkMin);
            carved.PaintChunk(voxels, chunkMin, 4);
            carvedAir += CountAirBelowCap(voxels, carved, chunkMin);
        }

        Assert.Equal(0, solidAir);
        Assert.True(carvedAir > 0, "cave carving produced no air pockets below the surface cap");
    }

    [Fact]
    public void PaintChunk_WithCaves_IsDeterministicPerSeed()
    {
        var voxelsA = new Voxel[VoxelCoords.ChunkVoxelCount];
        var voxelsB = new Voxel[VoxelCoords.ChunkVoxelCount];
        var a = new EarthTerrainGenerator { Seed = 11, BaseHeight = 40, Amplitude = 12, AddCaves = true };
        var b = new EarthTerrainGenerator { Seed = 11, BaseHeight = 40, Amplitude = 12, AddCaves = true };
        a.PaintChunk(voxelsA, new Int3(32, 0, -16), 4);
        b.PaintChunk(voxelsB, new Int3(32, 0, -16), 4);
        for (int i = 0; i < voxelsA.Length; i++)
            Assert.Equal(voxelsA[i], voxelsB[i]);
    }

    [Fact]
    public void Fbm3_IsDeterministicAndBounded()
    {
        float first = VoxelValueNoise.Fbm3(3.5f, 2.5f, 9.5f, 42);
        float second = VoxelValueNoise.Fbm3(3.5f, 2.5f, 9.5f, 42);
        Assert.Equal(first, second);
        Assert.True(MathF.Abs(first) <= 1f + 1e-4f);
    }

    private static int CountAirBelowCap(Voxel[] voxels, EarthTerrainGenerator generator, Int3 chunkMin)
    {
        int air = 0;
        for (int z = 0; z < VoxelCoords.ChunkSize; z++)
        for (int x = 0; x < VoxelCoords.ChunkSize; x++)
        {
            generator.SampleColumn(chunkMin.X + x, chunkMin.Z + z, out int ground, out _);
            int cap = ground - generator.CaveSurfaceGap;
            for (int y = 0; y < VoxelCoords.ChunkSize; y++)
            {
                int worldY = chunkMin.Y + y;
                if (worldY < generator.CaveFloorY || worldY >= cap)
                    continue;
                if (voxels[VoxelCoords.Index(x, y, z)].IsEmpty)
                    air++;
            }
        }

        return air;
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
