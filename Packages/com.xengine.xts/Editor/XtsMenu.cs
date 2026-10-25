using System;
using System.IO;
using XEngine.Editor;
using XEngine.Runtime;

namespace XEngine.XTS.Editor;

internal static class XtsMenu
{
    private const string PackageRoot = "Packages/com.xengine.xts";

    [MenuItem("Window/XTS/Native Manifest Status", priority: 24)]
    private static void ReportNativeManifestStatus()
    {
        try
        {
            var manifest = XtsNativeManifest.Load(FindPackageRoot());
            Debug.Log($"[XTS] native manifest: abiVersion={manifest.AbiVersion} quickjs={manifest.QuickjsVersion} " +
                      $"platforms=[{string.Join(", ", manifest.Platforms.Keys)}]");
            foreach (var platform in manifest.Platforms.Keys)
            {
                var errors = manifest.Validate(platform, FindPackageRoot());
                if (errors.Count == 0)
                    Debug.Log($"[XTS] native manifest platform '{platform}': OK");
                else
                    Debug.LogError($"[XTS] native manifest platform '{platform}' rejected:\n  " + string.Join("\n  ", errors));
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[XTS] native manifest could not be loaded: {e.Message}");
        }
    }

    /// <summary>
    /// The package may live in the editor install (built-in) or the project's
    /// package cache; probe both from a known file inside the package.
    /// </summary>
    internal static string FindPackageRoot()
    {
        var probe = typeof(XtsMenu).Assembly.Location;
        var dir = Path.GetDirectoryName(probe) ?? string.Empty;
        for (var i = 0; i < 8 && dir.Length > 1; i++, dir = Path.GetDirectoryName(dir)!)
        {
            if (File.Exists(Path.Combine(dir, "package.json"))
                && File.Exists(Path.Combine(dir, "Native", "native-manifest.json")))
                return dir;
        }
        return PackageRoot;
    }
}
