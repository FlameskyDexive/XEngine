// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;
using XEngine.PackageRegistry;

namespace XEngine.Ai.Host;

/// <summary>
/// Offline (no engine running) MCP tools for the XEngine package manager. These read and write the
/// project's <c>Packages/manifest.json</c> directly as JSON — the editor picks up the change via its
/// manifest watcher and triggers a recompile, so an AI can install/remove packages without launching
/// the editor. Mirrors the PackageManager P1 design (docs/superpowers/specs/2026-08-03-...md).
/// </summary>
public static class PackageManifestDto
{
    public sealed class Manifest
    {
        [JsonPropertyName("dependencies")] public Dictionary<string, string> Dependencies { get; set; } = new();
        [JsonPropertyName("testables")] public List<string> Testables { get; set; } = new();
    }
}

[McpServerToolType]
public static class PackageTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string ManifestPath(IServiceProvider services)
        => Path.Combine(services.GetRequiredService<ProjectDescriptor>().ProjectRoot, "Packages", "manifest.json");

    internal static PackageManifestDto.Manifest ReadManifest(string path)
    {
        if (!File.Exists(path)) return new PackageManifestDto.Manifest();
        var json = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(json)
            ? new PackageManifestDto.Manifest()
            : JsonSerializer.Deserialize<PackageManifestDto.Manifest>(json, JsonOptions) ?? new();
    }

    internal static void WriteManifest(string path, PackageManifestDto.Manifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions) + "\n");
    }

    // ------------------------------------------------------------------
    //  Tools
    // ------------------------------------------------------------------

    [McpServerTool(Name = "package_list", UseStructuredContent = true)]
    [Description("List every package declared in the project's Packages/manifest.json, with its source/version specifier. Pure read; no engine required.")]
    public static PackageListResult ListPackages(IServiceProvider services)
    {
        var manifest = ReadManifest(ManifestPath(services));
        var entries = manifest.Dependencies
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new PackageListEntry(kv.Key, kv.Value, Classify(kv.Value)))
            .ToList();
        return new PackageListResult(entries);
    }

    [McpServerTool(Name = "package_add", UseStructuredContent = true)]
    [Description("Add or update a package dependency in Packages/manifest.json. specifier examples: '1.2.3' (embed pin), 'file:../path' (local), 'git@url#ref' (git), 'embed:name'. Returns the resulting manifest entry. The editor's manifest watcher picks up the change and triggers a recompile.")]
    public static PackageAddResult AddPackage(
        IServiceProvider services,
        [Description("Reverse-domain package name (e.g. com.xengine.cinemachine).")] string name,
        [Description("Source/version specifier (e.g. '1.2.3', 'file:../pkg', 'git@https://...#v1').")] string specifier,
        [Description("Dry-run: validate and return what would change without writing. Default false.")] bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required.");
        if (string.IsNullOrWhiteSpace(specifier)) throw new ArgumentException("specifier is required.");

        var path = ManifestPath(services);
        var manifest = ReadManifest(path);
        bool existed = manifest.Dependencies.TryGetValue(name, out var prev);
        if (!dryRun)
        {
            manifest.Dependencies[name] = specifier;
            WriteManifest(path, manifest);
        }
        return new PackageAddResult(name, specifier, Classify(specifier), existed ? prev : null, dryRun);
    }

    [McpServerTool(Name = "package_remove", UseStructuredContent = true)]
    [Description("Remove a package dependency from Packages/manifest.json. Returns whether the package was present. The editor's manifest watcher picks up the change and triggers a recompile.")]
    public static PackageRemoveResult RemovePackage(
        IServiceProvider services,
        [Description("Reverse-domain package name to remove.")] string name,
        [Description("Dry-run: validate and return what would change without writing. Default false.")] bool dryRun = false)
    {
        var path = ManifestPath(services);
        var manifest = ReadManifest(path);
        bool existed = manifest.Dependencies.TryGetValue(name, out var prev);
        if (existed && !dryRun)
        {
            manifest.Dependencies.Remove(name);
            WriteManifest(path, manifest);
        }
        return new PackageRemoveResult(name, existed, existed ? prev : null, dryRun);
    }

    private static string Classify(string specifier)
    {
        var s = specifier.Trim();
        if (s.StartsWith("embed:", StringComparison.OrdinalIgnoreCase)) return "embed";
        if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return "local";
        if (s.StartsWith("git@", StringComparison.OrdinalIgnoreCase) || s.StartsWith("git://", StringComparison.OrdinalIgnoreCase)) return "git";
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "registry";
        return "version";
    }

    // ------------------------------------------------------------------
    //  Registry tools (npm protocol)
    // ------------------------------------------------------------------

    [McpServerTool(Name = "package_search", UseStructuredContent = true)]
    [Description("Search an npm-protocol registry for packages. Returns engine names (com.xengine.*) mapped from their registry scope (@inxep/*), latest versions, and descriptions. Registry defaults to the official npm registry; override per project with the scopedRegistries entry in Packages/manifest.json or the XENGINE_NPM_REGISTRY environment variable.")]
    public static async Task<PackageSearchMcpResult> SearchRegistry(
        IServiceProvider services,
        [Description("Search text; npm qualifiers like 'scope:inxep' or 'keywords:spine' are passed through.")] string text,
        [Description("Optional registry base URL; defaults to the configured official registry.")] string? registry = null,
        [Description("Maximum results (default 50).")] int limit = 50)
    {
        string projectRoot = services.GetRequiredService<ProjectDescriptor>().ProjectRoot;
        NpmRegistryOptions loaded = NpmRegistryOptions.Load(projectRoot);
        using var client = new NpmRegistryClient(Restrict(loaded, registry));
        IReadOnlyList<NpmSearchResult> results = await client.SearchAsync(text, limit).ConfigureAwait(false);
        return new PackageSearchMcpResult(results.Count,
            results.Select(r => new PackageSearchMcpEntry(r.EngineName, r.Name, r.Version, r.Description)).ToArray());
    }

    [McpServerTool(Name = "package_versions", UseStructuredContent = true)]
    [Description("List every published version of a package on an npm-protocol registry, with the latest dist-tag and per-version dependencies (engine names). Use before package_add to pick an installable version; offline installs fall back to the lock file / Library/PackageCache.")]
    public static async Task<PackageVersionsMcpResult> ListVersions(
        IServiceProvider services,
        [Description("Engine package name (com.xengine.spine) or registry name (@inxep/spine).")] string name,
        [Description("Optional registry base URL; defaults to the configured official registry.")] string? registry = null)
    {
        string projectRoot = services.GetRequiredService<ProjectDescriptor>().ProjectRoot;
        NpmRegistryOptions loaded = NpmRegistryOptions.Load(projectRoot);
        using var client = new NpmRegistryClient(Restrict(loaded, registry));
        NpmPackument? packument = await client.GetPackumentAsync(name).ConfigureAwait(false);
        if (packument == null)
            throw new FileNotFoundException($"Package '{name}' was not found on the registry.");
        return new PackageVersionsMcpResult(
            packument.EngineName, packument.Name, packument.LatestVersion,
            packument.Versions.Select(v => new PackageVersionMcpEntry(
                v.Version, v.Description,
                v.EngineDependencies().Select(d => $"{d.Key} {d.Value}").ToArray())).ToArray());
    }

    private static NpmRegistryOptions Restrict(NpmRegistryOptions loaded, string? registryOverride)
    {
        if (string.IsNullOrWhiteSpace(registryOverride)) return loaded;
        return new NpmRegistryOptions
        {
            RegistryUrl = registryOverride.Trim(),
            AuthToken = loaded.AuthToken,
            Timeout = loaded.Timeout,
            Disabled = loaded.Disabled,
            ProjectRoot = loaded.ProjectRoot,
        };
    }
}

public sealed record PackageSearchMcpResult(int Total, IReadOnlyList<PackageSearchMcpEntry> Results);
public sealed record PackageSearchMcpEntry(string Name, string RegistryName, string Version, string? Description);
public sealed record PackageVersionsMcpResult(string Name, string RegistryName, string? LatestVersion,
    IReadOnlyList<PackageVersionMcpEntry> Versions);
public sealed record PackageVersionMcpEntry(string Version, string? Description, IReadOnlyList<string> Dependencies);

public sealed record PackageListResult(IReadOnlyList<PackageListEntry> Packages);
public sealed record PackageListEntry(string Name, string Specifier, string Source);
public sealed record PackageAddResult(string Name, string Specifier, string Source, string? PreviousSpecifier, bool DryRun);
public sealed record PackageRemoveResult(string Name, bool WasPresent, string? PreviousSpecifier, bool DryRun);
