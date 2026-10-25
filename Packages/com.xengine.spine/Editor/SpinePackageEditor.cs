using XEngine.Spine;

namespace XEngine.Spine.Editor;

/// <summary>Marker used by package/build audits to prove Spine Editor is present.</summary>
public static class SpinePackageEditor
{
    public const string PackageName = SpineRuntime.PackageName;
    public const string Version = SpineRuntime.Version;
}
