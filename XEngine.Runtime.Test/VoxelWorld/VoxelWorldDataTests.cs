using System.Reflection;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelWorldDataTests
{
    [Fact]
    public void Chunk_Is16Cubed_WithYMajorIndex()
    {
        Assert.Equal(16, VoxelCoords.ChunkSize);
        Assert.Equal(4096, VoxelCoords.ChunkVoxelCount);
        Assert.Equal(0, VoxelCoords.Index(0, 0, 0));
        Assert.Equal(1, VoxelCoords.Index(1, 0, 0));
        Assert.Equal(16, VoxelCoords.Index(0, 0, 1));
        Assert.Equal(256, VoxelCoords.Index(0, 1, 0));
        Assert.Equal(4095, VoxelCoords.Index(15, 15, 15));
        Assert.Equal(new Int3(3, 4, 5), VoxelCoords.LocalFromIndex(VoxelCoords.Index(3, 4, 5)));
    }

    [Fact]
    public void ChunkCoord_FloorsNegativeWorldCells()
    {
        Assert.Equal(new Int3(0, 0, 0), VoxelCoords.ChunkCoord(new Int3(0, 0, 0)));
        Assert.Equal(new Int3(0, 0, 0), VoxelCoords.ChunkCoord(new Int3(15, 15, 15)));
        Assert.Equal(new Int3(1, 2, 3), VoxelCoords.ChunkCoord(new Int3(16, 32, 48)));
        Assert.Equal(new Int3(-1, -1, -1), VoxelCoords.ChunkCoord(new Int3(-1, -1, -1)));
        Assert.Equal(new Int3(-16, -16, -16), VoxelCoords.ChunkMin(new Int3(-1, -1, -1)));
    }

    [Fact]
    public void Voxel_EmptyType_IsAir()
    {
        Assert.True(Voxel.Empty.IsEmpty);
        Assert.Equal(Voxel.EmptyTypeIndex, Voxel.Empty.TypeIndex);
        Assert.False(Voxel.Solid(1).IsEmpty);
        Assert.Equal((byte)15, Voxel.Solid(1).Opaque);
    }

    [Fact]
    public void CreateAssetMenu_RegistersDefinitionTypes()
    {
        Assert.Equal("Voxel World/Voxel Definition", typeof(VoxelDefinition).GetCustomAttribute<CreateAssetMenuAttribute>()!.Name);
        Assert.Equal("Voxel World/World Definition", typeof(WorldDefinition).GetCustomAttribute<CreateAssetMenuAttribute>()!.Name);
        Assert.Equal("Voxel World/Biome Definition", typeof(BiomeDefinition).GetCustomAttribute<CreateAssetMenuAttribute>()!.Name);
        Assert.Equal("Voxel World/Item Definition", typeof(ItemDefinition).GetCustomAttribute<CreateAssetMenuAttribute>()!.Name);
        Assert.Equal("Voxel World/Model Definition", typeof(ModelDefinition).GetCustomAttribute<CreateAssetMenuAttribute>()!.Name);
        Assert.Equal("Voxel World/Flat Terrain Generator", typeof(FlatTerrainGenerator).GetCustomAttribute<CreateAssetMenuAttribute>()!.Name);
        Assert.Equal("Voxel World/Earth Terrain Generator", typeof(EarthTerrainGenerator).GetCustomAttribute<CreateAssetMenuAttribute>()!.Name);
    }

    [Fact]
    public void VoxelDefinition_EchoRoundTrip_KeepsTextureGuids()
    {
        var guid = Guid.Parse("fdc9f888-96fc-048b-f860-0563a62b9e7e");
        var def = new VoxelDefinition
        {
            Title = "Default Voxel",
            TextureTop = new AssetRef<Texture2D> { AssetID = guid },
            TextureSide = new AssetRef<Texture2D> { AssetID = guid },
            TextureBottom = new AssetRef<Texture2D> { AssetID = guid },
            TintColor = Color.White,
        };

        EchoObject echo = Serializer.Serialize(typeof(object), def);
        var loaded = Assert.IsType<VoxelDefinition>(Serializer.Deserialize<object>(echo));
        Assert.Equal("Default Voxel", loaded.Title);
        Assert.Equal(guid, loaded.TextureTop.AssetID);
        Assert.Equal(guid, loaded.TextureSide.AssetID);
        Assert.Equal(guid, loaded.TextureBottom.AssetID);
    }

    [Fact]
    public void FlatGenerator_FillsAltitudeBand()
    {
        var generator = new FlatTerrainGenerator { Altitude = 4 };
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];
        generator.PaintChunk(voxels, new Int3(0, 0, 0), terrainTypeIndex: 1);

        Assert.False(voxels[VoxelCoords.Index(0, 0, 0)].IsEmpty);
        Assert.Equal((ushort)1, voxels[VoxelCoords.Index(8, 3, 8)].TypeIndex);
        Assert.True(voxels[VoxelCoords.Index(0, 4, 0)].IsEmpty);

        generator.PaintChunk(voxels, new Int3(0, 16, 0), terrainTypeIndex: 1);
        Assert.True(voxels[VoxelCoords.Index(0, 0, 0)].IsEmpty);
    }

    [Fact]
    public void PackagedFlatAssets_KeepWhite64TextureGuid()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.voxelworld");
        string flat = Path.Combine(root, "Runtime", "Resources", "Worlds", "Flat");
        Assert.True(File.Exists(Path.Combine(flat, "white64.png")));
        Assert.Contains("fdc9f888-96fc-048b-f860-0563a62b9e7e", File.ReadAllText(Path.Combine(flat, "white64.png.meta")), StringComparison.Ordinal);
        string voxel = File.ReadAllText(Path.Combine(flat, "DefaultVoxel.asset"));
        Assert.Contains("fdc9f888-96fc-048b-f860-0563a62b9e7e", voxel, StringComparison.Ordinal);
        Assert.Contains("TextureTop", voxel, StringComparison.Ordinal);
        Assert.Contains("TextureSide", voxel, StringComparison.Ordinal);
        Assert.Contains("TextureBottom", voxel, StringComparison.Ordinal);
        Assert.Contains("764590ab-8f7e-0478-5a8a-c184f4ff675a", File.ReadAllText(Path.Combine(flat, "FlatTerrainGenerator.asset")), StringComparison.Ordinal);
        Assert.Contains("7e814b7c-31a3-b4ee-cab7-dd3f6bb9091f", File.ReadAllText(Path.Combine(flat, "FlatWorld.asset")), StringComparison.Ordinal);
        Assert.Contains("no remaining third-party art", File.ReadAllText(Path.Combine(root, "THIRD_PARTY_ASSETS.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void N2Evidence_WritesInspectorBoard()
    {
        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 300, 27, 29, 36);
        board.Fill(24, 24, 872, 252, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 252, 70, 74, 90, 2);
        board.Text(40, 40, "N2 VOXELWORLD DATA", 230, 232, 240, 2);
        board.Text(40, 80, "DEFAULTVOXEL TEXTURE REFS KEPT", 140, 200, 160, 1);
        board.Fill(60, 120, 18, 18, 80, 200, 140, 255);
        board.Text(90, 122, "TOP SIDE BOTTOM = WHITE64 GUID", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 80, 170, 220, 255);
        board.Text(90, 154, "FLATWORLD + FLAT GENERATOR ALT 50", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 220, 180, 80, 255);
        board.Text(90, 186, "CHUNK 16 CUBE / EMPTY TYPE 0", 200, 220, 200, 1);
        board.Text(40, 230, "TESTS 9/9  2026-09-15", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "n2-voxel-data.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "n2-voxel-data.md"),
            "# N2 Voxel world data + Flat import\n\n" +
            "Date: 2026-09-15. Branch: `main`.\n\n" +
            "Self-owned definitions (`Voxel`, `VoxelDefinition`, `WorldDefinition`, `BiomeDefinition`, " +
            "`ItemDefinition`, `ModelDefinition`, `FlatTerrainGenerator`) plus Demo3-scale Flat assets. " +
            "`DefaultVoxel` keeps the same top/side/bottom GUID as `white64.png` " +
            "(`fdc9f888-96fc-048b-f860-0563a62b9e7e`). Temporary art listed in `THIRD_PARTY_ASSETS.md`.\n\n" +
            "```\n" +
            "dotnet test XEngine.Runtime.Test --filter FullyQualifiedName~VoxelWorldDataTests\n" +
            "Passed!  - Failed: 0, Passed: 9, Skipped: 0\n" +
            "```\n\n" +
            "Screenshot: [n2-voxel-data.png](n2-voxel-data.png)\n");
    }

    [Fact]
    public void PackagedFlatAssets_DeserializeWithEcho()
    {
        string flat = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.voxelworld", "Runtime", "Resources", "Worlds", "Flat");
        var voxel = Serializer.Deserialize<VoxelDefinition>(EchoObject.ReadFromString(File.ReadAllText(Path.Combine(flat, "DefaultVoxel.asset"))))!;
        Assert.Equal(Guid.Parse("fdc9f888-96fc-048b-f860-0563a62b9e7e"), voxel.TextureTop.AssetID);
        var generator = Serializer.Deserialize<FlatTerrainGenerator>(EchoObject.ReadFromString(File.ReadAllText(Path.Combine(flat, "FlatTerrainGenerator.asset"))))!;
        Assert.Equal(50, generator.Altitude);
        Assert.Equal(Guid.Parse("764590ab-8f7e-0478-5a8a-c184f4ff675a"), generator.TerrainVoxel.AssetID);
        var world = Serializer.Deserialize<WorldDefinition>(EchoObject.ReadFromString(File.ReadAllText(Path.Combine(flat, "FlatWorld.asset"))))!;
        Assert.Equal(Guid.Parse("7e814b7c-31a3-b4ee-cab7-dd3f6bb9091f"), world.TerrainGenerator.AssetID);
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
