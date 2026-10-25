using XEngine.Genshin;

namespace XEngine.Genshin.Editor;

/// <summary>Marker used by package/build audits to prove Genshin Editor is present.</summary>
public static class GenshinPackageEditor
{
    public const string PackageName = GenshinRuntime.PackageName;
    public const string Version = GenshinRuntime.Version;
}
