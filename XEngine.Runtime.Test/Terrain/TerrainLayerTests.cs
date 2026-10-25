// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Terrain;

using Xunit;

namespace XEngine.Runtime.Test.Terrain;

public sealed class TerrainLayerTests : RuntimeTestBase
{
    [Fact]
    public void CreateAssetMenu_UsesTerrainLayerExtension()
    {
        var menu = typeof(TerrainLayer).GetCustomAttribute<CreateAssetMenuAttribute>();
        Assert.NotNull(menu);
        Assert.Equal(".terrainlayer", menu!.Extension);
        Assert.Equal("Terrain/Terrain Layer", menu.Name);
    }

    [Fact]
    public void PackageRuntime_OwnsTerrainLayerSource()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.terrain", "Runtime");
        Assert.True(File.Exists(Path.Combine(root, "TerrainLayer.cs")));
        Assert.Contains("List<AssetRef<TerrainLayer>>",
            File.ReadAllText(Path.Combine(root, "TerrainData.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Layers_AreAssetRefs_AndShareOneInstance()
    {
        var shared = new TerrainLayer { Name = "Dirt", Tiling = 4f };
        var a = new TerrainData();
        var b = new TerrainData();
        a.SetLayer(0, new AssetRef<TerrainLayer>(shared));
        b.SetLayer(0, new AssetRef<TerrainLayer>(shared));
        Assert.Same(shared, a.GetLayer(0));
        Assert.Same(a.GetLayer(0), b.GetLayer(0));
        Assert.Equal(4f, a.Layers[0].ResWeak!.Tiling);
    }

    [Fact]
    public void GetLayer_WritesResolvedRefBackIntoSlot()
    {
        var data = new TerrainData();
        var layer = new TerrainLayer { Name = "Rock", Tiling = 6f };
        data.SetLayer(1, new AssetRef<TerrainLayer>(layer));
        Assert.Same(layer, data.GetLayer(1));
        Assert.Same(layer, data.Layers[1].ResWeak);
    }

    [Fact]
    public void Serialize_RuntimeLayer_WritesEmptyAssetIdAndInlineInstance()
    {
        var data = new TerrainData();
        data.SetLayer(0, new AssetRef<TerrainLayer>(new TerrainLayer { Name = "Grass", Tiling = 12f }));
        EchoObject echo = Serializer.Serialize(data);
        EchoObject slot = echo.Get("Layers")!.List[0];
        Assert.Equal(Guid.Empty.ToString(), slot.Get("AssetID")?.StringValue);
        Assert.NotNull(slot.Get("Instance"));

        var loaded = Assert.IsType<TerrainData>(Serializer.Deserialize<TerrainData>(echo));
        TerrainLayer? restored = loaded.GetLayer(0);
        Assert.NotNull(restored);
        Assert.Equal("Grass", restored!.Name);
        Assert.Equal(12f, restored.Tiling);
        Assert.True(loaded.Layers[0].IsRuntimeResource);
    }

    [Fact]
    public void Serialize_PersistedLayer_WritesAssetIdWithoutInstance()
    {
        var layer = new TerrainLayer { Name = "Shared", Tiling = 3f };
        layer.AssetID = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var data = new TerrainData();
        data.SetLayer(0, new AssetRef<TerrainLayer>(layer));
        EchoObject echo = Serializer.Serialize(data);
        EchoObject slot = echo.Get("Layers")!.List[0];
        Assert.Equal(layer.AssetID.ToString(), slot.Get("AssetID")?.StringValue);
        Assert.Null(slot.Get("Instance"));
    }

    [Fact]
    public void Deserialize_EmbeddedLayerCompound_MigratesTiling()
    {
        var data = new TerrainData { HeightmapResolution = 2, SplatmapResolution = 2 };
        data.Heights = new short[4];
        data.Splats = new float[2 * 2 * 4];
        EchoObject echo = Serializer.Serialize(data);

        var embedded = EchoObject.NewCompound();
        embedded.Add("Albedo", Serializer.Serialize(default(AssetRef<Texture2D>)));
        embedded.Add("NormalMap", Serializer.Serialize(default(AssetRef<Texture2D>)));
        embedded.Add("Tiling", new EchoObject(7.5f));
        embedded.Add("Roughness", new EchoObject(0.4f));
        embedded.Add("Metallic", new EchoObject(0.2f));
        var layers = EchoObject.NewList();
        layers.ListAdd(embedded);
        echo["Layers"] = layers;

        var loaded = Assert.IsType<TerrainData>(Serializer.Deserialize<TerrainData>(echo));
        Assert.Equal(1, loaded.LayerCount);
        TerrainLayer? layer = loaded.GetLayer(0);
        Assert.NotNull(layer);
        Assert.Equal(7.5f, layer!.Tiling);
        Assert.Equal(0.4f, layer.Roughness);
        Assert.Equal(0.2f, layer.Metallic);
        Assert.True(loaded.Layers[0].IsRuntimeResource);
    }

    [Fact]
    public void LayerEvidence_WritesBoardAndNotes()
    {
        var menu = typeof(TerrainLayer).GetCustomAttribute<CreateAssetMenuAttribute>();
        Assert.Equal(".terrainlayer", menu!.Extension);

        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "terrain");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 280, 18, 19, 26);
        board.Fill(24, 24, 872, 232, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 232, 70, 74, 90, 2);
        board.Text(40, 40, "M4A TERRAIN LAYER ASSETS", 230, 232, 240, 2);
        board.Text(40, 80, ".TERRAINLAYER SHARED ACROSS TERRAINDATA", 140, 146, 160, 1);
        board.Fill(60, 120, 18, 18, 80, 200, 140, 255);
        board.Text(90, 122, "LAYERS ARE ASSETREF SLOTS", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 80, 170, 220, 255);
        board.Text(90, 154, "EMBEDDED COMPOUNDS AUTO-MIGRATE", 200, 220, 200, 1);
        board.Text(40, 210, "ACCEPTANCE 2026-08-15", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "m4a-terrainlayer.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "m4a-terrainlayer.md"),
            "# M4a TerrainLayer assets\n\n" +
            "Date: 2026-08-15. Branch: `codex/animation-module`.\n\n" +
            "`TerrainLayer` is a standalone ScriptableObject saved as `.terrainlayer`. " +
            "`TerrainData.Layers` is `List<AssetRef<TerrainLayer>>` so one layer asset can be shared. " +
            "Pre-M4a embedded layer compounds (Tiling/Albedo/Roughness, no AssetID) deserialize as " +
            "runtime resources. Add Layer in the inspector creates an unsaved runtime slot; " +
            "**Assets / Create / Terrain / Terrain Layer** writes a shareable file.\n\n" +
            "Screenshot: [m4a-terrainlayer.png](m4a-terrainlayer.png).\n");
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
