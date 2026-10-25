using System.Drawing;
using System.Runtime.CompilerServices;

using XEngine.Editor.GUI.Registries;
using XEngine.Editor.Theming;

namespace XEngine.GpuAnimation.Editor;

/// <summary>Marker used by package/build audits to prove GpuAnimation Editor tooling is present.</summary>
public static class GpuAnimationPackageEditor
{
    public const string PackageName = GpuAnimationRuntime.PackageName;
    public const string Version = GpuAnimationRuntime.Version;

    [ModuleInitializer]
    internal static void RegisterAssetStyles()
    {
        var style = AssetTypeStyles.UnityType("BakedGpuAnimation", EditorIcons.Film_I, Color.FromArgb(255, 52, 211, 238));
        AssetTypeStyles.Register(".gpuanim", style);
    }
}
