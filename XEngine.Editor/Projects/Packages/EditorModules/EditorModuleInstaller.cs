// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.PackageRegistry;

namespace XEngine.Editor.Projects.Packages.EditorModules;

/// <summary>Coarse install phases for progress reporting.</summary>
public enum EditorModulePhase
{
    Resolving,
    Downloading,
    Extracting,
    Installing,
    Done,
}

/// <summary>Progress tick surfaced to the launcher settings UI and the CLI.</summary>
public sealed record EditorModuleProgress(EditorModulePhase Phase, string Message)
{
    public long BytesReceived { get; init; }
    public long BytesTotal { get; init; }
}

/// <summary>
/// Installs, updates, and removes editor-level modules from the npm registry into
/// <see cref="EditorModuleStore"/>. This is the editor-scoped counterpart of
/// <see cref="RegistryPackageSource"/>: same npm protocol and wire-name rewriting, but no project,
/// no manifest pin, and a single version per module. Everything lands under the per-user store, so
/// a Program Files install never needs elevation and projects never see these packages unless their
/// manifest pins them (PackageManager consults the store as a built-in-like fallback).
/// </summary>
public static class EditorModuleInstaller
{
    /// <summary>
    /// Install <paramref name="moduleName"/> (engine name, e.g. <c>com.xengine.platform.android</c>)
    /// at the highest registry version satisfying <paramref name="versionRange"/> (default any).
    /// Idempotent: an already-cached satisfying version is reused without network. Throws
    /// <see cref="InvalidOperationException"/> when the module is not on the registry or no version
    /// satisfies the range, and <see cref="NpmRegistryException"/> on transport failures — a failed
    /// install never leaves staging debris or a half-written record.
    /// </summary>
    public static async Task<EditorModuleRecord> InstallAsync(
        string moduleName, string? versionRange = null, string? registryUrl = null,
        IProgress<EditorModuleProgress>? progress = null, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
            throw new ArgumentException("Module name is required.", nameof(moduleName));

        string range = string.IsNullOrWhiteSpace(versionRange) ? "*" : versionRange.Trim();
        NpmRegistryOptions options = BuildOptions(registryUrl);
        progress?.Report(new EditorModuleProgress(EditorModulePhase.Resolving,
            $"Resolving '{moduleName}' ({range})"));

        var parsedRange = VersionRange.TryParse(range) ?? VersionRange.Any;

        // Idempotent fast path: a recorded version satisfying the range (with content on disk)
        // short-circuits — reinstall after close/reopen never touches the network. Updates pass a
        // range that excludes the recorded version (">0.1.0"), so they keep going to the registry.
        if (EditorModuleStore.TryGetInstalled(moduleName, out EditorModuleRecord? recorded)
            && SemanticVersion.TryParse(recorded.Version, out SemanticVersion recordedVersion)
            && parsedRange.Satisfies(recordedVersion)
            && EditorModuleStore.TryGetModuleRoot(moduleName, out _, out _))
        {
            progress?.Report(new EditorModuleProgress(EditorModulePhase.Done,
                $"Module '{moduleName}' {recorded.Version} already present in the editor module store."));
            return recorded;
        }

        try
        {
            using var client = new NpmRegistryClient(options);
            NpmPackument? packument = await client.GetPackumentAsync(moduleName, cancellation).ConfigureAwait(false);
            if (packument == null)
                throw new InvalidOperationException(
                    $"Module '{moduleName}' was not found on registry '{options.RegistryUrl}'. "
                    + "Check the name, or set XENGINE_NPM_REGISTRY if you use a private mirror.");

            NpmVersionInfo? best = null;
            SemanticVersion? bestVersion = null;
            foreach (NpmVersionInfo candidate in packument.Versions)
            {
                if (!SemanticVersion.TryParse(candidate.Version, out SemanticVersion parsed)) continue;
                if (!parsedRange.Satisfies(parsed)) continue;
                if (bestVersion == null || parsed > bestVersion) { bestVersion = parsed; best = candidate; }
            }
            if (best == null)
                throw new InvalidOperationException(
                    $"No version of '{moduleName}' on '{options.RegistryUrl}' satisfies '{range}'.");

            string destDir = Path.Combine(EditorModuleStore.CacheRoot, $"{moduleName}@{best.Version}");
            if (Directory.Exists(destDir) && File.Exists(Path.Combine(destDir, "package.json")))
            {
                var present = new EditorModuleRecord
                {
                    Name = moduleName, Version = best.Version, InstalledAtUtc = DateTime.UtcNow,
                    Source = "npm", Registry = options.RegistryUrl,
                };
                EditorModuleStore.UpsertRecord(present);
                progress?.Report(new EditorModuleProgress(EditorModulePhase.Done,
                    $"Module '{moduleName}' {best.Version} already present in the editor module store."));
                return present;
            }

            // Extract into a staging sibling, rewrite wire names, then atomically swap in — the same
            // crash-safety contract as the project PackageCache path.
            progress?.Report(new EditorModuleProgress(EditorModulePhase.Downloading,
                $"Downloading '{moduleName}' {best.Version}"));
            string tempTarball = Path.Combine(Path.GetTempPath(),
                $"xengine-module-{moduleName}-{best.Version}-{Guid.NewGuid():N}.tgz");
            string staging = Path.Combine(EditorModuleStore.CacheRoot,
                $".staging-{moduleName}@{best.Version}-{Guid.NewGuid():N}");
            try
            {
                await client.DownloadTarballAsync(best, tempTarball, moduleName, cancellation).ConfigureAwait(false);

                progress?.Report(new EditorModuleProgress(EditorModulePhase.Extracting,
                    $"Extracting '{moduleName}' {best.Version}"));
                NpmTarball.Extract(tempTarball, staging);
                RegistryPackageSource.RewriteManifestToEngineNames(Path.Combine(staging, "package.json"));

                progress?.Report(new EditorModuleProgress(EditorModulePhase.Installing,
                    $"Installing '{moduleName}' {best.Version} into the editor module store"));
                Directory.CreateDirectory(EditorModuleStore.CacheRoot);
                if (Directory.Exists(destDir))
                    Directory.Delete(destDir, recursive: true);
                Directory.Move(staging, destDir);
            }
            finally
            {
                try { if (File.Exists(tempTarball)) File.Delete(tempTarball); } catch (IOException) { }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { }
            }

            // The store pins one version per module; older copies of this module are stale.
            EditorModuleStore.PruneOtherVersions(moduleName, best.Version);
            var installed = new EditorModuleRecord
            {
                Name = moduleName, Version = best.Version, InstalledAtUtc = DateTime.UtcNow,
                Source = "npm", Registry = options.RegistryUrl,
            };
            EditorModuleStore.UpsertRecord(installed);
            Runtime.Debug.Log(
                $"[EditorModules] Installed '{moduleName}' {best.Version} from '{options.RegistryUrl}'.");
            progress?.Report(new EditorModuleProgress(EditorModulePhase.Done,
                $"Module '{moduleName}' {best.Version} installed."));
            return installed;
        }
        catch (NpmRegistryException ex)
        {
            // Transport failure on an offline machine: a satisfying cached copy (e.g. the record was
            // wiped but the content survived) still completes the install. A 404 never lands here —
            // unknown modules must fail loudly.
            string? cached = EditorModuleStore.FindCachedRoot(moduleName, parsedRange);
            if (cached != null)
            {
                string cachedVersion = cached[(cached.LastIndexOf('@') + 1)..];
                var record = new EditorModuleRecord
                {
                    Name = moduleName, Version = cachedVersion, InstalledAtUtc = DateTime.UtcNow,
                    Source = "npm", Registry = options.RegistryUrl,
                };
                EditorModuleStore.UpsertRecord(record);
                Runtime.Debug.LogWarning(
                    $"[EditorModules] Registry unreachable for '{moduleName}', reusing cached {cachedVersion}: {ex.Message}");
                progress?.Report(new EditorModuleProgress(EditorModulePhase.Done,
                    $"Module '{moduleName}' {cachedVersion} reused from the local cache (registry unreachable)."));
                return record;
            }
            throw new InvalidOperationException(
                $"Registry '{options.RegistryUrl}' failed for editor module '{moduleName}': {ex.Message}", ex);
        }
    }

