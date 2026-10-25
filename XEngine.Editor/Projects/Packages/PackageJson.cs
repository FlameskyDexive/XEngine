// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XEngine.Editor.Projects.Packages;

/// <summary>
/// The per-package self-description file <c>package.json</c> that lives at the root of every package
/// (embed, local, git, or registry). Holds the package identity, version, optional dependency
/// constraints, platform support, and human-readable metadata. Pure JSON (not Echo) to match the
/// npm/UPM convention.
/// </summary>
public sealed class PackageJson
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reverse-domain package id, e.g. <c>com.xengine.cinemachine</c>.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>SemVer version string, e.g. <c>1.2.3</c>.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>Minimum XEngine version required by this package.</summary>
    [JsonPropertyName("xengine")] public string? XEngine { get; set; }

    /// <summary>Legacy compatibility field accepted from early UPM-shaped manifests.</summary>
    [JsonPropertyName("unity")] public string? LegacyUnity { get; set; }

    [JsonIgnore] public string? MinimumEngineVersion => XEngine ?? LegacyUnity;

    /// <summary>
    /// When <c>false</c>, an Editor-shipped package is listed in Package Manager but is not mounted
    /// until the project installs it (copied into <c>Library/PackageCache</c>). Missing / <c>null</c>
    /// means installed by default, matching every existing first-party package.
    /// </summary>
    [JsonPropertyName("installByDefault")] public bool? InstallByDefault { get; set; }

    [JsonIgnore] public bool IsInstalledByDefault => InstallByDefault != false;

    /// <summary>Package role, for example <c>package</c> or <c>feature</c>.</summary>
    [JsonPropertyName("type")] public string? PackageType { get; set; }

    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("documentationUrl")] public string? DocumentationUrl { get; set; }
    [JsonPropertyName("changelogUrl")] public string? ChangelogUrl { get; set; }
    [JsonPropertyName("licensesUrl")] public string? LicensesUrl { get; set; }

    [JsonPropertyName("samples")] public List<PackageSample> Samples { get; set; } = new();

    /// <summary>Optional lower-bound dependency constraints, e.g. <c>{ "com.xengine.input": "1.2.0" }</c>.
    /// P0 records these but does not run a full closure solve — every referenced embed package must
    /// be present in the manifest or under Packages/ directly. Full SemVer solving lands in P1.</summary>
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; set; } = new();

    /// <summary>
    /// Package names that cannot be mounted in the same project as this package. Checked
    /// bidirectionally at install and resolve time (either side declaring the other is enough).
    /// </summary>
    [JsonPropertyName("conflicts")]
    public List<string> Conflicts { get; set; } = new();

    /// <summary>Restrict the package to a subset of <see cref="Scripting.BuildPlatforms"/>. Empty/missing
    /// means "all platforms". Names are matched case-insensitively against <see cref="Scripting.BuildPlatforms.All"/>.</summary>
    [JsonPropertyName("supportedPlatforms")]
    public List<string> SupportedPlatforms { get; set; } = new();

    [JsonPropertyName("keywords")] public List<string>? Keywords { get; set; }
    [JsonPropertyName("license")] public string? License { get; set; }

    [JsonPropertyName("author")] public PackageAuthor? Author { get; set; }
    [JsonPropertyName("repository")] public PackageRepository? Repository { get; set; }

    /// <summary>Read a <c>package.json</c> from disk. Returns null when missing or unparseable
    /// (the caller decides whether that is fatal for that source).</summary>
    public static PackageJson? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<PackageJson>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            Runtime.Debug.LogError($"[Packages] Failed to parse package.json '{path}': {ex.Message}.");
            return null;
        }
        catch (IOException ex)
        {
            Runtime.Debug.LogError($"[Packages] Could not read package.json '{path}': {ex.Message}.");
            return null;
        }
    }
}

public sealed class PackageAuthor
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class PackageRepository
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class PackageSample
{
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}
