using System.Drawing;
using System.Runtime.CompilerServices;

using XEngine.Editor.GUI.Registries;
using XEngine.Editor.Theming;
using XEngine.Terrain;

namespace XEngine.Terrain.Editor;

/// <summary>Marker used by package/build audits to prove Terrain Editor is present.</summary>
public static class TerrainPackageEditor
{
    public const string PackageName = TerrainRuntime.PackageName;
    public const string Version = TerrainRuntime.Version;

    [ModuleInitializer]
    internal static void RegisterAssetStyles()
    {
        var style = AssetTypeStyles.UnityType("TerrainData", EditorIcons.Mountain_I, Color.FromArgb(255, 74, 222, 128));
        AssetTypeStyles.Register(".terrainlayer", style);
        AssetTypeStyles.Register(".terraindata", style);
    }
}
