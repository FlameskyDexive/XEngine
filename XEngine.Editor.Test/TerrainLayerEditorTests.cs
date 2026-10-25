// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;

using XEngine.Editor.GUI.Registries;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Terrain;
using XEngine.Terrain.Editor;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class TerrainLayerEditorTests
{
    [Fact]
    public void Package_OwnsTerrainLayerImporter()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.terrain");
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "TerrainLayer.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "TerrainLayerImporter.cs")));
        var attr = typeof(TerrainLayerImporter).GetCustomAttribute<ImporterForAttribute>();
        Assert.NotNull(attr);
        Assert.Contains(".terrainlayer", attr!.Extensions);
    }

    [Fact]
    public void CreateAssetMenu_RegistersTerrainLayer()
    {
        var menu = typeof(TerrainLayer).GetCustomAttribute<CreateAssetMenuAttribute>();
        Assert.NotNull(menu);
        Assert.Equal(".terrainlayer", menu!.Extension);
        Assert.Equal("Terrain/Terrain Layer", menu.Name);
    }

    [Fact]
    public void AssetTypeStyles_MapsTerrainLayerExtension()
    {
        TerrainPackageEditor.RegisterAssetStyles();
        Assert.NotNull(AssetTypeStyles.For(".terrainlayer").Icon);
        Assert.NotNull(AssetTypeStyles.For(".terraindata").Icon);
        Assert.NotSame(AssetTypeStyles.For(".unknown_ext_zzz").Icon, AssetTypeStyles.For(".terrainlayer").Icon);
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
