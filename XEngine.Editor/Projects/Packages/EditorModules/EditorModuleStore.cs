// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XEngine.Editor.Projects.Packages.EditorModules;

/// <summary>One installed editor-level module, as recorded in <c>modules.json</c>.</summary>
public sealed class EditorModuleRecord
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("installedAtUtc")] public DateTime InstalledAtUtc { get; set; }
    /// <summary>Acquisition channel; "npm" for registry-installed modules.</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "npm";
    /// <summary>Registry base URL the module was fetched from (audit trail for repro).</summary>
    [JsonPropertyName("registry")] public string? Registry { get; set; }
}

/// <summary>
/// Editor-level module store: a per-engine-version, per-user directory of npm-installed modules
/// (<c>%LOCALAPPDATA%\XEngine\EditorModules\&lt;version&gt;\</c>) that is fully independent of any
/// open project — the launcher and Build profiles read it before touching a project's
/// PackageManager. Layout:
/// <code>
/// EditorModules/&lt;version&gt;/
///   modules.json              # { "modules": [ { name, version, installedAtUtc, source, registry } ] }
///   cache/&lt;name&gt;@&lt;ver&gt;/      # extracted package content, mounted read-only
/// </code>
/// Version scoping mirrors Unity's per-install modules: upgrading the Editor starts a fresh store
/// instead of silently reusing modules built against an older engine.
/// </summary>
public static class EditorModuleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Tests inject an isolated store root; production resolves the per-version path under
    /// %LOCALAPPDATA%.</summary>
    internal static string? RootOverride { get; set; }

    public static string Root => RootOverride ?? GetDefaultRoot(EngineVersionFolderName());

    /// <summary>Directory holding extracted module contents (<c>cache/&lt;name&gt;@&lt;ver&gt;/</c>).</summary>
    public static string CacheRoot => Path.Combine(Root, "cache");

    /// <summary>The store's index file, sibling of <c>cache/</c>.</summary>
    public static string ManifestPath => Path.Combine(Root, "modules.json");

    /// <summary>Default store root for a given engine version, exposed for tests. The version's
    /// build metadata (<c>+</c> suffix) is stripped and any remaining filesystem-hostile characters
    /// are sanitized so CI versions like <c>1.0.0-a1.deadbeef</c> stay one folder.</summary>
    internal static string GetDefaultRoot(string engineVersion)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
            throw new InvalidOperationException("No per-user local app data directory is available.");
        int plus = engineVersion.IndexOf('+');
        if (plus >= 0) engineVersion = engineVersion[..plus];
        return Path.Combine(localAppData, "XEngine", "EditorModules", SanitizeSegment(engineVersion));
    }

    /// <summary>Running engine version in folder-safe form (matches EditorApplication.EngineVersion
    /// semantics: informational version, build metadata stripped).</summary>
    internal static string EngineVersionFolderName()
    {
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "0.0.1";
        int plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        return SanitizeSegment(version);
    }

    /// <summary>Modules recorded in <c>modules.json</c>; empty when nothing is installed or the
    /// index is unreadable (a corrupt index must never take the editor down).</summary>
    public static IReadOnlyList<EditorModuleRecord> ListInstalled()
    {
        var file = ReadStoreFile();
        return file?.Modules ?? (IReadOnlyList<EditorModuleRecord>)Array.Empty<EditorModuleRecord>();
    }

    public static bool IsInstalled(string name)
        => FindRecord(name) != null;

    public static bool TryGetInstalled(string name, out EditorModuleRecord record)
    {
        record = FindRecord(name)!;
        return record != null;
    }

    /// <summary>Installed record plus its on-disk cache root, when the recorded version's content is
    /// actually present (a record without content — e.g. the cache was manually wiped — reports
    /// false so callers fall back to reinstalling).</summary>
    public static bool TryGetModuleRoot(string name, out EditorModuleRecord record, out string root)
    {
        record = FindRecord(name)!;
        if (record == null)
        {
            root = string.Empty;
            return false;
        }
        root = Path.Combine(CacheRoot, $"{record.Name}@{record.Version}");
        return Directory.Exists(root) && File.Exists(Path.Combine(root, "package.json"));
    }

    /// <summary>Insert or replace the record for <paramref name="record"/>.Name and persist
    /// <c>modules.json</c> atomically (write + move).</summary>
    internal static void UpsertRecord(EditorModuleRecord record)
    {
        var file = ReadStoreFile() ?? new EditorModuleStoreFile();
        for (int i = 0; i < file.Modules.Count; i++)
        {
            if (string.Equals(file.Modules[i].Name, record.Name, StringComparison.OrdinalIgnoreCase))
            {
                file.Modules[i] = record;
                WriteStoreFile(file);
                return;
            }
        }
        file.Modules.Add(record);
        WriteStoreFile(file);
    }

    /// <summary>Drop the record for <paramref name="name"/>. Returns false when it was not recorded.</summary>
    internal static bool RemoveRecord(string name)
    {
        var file = ReadStoreFile();
        if (file == null) return false;
        bool removed = false;
        for (int i = file.Modules.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(file.Modules[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            file.Modules.RemoveAt(i);
            removed = true;
        }
        if (removed) WriteStoreFile(file);
        return removed;
    }

    /// <summary>Scan the cache for an existing <c>&lt;name&gt;@&lt;ver&gt;/</c> whose version satisfies
    /// <paramref name="range"/> (highest wins) — the offline fast path that lets a reinstall complete
    /// with no network.</summary>
    internal static string? FindCachedRoot(string name, VersionRange range)
    {
        if (!Directory.Exists(CacheRoot)) return null;
        string? best = null;
        SemanticVersion? bestVersion = null;
        foreach (string dir in Directory.EnumerateDirectories(CacheRoot, name + "@*", SearchOption.TopDirectoryOnly))
        {
            if (!File.Exists(Path.Combine(dir, "package.json"))) continue;
            string version = dir[(dir.LastIndexOf('@') + 1)..];
            if (!SemanticVersion.TryParse(version, out SemanticVersion parsed)) continue;
            if (!range.Satisfies(parsed)) continue;
            if (bestVersion == null || parsed > bestVersion) { bestVersion = parsed; best = dir; }
        }
        return best;
    }

    /// <summary>Delete every cached version of <paramref name="name"/> except <paramref name="keepVersion"/>
    /// (null keeps nothing). The store pins one version per module; stale copies are garbage.</summary>
    internal static void PruneOtherVersions(string name, string? keepVersion)
    {
        if (!Directory.Exists(CacheRoot)) return;
        foreach (string dir in Directory.EnumerateDirectories(CacheRoot, name + "@*", SearchOption.TopDirectoryOnly))
        {
            string version = dir[(dir.LastIndexOf('@') + 1)..];
            if (keepVersion != null && string.Equals(version, keepVersion, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    private static EditorModuleRecord? FindRecord(string name)
    {
        foreach (EditorModuleRecord record in ListInstalled())
            if (string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
                return record;
        return null;
    }

    private static EditorModuleStoreFile? ReadStoreFile()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return null;
            return JsonSerializer.Deserialize<EditorModuleStoreFile>(File.ReadAllText(ManifestPath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Runtime.Debug.LogWarning($"[EditorModules] Store index '{ManifestPath}' is unreadable: {ex.Message}");
            return null;
        }
    }

    private static void WriteStoreFile(EditorModuleStoreFile file)
    {
        Directory.CreateDirectory(Root);
        string temp = ManifestPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(temp, ManifestPath, overwrite: true);
    }

    private static string SanitizeSegment(string value)
    {
        // Sanitize against the Windows-invalid set on every host: module roots must hash to the
        // SAME folder name cross-platform, and characters like '|' are legal on macOS/Linux but
        // would break the moment a cache is shared with (or authored on) Windows.
        var invalid = WindowsInvalidFileNameChars;
        var chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '-';
        }
        return new string(chars);
    }

    private static readonly char[] WindowsInvalidFileNameChars =
    [
        '\"', '<', '>', '|', '\0',
        (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8,
        (char)9, (char)10, (char)11, (char)12, (char)13, (char)14, (char)15,
        (char)16, (char)17, (char)18, (char)19, (char)20, (char)21, (char)22,
        (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29,
        (char)30, (char)31, ':', '*', '?', '\\', '/',
    ];
}

/// <summary>On-disk shape of <c>modules.json</c>.</summary>
internal sealed class EditorModuleStoreFile
{
    [JsonPropertyName("modules")] public List<EditorModuleRecord> Modules { get; set; } = new();
}
