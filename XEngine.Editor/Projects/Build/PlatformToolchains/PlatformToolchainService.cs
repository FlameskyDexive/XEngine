// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Projects.Packages;
using XEngine.Runtime.AssetBundles;

namespace XEngine.Editor.Projects.Build.PlatformToolchains;

public enum PlatformToolchainComponentStatus
{
    /// <summary>State fingerprint matched and the sentinel exists — nothing was downloaded.</summary>
    Cached,
    Installed,
    /// <summary>Handled by a platform-specific channel (sdkmanager orchestration lands with the
    /// Android node); reported so callers can present an accurate plan.</summary>
    PendingChannel,
    Failed,
}

public sealed class PlatformToolchainComponentResult
{
    public required string Id { get; init; }
    public required PlatformToolchainComponentStatus Status { get; init; }
    public required string Root { get; init; }
    public string? Sha256 { get; init; }
    public string? SourceUrl { get; init; }
    public string? Error { get; init; }
}

public sealed class PlatformToolchainInstallResult
{
    public required string Platform { get; init; }
    public required string InstallRoot { get; init; }
    public required List<PlatformToolchainComponentResult> Components { get; init; }
    public bool Success => Components.All(c => c.Status != PlatformToolchainComponentStatus.Failed);
}

public sealed record PlatformToolchainProgress(
    string Stage, string? ComponentId, long Current, long Total, string? Detail = null);

