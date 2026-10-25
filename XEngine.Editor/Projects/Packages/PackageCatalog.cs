// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.PackageRegistry;

namespace XEngine.Editor.Projects.Packages;

public enum PackageInstallState
{
    Available,
    Installed,
    InstalledTransitively,
    BuiltIn,
    UpdateAvailable,
    Unsupported,
}

public sealed class PackageCatalogVersion
{
    public required string Version { get; init; }
    public required PackageSource Source { get; init; }
    public required string RootPath { get; init; }
    public required string SourceSpecifier { get; init; }
    public required PackageJson Metadata { get; init; }
}

public sealed class PackageCatalogEntry
{
    public required string Name { get; init; }
    public string DisplayName { get; internal set; } = "";
    public string Description { get; internal set; } = "";
    public string Category { get; internal set; } = "";
    public string PackageType { get; internal set; } = "package";
    public string? InstalledVersion { get; internal set; }
    public string? LatestVersion { get; internal set; }
    public PackageSource Source { get; internal set; }
    public PackageInstallState State { get; internal set; }
    public bool IsDirectDependency { get; internal set; }
    public bool HasSamples { get; internal set; }
    public ResolvedPackage? Resolved { get; internal set; }
    public PackageJson? Metadata { get; internal set; }
    public List<PackageCatalogVersion> Versions { get; } = new();
}

public sealed class PackageCatalogSnapshot
{
    public List<PackageCatalogEntry> Entries { get; } = new();
    public List<string> Problems { get; } = new();
    public DateTime RefreshedAtUtc { get; internal set; }
}

/// <summary>
/// Builds the Package Manager browse model. Resolution remains the source of truth for installed
/// content; the catalog adds versions discoverable from configured local registries so uninstalled
/// packages can be selected before they exist in the project cache.
/// </summary>
public static class PackageCatalog
{
    public static PackageCatalogSnapshot Build(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var snapshot = new PackageCatalogSnapshot { RefreshedAtUtc = DateTime.UtcNow };
        var manifest = ProjectPackageManifest.ReadOrDefault(Path.Combine(project.PackagesPath, "manifest.json"));
        IReadOnlyList<ResolvedPackage> resolved = PackageManager.Resolve(project);
        var entries = new Dictionary<string, PackageCatalogEntry>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < resolved.Count; i++)
            AddResolved(entries, resolved[i], manifest);

        AddRegistryPackages(entries, snapshot.Problems, manifest);
        AddOfficialOptionalPackages(entries);

