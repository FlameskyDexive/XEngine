using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

using XEngine.Echo;
using XEngine.Editor;
using XEngine.UnityImporter.Editor;


namespace XEngine.Genshin.Editor;

/// <summary>
/// Copies asset files from an external Unity project into an XEngine project's Assets folder.
/// Unity .meta files are NOT copied: the engine mints its own metas, but texture import settings
/// are aligned from the Unity meta (sRGB / mipmaps / wrap / filter — see the setting map below).
/// A `unity-guid-map.json` sidecar (engine tooling data) records Unity-guid → relative-path so
/// the Unity YAML importers can resolve cross-references after re-GUIDing.
/// </summary>
public static partial class GenshinUnityAssetCopier
{
    public const string DefaultSourceRoot = @"F:\Git\GenshinGamePlay\Assets\AssetsPackage\Unit";

    /// <summary>File extensions copied verbatim (no importer needed — engine handles natively).</summary>
    private static readonly string[] s_textureExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tga" };

    /// <summary>File extensions claimed by the package's Unity YAML importers.</summary>
    private static readonly string[] s_unityExtensions = { ".mesh", ".prefab", ".mat" };

    [GeneratedRegex(@"^guid:\s*([0-9a-f]{32})", RegexOptions.Multiline)]
    private static partial Regex GuidRegex();

    public sealed class CopyResult
    {
        public int FilesCopied;
        public int TexturesAligned;
        public List<string> Warnings = new();
    }

    /// <summary>Copies the given unit folders (e.g. "KeQing", "Common") from the Unity source
    /// project into <paramref name="destinationDir"/> inside the current project's Assets.</summary>
    public static CopyResult CopyUnits(string sourceRoot, IEnumerable<string> unitNames, string destinationDir)
    {
        var result = new CopyResult();
        var guidMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(destinationDir);
        foreach (string unitName in unitNames)
        {
            string unitDir = Path.Combine(sourceRoot, unitName);
            if (!Directory.Exists(unitDir))
            {
                result.Warnings.Add($"Unit folder not found: {unitDir}");
                continue;
            }
            CopyDirectory(unitDir, Path.Combine(destinationDir, unitName), destinationDir, result, guidMap);
        }

        string mapPath = Path.Combine(destinationDir, UnityGuidMap.MapFileName);
        File.WriteAllText(mapPath, JsonSerializer.Serialize(guidMap, new JsonSerializerOptions { WriteIndented = true }));
        UnityGuidMap.InvalidateCache();
        return result;
    }

    private static void CopyDirectory(string source, string destination, string mapRoot, CopyResult result, Dictionary<string, string> guidMap)
    {
        Directory.CreateDirectory(destination);

        foreach (string filePath in Directory.GetFiles(source))
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            bool isTexture = Array.IndexOf(s_textureExtensions, extension) >= 0;
            if (!isTexture && Array.IndexOf(s_unityExtensions, extension) < 0)
                continue; // .meta / .anim / .controller / .asset / configs — copied by later milestones

            string destFile = Path.Combine(destination, Path.GetFileName(filePath));
            File.Copy(filePath, destFile, overwrite: true);
            result.FilesCopied++;

            string? unityGuid = ReadUnityGuid(filePath + ".meta");
            if (unityGuid != null)
                guidMap[unityGuid] = Path.GetRelativePath(mapRoot, destFile).Replace('\\', '/');

            WriteEngineMeta(destFile, isTexture, filePath + ".meta", unityGuid, result);
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            string name = Path.GetFileName(directory);
            if (name is "Edit" or "Animations") continue; // authoring timelines / M2 clips
            CopyDirectory(directory, Path.Combine(destination, name), mapRoot, result, guidMap);
        }
    }

    private static string? ReadUnityGuid(string metaPath)
    {
        if (!File.Exists(metaPath)) return null;
        using var stream = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[256];
        int read = stream.Read(buffer, 0, buffer.Length);
        Match match = GuidRegex().Match(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Writes the engine .meta. GUIDs are derived deterministically from the source
    /// Unity guid, so every machine that copies the same source gets identical engine GUIDs and
    /// scenes referencing these assets stay valid across machines. Texture import settings are
    /// aligned from the sibling Unity meta (Unity → engine setting names in the comments).</summary>
    private static void WriteEngineMeta(string destFile, bool isTexture, string unityMetaPath, string? unityGuid, CopyResult result)
    {
        string importerName = isTexture ? "TextureImporter" : UnityImporterNameFor(Path.GetExtension(destFile));
        EchoObject? settings = null;
        if (isTexture)
        {
            settings = UnityTextureMetaAligner.AlignSettings(unityMetaPath, result.Warnings);
            result.TexturesAligned++;
        }

        var meta = new MetaFileData
        {
            Guid = unityGuid != null ? StableGuid(unityGuid) : Guid.NewGuid(),
            ImporterType = importerName,
            ImporterVersion = 1,
            Settings = settings,
        };
        MetaFile.Write(MetaFile.GetMetaPath(destFile), meta);
    }

    /// <summary>Deterministic engine GUID for a Unity source guid (MD5-derived, stable across
    /// machines and runs).</summary>
    public static Guid StableGuid(string unityGuid)
    {
        byte[] hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("xengine:genshin:unity-guid:" + unityGuid));
        return new Guid(hash);
    }

    private static string UnityImporterNameFor(string extension) => extension switch
    {
        ".mesh" => "UnityMeshImporter",
        ".prefab" => "UnityPrefabImporter",
        ".mat" => "UnityMatImporter",
        ".anim" => "UnityAnimImporter",
        _ => "DefaultImporter",
    };
}
