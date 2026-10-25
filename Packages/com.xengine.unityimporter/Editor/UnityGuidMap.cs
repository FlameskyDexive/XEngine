using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;


namespace XEngine.UnityImporter.Editor;

/// <summary>
/// Resolves Unity asset GUIDs (as written inside copied .prefab/.mat YAML) to project paths via
/// the `unity-guid-map.json` sidecar written by the per-game asset copier tooling. The sidecar
/// is engine-generated tooling data — Unity's own .meta files are never copied into the project.
/// </summary>
public static class UnityGuidMap
{
    public const string MapFileName = "unity-guid-map.json";

    private static readonly Dictionary<string, Dictionary<string, string>> s_maps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves a Unity guid to an absolute path by walking up from the asset to the
    /// nearest enclosing sidecar. Returns null when unmapped.</summary>
    public static string? ResolveToAbsolutePath(string assetPath, string unityGuid)
    {
        if (string.IsNullOrEmpty(unityGuid)) return null;
        DirectoryInfo? dir = new(Path.GetDirectoryName(Path.GetFullPath(assetPath))!);
        while (dir != null)
        {
            string mapPath = Path.Combine(dir.FullName, MapFileName);
            if (File.Exists(mapPath) && TryGetMap(mapPath, out Dictionary<string, string> map)
                && map.TryGetValue(unityGuid, out string? relative))
            {
                return Path.GetFullPath(Path.Combine(dir.FullName, relative));
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static bool TryGetMap(string mapPath, out Dictionary<string, string> map)
    {
        if (s_maps.TryGetValue(mapPath, out map!)) return true;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(mapPath));
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
                result[property.Name] = property.Value.GetString() ?? string.Empty;
            s_maps[mapPath] = result;
            map = result;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Test hook: clears cached sidecars so a rewritten map is re-read.</summary>
    public static void InvalidateCache() => s_maps.Clear();
}