    /// <summary>Remove <paramref name="moduleName"/> from the store (cache copy + record). Returns
    /// false when it was not installed; SDKs under PlatformSDKs are untouched — they outlive module
    /// removal and are validated independently.</summary>
    public static Task<bool> UninstallAsync(string moduleName)
    {
        if (!EditorModuleStore.TryGetInstalled(moduleName, out EditorModuleRecord record))
            return Task.FromResult(false);

        string root = Path.Combine(EditorModuleStore.CacheRoot, $"{record.Name}@{record.Version}");
        if (Directory.Exists(root))
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"Cannot delete module folder '{root}' (files in use?): {ex.Message}", ex);
            }
        }
        EditorModuleStore.RemoveRecord(moduleName);
        Runtime.Debug.Log($"[EditorModules] Uninstalled '{moduleName}'.");
        return Task.FromResult(true);
    }

    /// <summary>Highest registry version of <paramref name="moduleName"/>, or null when unpublished —
    /// the update check for the settings UI. Never throws for missing packages.</summary>
    public static async Task<string?> QueryLatestVersionAsync(
        string moduleName, string? registryUrl = null, CancellationToken cancellation = default)
    {
        NpmRegistryOptions options = BuildOptions(registryUrl);
        try
        {
            using var client = new NpmRegistryClient(options);
            NpmPackument? packument = await client.GetPackumentAsync(moduleName, cancellation).ConfigureAwait(false);
            if (packument == null) return null;
            string? latest = null;
            SemanticVersion? latestVersion = null;
            foreach (NpmVersionInfo candidate in packument.Versions)
            {
                if (!SemanticVersion.TryParse(candidate.Version, out SemanticVersion parsed)) continue;
                if (latestVersion == null || parsed > latestVersion) { latestVersion = parsed; latest = candidate.Version; }
            }
            return latest;
        }
        catch (NpmRegistryException)
        {
            return null;
        }
    }

    private static NpmRegistryOptions BuildOptions(string? registryUrl)
    {
        // No project root here by design: editor-level credentials come from the env var or the
        // user-level secrets file, never a specific project's secrets.
        NpmRegistryOptions loaded = NpmRegistryOptions.Load(projectRoot: null);
        if (string.IsNullOrWhiteSpace(registryUrl)) return loaded;
        return new NpmRegistryOptions
        {
            RegistryUrl = registryUrl.Trim(),
            AuthToken = loaded.AuthToken,
            OneTimePassword = loaded.OneTimePassword,
            Timeout = loaded.Timeout,
            Disabled = loaded.Disabled,
        };
    }
}