        FinalizeSnapshot(snapshot, entries);
        return snapshot;
    }

    /// <summary>
    /// P3: <see cref="Build"/> plus the configured npm registries' remote catalogs — search results
    /// for discovery and per-package packuments for update detection. Runs on a caller-provided
    /// background context (all I/O is network); a registry that cannot be reached adds a problem
    /// instead of failing the snapshot, and the first hard network error stops the remote phase so an
    /// offline machine pays at most one timeout.
    /// </summary>
    public static async Task<PackageCatalogSnapshot> BuildWithRemoteAsync(
        Project project, CancellationToken cancellationToken = default)
    {
        PackageCatalogSnapshot snapshot = Build(project);

        // (registry URL, engine-name scopes for filtering, npm search text). The official registry
        // searches its npm scope ("@inxep"); custom registries search their declared scope prefix.
        var registries = new List<(string Url, IReadOnlyList<string> Scopes, string SearchText)>();
        var manifest = ProjectPackageManifest.ReadOrDefault(Path.Combine(project.PackagesPath, "manifest.json"));
        if (manifest.ScopedRegistries != null)
        {
            foreach (KeyValuePair<string, ScopedRegistry> pair in manifest.ScopedRegistries)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value.Url) && RegistryPackageSource.IsHttp(pair.Value.Url))
                    registries.Add((pair.Value.Url!, pair.Value.Scopes,
                        pair.Value.Scopes.Count > 0 ? pair.Value.Scopes[0] : "xengine"));
            }
        }
        NpmRegistryOptions options = NpmRegistryOptions.Load(project.RootPath);
        if (!options.Disabled)
            registries.Add((options.RegistryUrl,
                new[] { NpmPackageName.InternalPrefix.TrimEnd('.') },
                "@" + NpmPackageName.OfficialScope));

        if (registries.Count == 0) return snapshot;

        var entries = new Dictionary<string, PackageCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageCatalogEntry entry in snapshot.Entries)
            entries[entry.Name] = entry;

        bool networkAlive = true;
        for (int i = 0; i < registries.Count && networkAlive; i++)
        {
            string url = registries[i].Url;
            IReadOnlyList<string> scopes = registries[i].Scopes;
            string searchText = registries[i].SearchText;
            // One client per registry — the URL is per-registry; token/timeout stay global.
            using var client = new NpmRegistryClient(new NpmRegistryOptions
            {
                RegistryUrl = url,
                AuthToken = options.AuthToken,
                Timeout = options.Timeout,
                Disabled = options.Disabled,
                ProjectRoot = options.ProjectRoot,
            });
            List<string> searchNames;
            try
            {
                // Plain-scope text search: the registry's full-text match finds reverse-domain and
                // scoped names alike; results are filtered against the declared scopes below.
                IReadOnlyList<NpmSearchResult> results = await client.SearchAsync(searchText, 250, cancellationToken).ConfigureAwait(false);
                searchNames = new List<string>(results.Count);
                foreach (NpmSearchResult result in results)
                    searchNames.Add(result.EngineName);
            }
            catch (NpmRegistryException ex)
            {
                snapshot.Problems.Add($"Registry '{url}' search failed: {ex.Message}");
                networkAlive = false;
                break;
            }

            // Packuments for search hits (discovery) and for already-installed/registry packages
            // (update detection), merged version by version.
            var fetchNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in searchNames)
                if (MatchesAnyScope(name, scopes)) fetchNames.Add(name);
            foreach (PackageCatalogEntry entry in snapshot.Entries)
                if (entry.InstalledVersion != null && MatchesAnyScope(entry.Name, scopes))
                    fetchNames.Add(entry.Name);

            foreach (string name in fetchNames)
            {
                if (!networkAlive) break;
                NpmPackument? packument;
                try
                {
                    packument = await client.GetPackumentAsync(name, cancellationToken).ConfigureAwait(false);
                }
                catch (NpmRegistryException ex)
                {
                    snapshot.Problems.Add($"Registry '{url}' failed for '{name}': {ex.Message}");
                    networkAlive = false;
                    break;
                }
                if (packument != null)
                    MergeRemote(entries, packument);
            }
        }

        FinalizeSnapshot(snapshot, entries);
        return snapshot;
    }

    /// <summary>Merge one remote packument's versions into the snapshot entries.</summary>
    internal static void MergeRemote(Dictionary<string, PackageCatalogEntry> entries, NpmPackument packument)
    {
        string engineName = packument.EngineName;
        PackageCatalogEntry entry = GetOrCreate(entries, engineName);
        foreach (NpmVersionInfo remote in packument.Versions)
        {
            if (!SemanticVersion.TryParse(remote.Version, out _)) continue;
            var metadata = new PackageJson
            {
                Name = engineName,
                Version = remote.Version,
                Description = remote.Description ?? "",
            };
            foreach (KeyValuePair<string, string> dependency in remote.EngineDependencies())
                metadata.Dependencies[dependency.Key] = dependency.Value;
            AddVersion(entry, new PackageCatalogVersion
            {
                Version = remote.Version,
                Source = PackageSource.Registry,
                RootPath = "",
                SourceSpecifier = remote.Version,
                Metadata = metadata,
            });
        }
        if (entry.Metadata == null && packument.LatestVersion is { Length: > 0 } latest)
        {
            var metadata = new PackageJson { Name = engineName, Version = latest };
            NpmVersionInfo? latestInfo = null;
            foreach (NpmVersionInfo remote in packument.Versions)
                if (string.Equals(remote.Version, latest, StringComparison.OrdinalIgnoreCase)) latestInfo = remote;
            if (latestInfo?.Description is { Length: > 0 } description) metadata.Description = description;
            ApplyMetadata(entry, metadata);
        }
        if (entry.InstalledVersion == null)
        {
            entry.Source = PackageSource.Registry;
            entry.State = PackageInstallState.Available;
        }
    }

    /// <summary>Sort versions, derive latest/update states, and sort entries. Idempotent — remote
    /// merges re-run this over the same entries.</summary>
    private static void FinalizeSnapshot(PackageCatalogSnapshot snapshot,
        Dictionary<string, PackageCatalogEntry> entries)
    {
        snapshot.Entries.Clear();
        foreach (PackageCatalogEntry entry in entries.Values)
        {
            entry.Versions.Sort(CompareVersionsDescending);
            if (entry.Versions.Count > 0)
            {
                PackageCatalogVersion latest = entry.Versions[0];
                entry.LatestVersion = latest.Version;
                if (entry.Metadata == null)
                    ApplyMetadata(entry, latest.Metadata);
            }

            bool updateAvailable = entry.InstalledVersion != null && entry.LatestVersion != null
                && SemanticVersion.TryParse(entry.InstalledVersion, out SemanticVersion installed)
                && SemanticVersion.TryParse(entry.LatestVersion, out SemanticVersion latestVersion)
                && latestVersion > installed;
            if (updateAvailable)
                entry.State = PackageInstallState.UpdateAvailable;
            else if (entry.State == PackageInstallState.UpdateAvailable)
            {
                // A re-finalize after remote merge may have withdrawn the update; restore the
                // underlying install state instead of leaving a stale UpdateAvailable.
                entry.State = entry.IsDirectDependency || entry.Source == PackageSource.Embed
                    ? PackageInstallState.Installed
                    : entry.Source == PackageSource.BuiltIn
                        ? PackageInstallState.BuiltIn
                        : PackageInstallState.InstalledTransitively;
            }

            snapshot.Entries.Add(entry);
        }

        snapshot.Entries.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(
            string.IsNullOrWhiteSpace(left.DisplayName) ? left.Name : left.DisplayName,
            string.IsNullOrWhiteSpace(right.DisplayName) ? right.Name : right.DisplayName));
    }

    private static void AddResolved(Dictionary<string, PackageCatalogEntry> entries, ResolvedPackage package,
        ProjectPackageManifest manifest)
    {
        PackageCatalogEntry entry = GetOrCreate(entries, package.Name);
        PackageJson metadata = PackageJson.Read(Path.Combine(package.ResolvedRoot, "package.json"))
            ?? new PackageJson { Name = package.Name, Version = package.Version, DisplayName = package.DisplayName };

        entry.Resolved = package;
        entry.InstalledVersion = package.Version;
        entry.Source = package.Source;
        entry.IsDirectDependency = manifest.Dependencies.ContainsKey(package.Name);
        if (package.Source == PackageSource.BuiltIn && !entry.IsDirectDependency)
            entry.State = PackageInstallState.BuiltIn;
        else if (entry.IsDirectDependency || package.Source == PackageSource.Embed)
            entry.State = PackageInstallState.Installed;
        else
            entry.State = PackageInstallState.InstalledTransitively;
        ApplyMetadata(entry, metadata);
        AddVersion(entry, new PackageCatalogVersion
        {
            Version = package.Version,
            Source = package.Source,
            RootPath = package.ResolvedRoot,
            SourceSpecifier = package.SourceSpecifier ?? package.Version,
            Metadata = metadata,
        });
    }

    private static void AddRegistryPackages(Dictionary<string, PackageCatalogEntry> entries,
        List<string> problems, ProjectPackageManifest manifest)
    {
        if (manifest.ScopedRegistries == null)
            return;

        foreach (KeyValuePair<string, ScopedRegistry> pair in manifest.ScopedRegistries)
        {
            ScopedRegistry registry = pair.Value;
            if (string.IsNullOrWhiteSpace(registry.Url))
                continue;

            string? registryRoot = RegistryPackageSource.ToLocalDirectory(registry.Url);
            if (registryRoot == null)
            {
                problems.Add($"Registry '{pair.Key}' is remote and its catalog is unavailable offline.");
                continue;
            }
            if (!Directory.Exists(registryRoot))
            {
                problems.Add($"Registry '{pair.Key}' was not found at '{registryRoot}'.");
                continue;
            }

            string[] packageDirectories = Directory.GetDirectories(registryRoot);
            for (int packageIndex = 0; packageIndex < packageDirectories.Length; packageIndex++)
            {
                string packageName = Path.GetFileName(packageDirectories[packageIndex]);
                if (!MatchesAnyScope(packageName, registry.Scopes))
                    continue;

                string[] versionDirectories = Directory.GetDirectories(packageDirectories[packageIndex]);
                for (int versionIndex = 0; versionIndex < versionDirectories.Length; versionIndex++)
                {
                    string version = Path.GetFileName(versionDirectories[versionIndex]);
                    if (!SemanticVersion.TryParse(version, out _))
                        continue;
                    string packageJsonPath = Path.Combine(versionDirectories[versionIndex], "package.json");
                    PackageJson? metadata = PackageJson.Read(packageJsonPath);
                    if (metadata == null || !string.Equals(metadata.Name, packageName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    PackageCatalogEntry entry = GetOrCreate(entries, packageName);
                    AddVersion(entry, new PackageCatalogVersion
                    {
                        Version = version,
                        Source = PackageSource.Registry,
                        RootPath = versionDirectories[versionIndex],
                        SourceSpecifier = version,
                        Metadata = metadata,
                    });
                    if (entry.Metadata == null)
                        ApplyMetadata(entry, metadata);
                    if (entry.InstalledVersion == null)
                    {
                        entry.Source = PackageSource.Registry;
                        entry.State = PackageInstallState.Available;
                    }
                }
            }
        }
    }

    private static void AddOfficialOptionalPackages(Dictionary<string, PackageCatalogEntry> entries)
    {
        string builtInPath = PackageManager.BuiltInPackagesPath;
        if (!Directory.Exists(builtInPath))
            return;

        string[] packageDirectories;
        try { packageDirectories = Directory.GetDirectories(builtInPath); }
        catch { return; }

        for (int i = 0; i < packageDirectories.Length; i++)
        {
            string dir = packageDirectories[i];
            string dirName = Path.GetFileName(dir);
            if (dirName.StartsWith('.') || dirName.EndsWith("~", StringComparison.OrdinalIgnoreCase))
                continue;

            PackageJson? metadata = PackageJson.Read(Path.Combine(dir, "package.json"));
            if (metadata == null || string.IsNullOrWhiteSpace(metadata.Name) || metadata.IsInstalledByDefault)
                continue;

            PackageCatalogEntry entry = GetOrCreate(entries, metadata.Name);
            AddVersion(entry, new PackageCatalogVersion
            {
                Version = metadata.Version,
                Source = PackageSource.BuiltIn,
                RootPath = dir,
                SourceSpecifier = metadata.Version,
                Metadata = metadata,
            });
            if (entry.Metadata == null)
                ApplyMetadata(entry, metadata);
            if (string.IsNullOrWhiteSpace(entry.Category))
                entry.Category = "Official";
            if (entry.InstalledVersion == null)
            {
                entry.Source = PackageSource.BuiltIn;
                entry.State = PackageInstallState.Available;
            }
        }
    }

    private static PackageCatalogEntry GetOrCreate(Dictionary<string, PackageCatalogEntry> entries, string name)
    {
        if (entries.TryGetValue(name, out PackageCatalogEntry? entry))
            return entry;
        entry = new PackageCatalogEntry { Name = name, DisplayName = name };
        entries.Add(name, entry);
        return entry;
    }

    private static void ApplyMetadata(PackageCatalogEntry entry, PackageJson metadata)
    {
        entry.Metadata = metadata;
        entry.DisplayName = string.IsNullOrWhiteSpace(metadata.DisplayName) ? entry.Name : metadata.DisplayName!;
        entry.Description = metadata.Description ?? "";
        entry.Category = metadata.Category ?? "";
        entry.PackageType = metadata.PackageType ?? "package";
        entry.HasSamples = metadata.Samples.Count > 0;
    }

    private static void AddVersion(PackageCatalogEntry entry, PackageCatalogVersion candidate)
    {
        for (int i = 0; i < entry.Versions.Count; i++)
        {
            PackageCatalogVersion existing = entry.Versions[i];
            if (string.Equals(existing.Version, candidate.Version, StringComparison.OrdinalIgnoreCase)
                && existing.Source == candidate.Source)
                return;
        }
        entry.Versions.Add(candidate);
    }

    private static bool MatchesAnyScope(string packageName, IReadOnlyList<string> scopes)
    {
        if (scopes.Count == 0)
            return true;
        for (int i = 0; i < scopes.Count; i++)
        {
            if (packageName.StartsWith(scopes[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static int CompareVersionsDescending(PackageCatalogVersion left, PackageCatalogVersion right)
    {
        if (SemanticVersion.TryParse(left.Version, out SemanticVersion leftVersion)
            && SemanticVersion.TryParse(right.Version, out SemanticVersion rightVersion))
            return rightVersion.CompareTo(leftVersion);
        return StringComparer.OrdinalIgnoreCase.Compare(right.Version, left.Version);
    }
}
