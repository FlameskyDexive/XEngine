// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XEngine.Editor.Projects.Packages;

/// <summary>
/// The reproducible package resolution record stored in <c>Packages/packages-lock.json</c>.
/// Physical paths are represented relative to the project whenever possible; package assets and
/// serialized data continue to use <c>Packages/&lt;name&gt;/...</c> logical paths.
/// </summary>
public sealed class PackageLockFile
{
    public const string DefaultFileName = "packages-lock.json";
    public const int CurrentFormatVersion = 1;
    public const string CurrentResolverVersion = "xengine-package-resolver/1";

    [JsonPropertyName("lockfileVersion")]
    public int LockfileVersion { get; set; } = CurrentFormatVersion;

    [JsonPropertyName("resolverVersion")]
    public string ResolverVersion { get; set; } = CurrentResolverVersion;

    [JsonPropertyName("manifestHash")]
    public string ManifestHash { get; set; } = string.Empty;

    [JsonPropertyName("generatedAtUtc")]
    public string GeneratedAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("packages")]
    public List<PackageLockEntry> Packages { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string GetPath(Project project)
        => Path.Combine(project.PackagesPath, DefaultFileName);

    public static PackageLockFile? Read(Project project)
        => Read(GetPath(project));

    public static PackageLockFile? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            var value = JsonSerializer.Deserialize<PackageLockFile>(json, JsonOptions);
            if (value == null || value.LockfileVersion != CurrentFormatVersion)
            {
                Runtime.Debug.LogWarning($"[Packages] Ignoring unsupported lock file '{path}'.");
                return null;
            }
            value.Packages ??= new List<PackageLockEntry>();
            return value;
        }
        catch (JsonException ex)
        {
            Runtime.Debug.LogWarning($"[Packages] Ignoring invalid lock file '{path}': {ex.Message}");
            return null;
        }
        catch (IOException ex)
        {
            Runtime.Debug.LogWarning($"[Packages] Could not read lock file '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Write a canonical lock file. Package and dependency order is normalized before
    /// serialization so equivalent resolutions produce byte-for-byte identical content apart from
    /// the intentionally informational generation timestamp.</summary>
    public void Write(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        LockfileVersion = CurrentFormatVersion;
        ResolverVersion = CurrentResolverVersion;
        Packages.Sort(static (a, b) => CompareNames(a.Name, b.Name));
        for (int i = 0; i < Packages.Count; i++)
            Packages[i].Normalize();

        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions) + "\n", new UTF8Encoding(false));
    }

    internal static int CompareNames(string left, string right)
    {
        int c = StringComparer.OrdinalIgnoreCase.Compare(left, right);
        return c != 0 ? c : StringComparer.Ordinal.Compare(left, right);
    }

    public static string ComputeManifestHash(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return string.Empty;
        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))).ToLowerInvariant();
        }
        catch (IOException) { return string.Empty; }
    }

    public static string ToProjectRelativePath(Project project, string physicalRoot)
    {
        string fullRoot = Path.GetFullPath(physicalRoot);
        string projectRoot = Path.GetFullPath(project.RootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string prefix = projectRoot + Path.DirectorySeparatorChar;
        if (fullRoot.Equals(projectRoot, StringComparison.OrdinalIgnoreCase)) return ".";
        if (fullRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(projectRoot, fullRoot).Replace('\\', '/');
        return string.Empty;
    }

    public static string NormalizeSourcePath(string path)
        => path.Replace('\\', '/');

    /// <summary>Compare resolution content while intentionally ignoring the informational timestamp.</summary>
    public static bool ContentEquals(PackageLockFile left, PackageLockFile right)
    {
        if (!string.Equals(left.ManifestHash, right.ManifestHash, StringComparison.Ordinal)
            || !string.Equals(left.ResolverVersion, right.ResolverVersion, StringComparison.Ordinal)
            || left.LockfileVersion != right.LockfileVersion
            || left.Packages.Count != right.Packages.Count) return false;

        var a = new List<PackageLockEntry>(left.Packages);
        var b = new List<PackageLockEntry>(right.Packages);
        a.Sort(static (x, y) => CompareNames(x.Name, y.Name));
        b.Sort(static (x, y) => CompareNames(x.Name, y.Name));
        for (int i = 0; i < a.Count; i++)
        {
            a[i].Normalize();
            b[i].Normalize();
            if (!EntryContentEquals(a[i], b[i])) return false;
        }
        return true;
    }

    private static bool EntryContentEquals(PackageLockEntry left, PackageLockEntry right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            || !string.Equals(left.Version, right.Version, StringComparison.Ordinal)
            || !string.Equals(left.Source, right.Source, StringComparison.Ordinal)
            || !string.Equals(left.SourceSpecifier, right.SourceSpecifier, StringComparison.Ordinal)
            || !string.Equals(left.LogicalRoot, right.LogicalRoot, StringComparison.Ordinal)
            || !string.Equals(left.ResolvedPath, right.ResolvedPath, StringComparison.Ordinal)
            || !string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal)
            || !string.Equals(left.GitUrl, right.GitUrl, StringComparison.Ordinal)
            || !string.Equals(left.GitRef, right.GitRef, StringComparison.Ordinal)
            || !string.Equals(left.GitCommit, right.GitCommit, StringComparison.Ordinal)
            || !string.Equals(left.RegistryUrl, right.RegistryUrl, StringComparison.Ordinal)
            || left.Dependencies.Count != right.Dependencies.Count
            || left.DependencyClosure.Count != right.DependencyClosure.Count) return false;
        foreach (var pair in left.Dependencies)
            if (!right.Dependencies.TryGetValue(pair.Key, out var value)
                || !string.Equals(pair.Value, value, StringComparison.Ordinal)) return false;
        for (int i = 0; i < left.DependencyClosure.Count; i++)
            if (!string.Equals(left.DependencyClosure[i], right.DependencyClosure[i], StringComparison.Ordinal)) return false;
        return true;
    }
}

public sealed class PackageLockEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = "0.0.0";
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("sourceSpecifier")] public string? SourceSpecifier { get; set; }
    [JsonPropertyName("logicalRoot")] public string LogicalRoot { get; set; } = string.Empty;
    [JsonPropertyName("resolvedPath")] public string? ResolvedPath { get; set; }
    [JsonPropertyName("contentHash")] public string ContentHash { get; set; } = string.Empty;
    [JsonPropertyName("gitUrl")] public string? GitUrl { get; set; }
    [JsonPropertyName("gitRef")] public string? GitRef { get; set; }
    [JsonPropertyName("gitCommit")] public string? GitCommit { get; set; }
    [JsonPropertyName("registryUrl")] public string? RegistryUrl { get; set; }
    [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = new();
    [JsonPropertyName("dependencyClosure")] public List<string> DependencyClosure { get; set; } = new();

    internal void Normalize()
    {
        Source = Source.Trim();
        LogicalRoot = PackageMount.NormalizeLogicalRoot(LogicalRoot);
        var dependencyKeys = new List<string>(Dependencies.Keys);
        dependencyKeys.Sort(PackageLockFile.CompareNames);
        var sortedDependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < dependencyKeys.Count; i++)
            sortedDependencies[dependencyKeys[i]] = Dependencies[dependencyKeys[i]];
        Dependencies = sortedDependencies;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueClosure = new List<string>(DependencyClosure.Count);
        for (int i = 0; i < DependencyClosure.Count; i++)
            if (seen.Add(DependencyClosure[i])) uniqueClosure.Add(DependencyClosure[i]);
        uniqueClosure.Sort(PackageLockFile.CompareNames);
        DependencyClosure = uniqueClosure;
    }
}