/// <summary>
/// Drives one-click toolchain acquisition for an installed platform module
/// (<c>com.xengine.platform.&lt;platform&gt;</c>): resolve the module's <c>toolchain.json</c>,
/// validate it, then per component — reuse a fingerprint-matched install, or download (multi-source
/// failover, resumable) + verify + extract into staging + publish atomically into the managed SDK
/// root. Downloads never touch the Editor bundle; everything lands under
/// <c>%LOCALAPPDATA%\XEngine\PlatformSDKs\&lt;platform&gt;\</c>. The Android sdkmanager channel and
/// the settings write-back are wired by the platform nodes on top of this core.
/// </summary>
public static class PlatformToolchainService
{
    /// <summary>The built-in per-user SDK root (<c>%LOCALAPPDATA%\XEngine\PlatformSDKs</c>) — the
    /// fallback when no user location is configured. Exposed for settings UI display.</summary>
    public static string BuiltinCacheRoot { get; } = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "XEngine", "PlatformSDKs");

    public static string GetDefaultCacheRoot()
    {
        // User-selected location (launcher Settings → General → download location) wins so big
        // toolchains can live off the system drive; the per-user default otherwise. Explicit
        // --platform-toolchain-cache / CacheRootOverride still beats this per call.
        string? configured = Theming.EditorSettings.Instance.PlatformSdkCacheRoot;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        string localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
            throw new InvalidOperationException("No per-user local app data directory is available.");
        return Path.Combine(localAppData, "XEngine", "PlatformSDKs");
    }

    public static string GetInstallRoot(PlatformToolchainKind platform, string? cacheRootOverride = null)
        => Path.Combine(string.IsNullOrWhiteSpace(cacheRootOverride) ? GetDefaultCacheRoot() : cacheRootOverride!,
            PlatformFolderName(platform));

    public static async Task<PlatformToolchainInstallResult> InstallAsync(
        Project? project, PlatformToolchainKind platform, string? cacheRootOverride = null,
        IProgress<PlatformToolchainProgress>? progress = null, CancellationToken cancellation = default)
    {
        ToolchainManifest? manifest = ToolchainManifest.ReadForPlatform(project, platform);
        if (manifest == null)
            throw new InvalidOperationException(
                $"Platform module '{ToolchainManifest.ModulePackageName(platform)}' is not installed — "
                + "install it from the launcher Settings > Platforms page (editor-level), "
                + "or pin it in the project's Packages/manifest.json.");
        return await InstallAsync(manifest, cacheRootOverride, progress, cancellation).ConfigureAwait(false);
    }

    /// <summary>Install a manifest the caller already holds — channels use this to run a filtered
    /// view (e.g. the HarmonyOS bundle replaced by a local archive) without rewriting the module
    /// package on disk.</summary>
    public static async Task<PlatformToolchainInstallResult> InstallAsync(
        ToolchainManifest manifest, string? cacheRootOverride = null,
        IProgress<PlatformToolchainProgress>? progress = null, CancellationToken cancellation = default)
    {
        List<string> problems = ToolchainManifest.Validate(manifest);
        if (problems.Count > 0)
            throw new InvalidOperationException($"Invalid toolchain manifest: {string.Join(" ", problems)}");
        if (!ToolchainManifest.TryMapPlatform(manifest.Platform, out PlatformToolchainKind platform))
            throw new InvalidOperationException($"Unknown platform '{manifest.Platform}'.");

        string installRoot = GetInstallRoot(platform, cacheRootOverride);
        string stateDir = Path.Combine(installRoot, ".state");
        string downloadDir = Path.Combine(installRoot, ".downloads");
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(stateDir);

        var results = new List<PlatformToolchainComponentResult>();
        foreach (ToolchainComponent component in manifest.Components)
        {
            cancellation.ThrowIfCancellationRequested();
            results.Add(await InstallComponentAsync(component, installRoot, stateDir, downloadDir,
                progress, cancellation).ConfigureAwait(false));
        }

        return new PlatformToolchainInstallResult
        {
            Platform = manifest.Platform,
            InstallRoot = installRoot,
            Components = results,
        };
    }

    private static async Task<PlatformToolchainComponentResult> InstallComponentAsync(
        ToolchainComponent component, string installRoot, string stateDir, string downloadDir,
        IProgress<PlatformToolchainProgress>? progress, CancellationToken cancellation)
    {
        string statePath = Path.Combine(stateDir, component.Id + ".json");
        string fingerprint = ToolchainInstallState.ComputeFingerprint(component);
        string componentRoot = Path.Combine(installRoot, component.Layout.Replace('/', Path.DirectorySeparatorChar));

        void Emit(string stage, long current, long total, string? detail = null)
            => progress?.Report(new PlatformToolchainProgress(stage, component.Id, current, total, detail));

        // Reuse: matching spec fingerprint + sentinel present (the sdkmanager channel records its
        // own state with ManagedBy= "sdkmanager").
        ToolchainInstallState? existing = ToolchainInstallState.Read(statePath);
        bool sentinelPresent = component.Verify == null
            || File.Exists(Path.Combine(componentRoot, component.Verify.Replace('/', Path.DirectorySeparatorChar)));
        if (existing is { Fingerprint: var fp } && fp == fingerprint && sentinelPresent)
        {
            Emit("cached", 1, 1, $"{component.Id} fingerprint matched; skipping download.");
            return new PlatformToolchainComponentResult
            {
                Id = component.Id,
                Status = PlatformToolchainComponentStatus.Cached,
                Root = componentRoot,
                Sha256 = existing.Sha256,
                SourceUrl = existing.SourceUrl,
            };
        }

        if (component.Kind == ToolchainComponentKind.SdkManager)
        {
            Emit("pending", 0, 0, $"{component.Id} waits for the sdkmanager channel (licenses are accepted in-editor).");
            return new PlatformToolchainComponentResult
            {
                Id = component.Id,
                Status = PlatformToolchainComponentStatus.PendingChannel,
                Root = componentRoot,
            };
        }

        // Acquire: archive/file components download with source failover. The local file name keeps
        // the source extension so the extractor can pick zip vs tar.gz.
        string sourceExtension = Path.GetExtension(new Uri(component.Sources[0].Url).AbsolutePath);
        string fileName = component.Kind == ToolchainComponentKind.File
            ? "file-" + component.Id + sourceExtension
            : "artifact-" + component.Id + (string.IsNullOrWhiteSpace(sourceExtension) ? ".zip" : sourceExtension);
        string targetFile = Path.Combine(downloadDir, SanitizeFileName(fileName));

        // An interrupted install may have left a fully downloaded artifact behind: when it verifies
        // against a pinned or previously recorded hash, the network round-trip is skipped.
        if (File.Exists(targetFile))
        {
            string? knownHash = component.Sources
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Sha256))?.Sha256 ?? existing?.Sha256;
            if (!string.IsNullOrWhiteSpace(knownHash))
            {
                try
                {
                    string cachedSha = await ToolchainArchive.ComputeSha256Async(targetFile, cancellation)
                        .ConfigureAwait(false);
                    if (string.Equals(cachedSha, knownHash, StringComparison.OrdinalIgnoreCase))
                    {
                        Emit("verified-cache", 1, 1, "previously downloaded artifact verified; skipping download.");
                        return await MaterializeAsync(component, targetFile, fingerprint, statePath,
                            existing?.SourceUrl ?? "verified-cache", cachedSha, componentRoot,
                            Emit, cancellation).ConfigureAwait(false);
                    }
                }
                catch (IOException)
                {
                    // Unreadable leftover — fall through to a fresh download.
                }
            }
        }

        string? lastError = null;
        foreach (ToolchainSource source in component.Sources)
        {
            try
            {
                Emit("download", 0, 0, source.Url);
                string? pinned = source.Sha256
                    ?? (existing?.SourceUrl == source.Url ? existing.Sha256 : null);
                string actualSha = await ToolchainArchive.DownloadAsync(
                    source.Url, targetFile, pinned,
                    new Progress<DownloadProgress>(p =>
                        Emit("download", p.BytesReceived, p.TotalBytes <= 0 ? 0 : p.TotalBytes)),
                    cancellation).ConfigureAwait(false);

                return await MaterializeAsync(component, targetFile, fingerprint, statePath,
                    source.Url, actualSha, componentRoot, Emit, cancellation)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ToolchainArchiveException or HttpRequestException
                or IOException or InvalidDataException or TaskCanceledException or InvalidOperationException)
            {
                string frame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
                lastError = $"{source.Url}: {ex.GetType().Name}: {ex.Message} | {frame}";
                Emit("failover", 0, 0, lastError);
            }
        }

        return new PlatformToolchainComponentResult
        {
            Id = component.Id,
            Status = PlatformToolchainComponentStatus.Failed,
            Root = componentRoot,
            Error = lastError ?? "no sources were available",
        };
    }

    /// <summary>Publish a verified artifact into the component layout: single-file components copy
    /// verbatim; archives extract into a staging sibling and merge atomically.</summary>
    private static async Task<PlatformToolchainComponentResult> MaterializeAsync(
        ToolchainComponent component, string targetFile, string fingerprint, string statePath,
        string sourceUrl, string actualSha, string componentRoot,
        Action<string, long, long, string?> emit, CancellationToken cancellation)
    {
        if (component.Kind == ToolchainComponentKind.File)
        {
            // Single-file components publish straight into their layout.
            Directory.CreateDirectory(Path.GetDirectoryName(componentRoot)!);
            File.Copy(targetFile, componentRoot, overwrite: true);
            WriteState(statePath, component, fingerprint, sourceUrl, actualSha, componentRoot);
            emit("installed", 1, 1, componentRoot);
            return new PlatformToolchainComponentResult
            {
                Id = component.Id,
                Status = PlatformToolchainComponentStatus.Installed,
                Root = componentRoot,
                Sha256 = actualSha,
                SourceUrl = sourceUrl,
            };
        }

        // Archive components extract DIRECTLY into the layout target. Correctness is guarded by the
        // state file (fingerprint + sentinel), so a partial tree is simply re-extracted on rerun —
        // and a staging→layout Directory.Move would race Defender scanning freshly extracted
        // binaries (observed as MoveDirectory "access denied" on real vendor zips).
        emit("extract", 0, 0, null);
        Directory.CreateDirectory(componentRoot);
        if (ToolchainArchive.IsTarGz(targetFile))
            await ToolchainArchive.ExtractTarGzAsync(targetFile, componentRoot, component.StripRootSegments,
                new Progress<long>(b => emit("extract", b, 0, null)), cancellation).ConfigureAwait(false);
        else
            await ToolchainArchive.ExtractZipAsync(targetFile, componentRoot, component.StripRootSegments,
                new Progress<long>(b => emit("extract", b, 0, null)), cancellation).ConfigureAwait(false);

        // Post-extract: archives-of-archives (the OpenHarmony combined SDK) ship per-component
        // zips under a host subfolder; unpack them into the component root so the resolver sees
        // the final layout (native/…, toolchains/hdc).
        if (component.UnpackZipsUnder != null)
        {
            string innerRoot = Path.Combine(componentRoot,
                component.UnpackZipsUnder.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(innerRoot))
            {
                foreach (string innerZip in Directory.EnumerateFiles(innerRoot, "*.zip", SearchOption.TopDirectoryOnly))
                {
                    emit("unpack", 0, 0, Path.GetFileName(innerZip));
                    await ToolchainArchive.ExtractZipAsync(innerZip, componentRoot, 0,
                        new Progress<long>(b => emit("unpack", b, 0, null)), cancellation).ConfigureAwait(false);
                }
            }
        }

        if (component.Verify != null && !File.Exists(
                Path.Combine(componentRoot, component.Verify.Replace('/', Path.DirectorySeparatorChar))))
            throw new ToolchainArchiveException(
                $"Installed component '{component.Id}' is missing its verify sentinel '{component.Verify}'.");
        WriteState(statePath, component, fingerprint, sourceUrl, actualSha, componentRoot);
        emit("installed", 1, 1, componentRoot);
        return new PlatformToolchainComponentResult
        {
            Id = component.Id,
            Status = PlatformToolchainComponentStatus.Installed,
            Root = componentRoot,
            Sha256 = actualSha,
            SourceUrl = sourceUrl,
        };
    }

    private static void WriteState(string statePath, ToolchainComponent component, string fingerprint,
        string sourceUrl, string? sha256, string root)
    {
        ToolchainInstallState.Write(statePath, new ToolchainInstallState
        {
            Id = component.Id,
            Version = component.Version,
            Fingerprint = fingerprint,
            SourceUrl = sourceUrl,
            Sha256 = sha256,
            InstalledAtUtc = DateTime.UtcNow.ToString("O"),
            Root = root,
            ManagedBy = "archive",
        });
    }

    public static string PlatformFolderName(PlatformToolchainKind platform) => platform switch
    {
        PlatformToolchainKind.HarmonyOS => "harmonyos",
        PlatformToolchainKind.Ios => "ios",
        _ => "android",
    };

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
