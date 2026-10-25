// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using XEngine.PackageRegistry;

namespace XEngine.Editor.Projects.Packages;

/// <summary>
/// Resolves version-pinned registry sources. Local <c>file://</c> registries keep the P2 layout
/// (flat <c>index.json</c> + per-package version directories); <c>http(s)://</c> locations speak the
/// npm protocol through <see cref="NpmRegistryClient"/> (P3): packument fetch, SemVer range
/// selection, SRI-verified tarball download, and extraction into
/// <c>Library/PackageCache/&lt;name&gt;@&lt;ver&gt;/</c>.
///
/// <para>Local registry layout (a directory):
/// <code>
/// my-registry/
///   index.json                 # { "packages": { "com.x.foo": { "1.2.3": "com.x.foo/1.2.3", ... } } }
///   com.x.foo/
///     1.2.3/                   # a full package root (package.json + Runtime/ + ...)
///     1.2.4/
/// </code>
/// Both paths copy/extract the selected version into <c>Library/PackageCache/&lt;name&gt;@&lt;ver&gt;/</c>
/// (so the rest of the pipeline treats it identically to a git clone) and return that cache path.</para>
/// </summary>
public static class RegistryPackageSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Parse a registry specifier of the form <c>registry:&lt;url-or-path&gt;#&lt;version&gt;</c> or a
    /// bare registry entry already resolved against a scoped registry. Returns null if not a registry
    /// source.
    /// </summary>
    public static (string registryLocation, string version)? Parse(string specifier)
    {
        var s = (specifier ?? "").Trim();
        if (s.StartsWith("registry:", StringComparison.OrdinalIgnoreCase))
            s = s["registry:".Length..].Trim();
        else if (!s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                 && !s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;
        // For file:// the version is the part after '#', defaulting to the manifest's pin (handled
        // by caller for bare version specifiers; here we only handle explicit registry: prefixes).
        string location = s, version = "";
        int hash = s.IndexOf('#');
        if (hash >= 0)
        {
            version = s[(hash + 1)..];
            location = s[..hash];
        }
        if (string.IsNullOrWhiteSpace(location)) return null;
        return (location.Trim(), version.Trim());
    }

    /// <summary>
    /// Resolve a version from a registry into <c>Library/PackageCache/&lt;name&gt;@&lt;ver&gt;/</c> and return
    /// that cache path. <paramref name="registryLocation"/> is either a local directory (file:// or
    /// bare path, P2) or an npm-protocol registry URL (http/https, P3). The selected version is
    /// copied/extracted into the cache so the rest of the pipeline sees a stable read-only root.
    /// Returns null on failure.
    /// </summary>
    public static string? Resolve(Project project, string packageName, string versionRangeSpec,
        string registryLocation, IEnumerable<string>? candidateVersions = null)
    {
        if (IsHttp(registryLocation))
            return ResolveNpm(project, packageName, versionRangeSpec, registryLocation);

        string registryDir = ToLocalDirectory(registryLocation);
        if (registryDir == null)
        {
            Runtime.Debug.LogError($"[Packages] Unsupported registry location '{registryLocation}'.");
            return null;
        }
        if (!Directory.Exists(registryDir))
        {
            Runtime.Debug.LogError($"[Packages] Registry directory not found: '{registryDir}'.");
            return null;
        }

        // Read the index (optional but recommended). Without it, fall back to scanning
        // <registryDir>/<packageName>/<version>/ directories.
        var available = candidateVersions ?? EnumerateVersions(registryDir, packageName).ToList();
        var selected = SelectVersion(versionRangeSpec, available);
        if (selected == null)
        {
            Runtime.Debug.LogError(
                $"[Packages] No version of '{packageName}' in registry '{registryDir}' satisfies '{versionRangeSpec}'. Available: {(available.Any() ? string.Join(", ", available) : "(none)")}");
            return null;
        }

        string srcDir = Path.Combine(registryDir, packageName, selected);
        if (!Directory.Exists(srcDir) || !File.Exists(Path.Combine(srcDir, "package.json")))
        {
            Runtime.Debug.LogError($"[Packages] Registry entry '{packageName}@{selected}' not found at '{srcDir}'.");
            return null;
        }

        // Copy into the package cache (read-only, identical treatment to a git clone).
        var cacheRoot = Path.Combine(project.LibraryPath, "PackageCache");
        Directory.CreateDirectory(cacheRoot);
        string destDir = Path.Combine(cacheRoot, $"{packageName}@{selected}");
        if (!Directory.Exists(destDir))
        {
            CopyDirectory(srcDir, destDir);
        }
        return destDir;
    }

    /// <summary>True when the location is an http(s) npm-protocol registry URL.</summary>
    internal static bool IsHttp(string location)
        => location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// P3: resolve from an npm-protocol registry. Fast path: a previously downloaded
    /// <c>PackageCache/&lt;name&gt;@&lt;ver&gt;</c> satisfying the range short-circuits all network use, so a
    /// cached project reopens offline. On network failure returns null — the caller's lock-file
    /// recovery path then remounts the previous cache selection if one exists.
    /// </summary>
    private static string? ResolveNpm(Project project, string packageName, string versionRangeSpec, string registryLocation)
    {
        string? cached = FindCachedSatisfying(project, packageName, versionRangeSpec);
        if (cached != null) return cached;

        // Registry URL is explicit here; token/timeout/offline switch still honor env + secrets.
        NpmRegistryOptions loaded = NpmRegistryOptions.Load(project.RootPath);
        var options = new NpmRegistryOptions
        {
            RegistryUrl = registryLocation,
            AuthToken = loaded.AuthToken,
            Timeout = loaded.Timeout,
            Disabled = loaded.Disabled,
            ProjectRoot = loaded.ProjectRoot,
        };

        try
        {
            using var client = new NpmRegistryClient(options);
            NpmPackument? packument = client.GetPackumentAsync(packageName).GetAwaiter().GetResult();
            if (packument == null)
            {
                Runtime.Debug.LogError(
                    $"[Packages] '{packageName}' was not found on registry '{registryLocation}'.");
                return null;
            }

            var range = VersionRange.TryParse(versionRangeSpec) ?? VersionRange.Any;
            NpmVersionInfo? best = null;
            SemanticVersion? bestVersion = null;
            foreach (NpmVersionInfo candidate in packument.Versions)
            {
                if (!SemanticVersion.TryParse(candidate.Version, out SemanticVersion parsed)) continue;
                if (!range.Satisfies(parsed)) continue;
                if (bestVersion == null || parsed > bestVersion) { bestVersion = parsed; best = candidate; }
            }
            if (best == null)
            {
                Runtime.Debug.LogError(
                    $"[Packages] No version of '{packageName}' on '{registryLocation}' satisfies '{versionRangeSpec}'. Available: {string.Join(", ", packument.Versions.Select(static v => v.Version))}");
                return null;
            }

            string cacheRoot = Path.Combine(project.LibraryPath, "PackageCache");
            string destDir = Path.Combine(cacheRoot, $"{packageName}@{best.Version}");
            if (Directory.Exists(destDir)) return destDir;

            // Extract into a staging sibling, rewrite wire names, then atomically swap in.
            string tempTarball = Path.Combine(Path.GetTempPath(),
                $"xengine-npm-{packageName}-{best.Version}-{Guid.NewGuid():N}.tgz");
            string staging = Path.Combine(cacheRoot,
                $".staging-{packageName}@{best.Version}-{Guid.NewGuid():N}");
            try
            {
                client.DownloadTarballAsync(best, tempTarball, packageName).GetAwaiter().GetResult();
                NpmTarball.Extract(tempTarball, staging);
                RewriteManifestToEngineNames(Path.Combine(staging, "package.json"));
                Directory.CreateDirectory(cacheRoot);
                Directory.Move(staging, destDir);
                Runtime.Debug.Log(
                    $"[Packages] Installed '{packageName}@{best.Version}' from registry '{registryLocation}' into PackageCache.");
                return destDir;
            }
            finally
            {
                try { if (File.Exists(tempTarball)) File.Delete(tempTarball); } catch (IOException) { }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { }
            }
        }
        catch (NpmRegistryException ex)
        {
            Runtime.Debug.LogError($"[Packages] Registry '{registryLocation}' failed for '{packageName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Highest already-downloaded <c>PackageCache/&lt;name&gt;@&lt;ver&gt;</c> satisfying the range, or null.</summary>
    private static string? FindCachedSatisfying(Project project, string packageName, string versionRangeSpec)
    {
        var cacheRoot = Path.Combine(project.LibraryPath, "PackageCache");
        if (!Directory.Exists(cacheRoot)) return null;
        var range = VersionRange.TryParse(versionRangeSpec) ?? VersionRange.Any;
        string? best = null;
        SemanticVersion? bestVersion = null;
        foreach (string dir in Directory.EnumerateDirectories(cacheRoot, packageName + "@*", SearchOption.TopDirectoryOnly))
        {
            if (!File.Exists(Path.Combine(dir, "package.json"))) continue;
            string version = dir[(dir.LastIndexOf('@') + 1)..];
            if (!SemanticVersion.TryParse(version, out SemanticVersion parsed)) continue;
            if (!range.Satisfies(parsed)) continue;
            if (bestVersion == null || parsed > bestVersion) { bestVersion = parsed; best = dir; }
        }
        return best;
    }

    /// <summary>
    /// The npm tarball's package.json carries wire names (<c>@inxep/spine</c>); the engine's
    /// canonical form stays <c>com.xengine.spine</c> everywhere (mounts, dependencies, catalog), so
    /// flip the name and dependency keys back after extraction. This makes a registry-installed
    /// package byte-equivalent to a built-in materialization for every downstream consumer.
    /// Shared with <see cref="EditorModules.EditorModuleInstaller"/>, which applies the same rule to
    /// editor-level module installs.
    /// </summary>
    internal static void RewriteManifestToEngineNames(string manifestPath)
    {
        JsonObject? manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
        if (manifest == null) return;

        if (manifest["name"]?.GetValue<string>() is { Length: > 0 } name)
            manifest["name"] = NpmPackageName.FromNpmName(name);
        if (manifest["dependencies"] is JsonObject dependencies)
        {
            var rewritten = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> dependency in dependencies)
                rewritten[NpmPackageName.FromNpmName(dependency.Key)] = dependency.Value?.DeepClone();
            manifest["dependencies"] = rewritten;
        }
        File.WriteAllText(manifestPath, manifest.ToJsonString(JsonOptions) + "\n");
    }

    /// <summary>Convert a registry location to a local directory path, or null if it's not file://-backed.</summary>
    internal static string? ToLocalDirectory(string location)
    {
        if (location.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var p = location["file://".Length..];
            // file:///abs/path -> /abs/path on Unix, file:///C:/x -> C:/x on Windows
            if (p.StartsWith('/')) p = p[1..];
            return Path.GetFullPath(p);
        }
        // Bare directory path (relative to current dir or absolute) is treated as a local registry too.
        if (!location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(location);
        }
        return null;
    }

    /// <summary>Pick the highest version from <paramref name="available"/> satisfying the range spec.</summary>
    private static string? SelectVersion(string rangeSpec, IEnumerable<string> available)
    {
        var range = VersionRange.TryParse(rangeSpec) ?? VersionRange.Any;
        string? best = null;
        var bestVer = (SemanticVersion?)null;
        foreach (var v in available)
        {
            if (!SemanticVersion.TryParse(v, out var parsed)) continue;
            if (!range.Satisfies(parsed)) continue;
            if (bestVer == null || parsed > bestVer) { bestVer = parsed; best = v; }
        }
        return best;
    }

    internal static IEnumerable<string> EnumerateVersions(string registryDir, string packageName)
    {
        var pkgDir = Path.Combine(registryDir, packageName);
        if (!Directory.Exists(pkgDir)) return Enumerable.Empty<string>();
        var result = new List<string>();
        foreach (var d in Directory.EnumerateDirectories(pkgDir))
        {
            var name = Path.GetFileName(d);
            if (SemanticVersion.TryParse(name, out _) && File.Exists(Path.Combine(d, "package.json")))
                result.Add(name);
        }
        return result;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
