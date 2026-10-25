// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build.HarmonyOS;
using XEngine.Editor.Projects.Packages;
using XEngine.Editor.Theming;

namespace XEngine.Editor.Projects.Build.PlatformToolchains;

public sealed class HarmonyOSChannelOptions
{
    /// <summary>Project whose PackageManager may supply the module (legacy manifest pin), and the
    /// import target for a local SDK Bundle. Editor-level resolution (module store / Editor bundle)
    /// runs first, so this can be null — the launcher installs SDKs without any project open; a
    /// local bundle import is then skipped and reported as guidance (the SDK itself still lands).</summary>
    public required Project? Project { get; init; }
    public string? CacheRootOverride { get; init; }

    /// <summary>Local SDK Bundle archive (zip) routed through the existing verified importer —
    /// used when the GitHub Releases feed has not hosted the bundle yet.</summary>
    public string? LocalBundleArchivePath { get; init; }

    /// <summary>Skip the sdk-bundle component entirely (SDK-root-only install: toolchain +
    /// hdc). HarmonyOS BUILDS still need the bundle; this is for staging the big SDK first.</summary>
    public bool SkipBundle { get; init; }

    public bool WriteEditorSettings { get; init; } = true;
    public IProgress<PlatformToolchainProgress>? Progress { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed class HarmonyOSChannelResult
{
    public required PlatformToolchainInstallResult Core { get; init; }

    /// <summary>The official SDK root the resolver consumes
    /// (<c>&lt;root&gt;/&lt;folder&gt;/native/oh-uni-package.json</c> semantics).</summary>
    public string? OfficialSdkRoot { get; init; }

    /// <summary>hdc executable resolved from the installed toolchains (null when absent).</summary>
    public string? HdcPath { get; init; }

    public string? BundleImportError { get; init; }
    public string? Error { get; init; }
    public bool Success => Error == null && Core.Success && BundleImportError == null;
}

/// <summary>
/// HarmonyOS acquisition channel on top of <see cref="PlatformToolchainService"/>: installs the
/// official OpenHarmony SDK archive (native toolchain + toolchains incl. hdc) from the Huawei Cloud
/// open mirror into the managed SDK root, then routes the SDK Bundle through the EXISTING verified
/// importer (remote GitHub Releases feed, or a local archive while the feed is not yet hosted), and
/// writes the official SDK root back into EditorSettings for
/// <see cref="HarmonyOSToolchainResolver"/>. Resolver semantics are untouched.
/// </summary>
public static class HarmonyOSToolchainChannel
{
    public static async Task<HarmonyOSChannelResult> InstallAsync(HarmonyOSChannelOptions options)
    {
        IProgress<PlatformToolchainProgress>? progress = options.Progress;
        void Emit(string stage, string? detail = null)
            => progress?.Report(new PlatformToolchainProgress(stage, "ohos-sdk", 0, 0, detail));

        // 1. Archive components (ohos-sdk; sdk-bundle when its feed hosts it). A local bundle
        //    archive or SkipBundle removes the remote-bundle component from the plan.
        ToolchainManifest? manifest = ToolchainManifest.ReadForPlatform(options.Project, PlatformToolchainKind.HarmonyOS);
        if (manifest == null)
            throw new InvalidOperationException(
                "Platform module 'com.xengine.platform.harmonyos' is not installed — install it from the "
                + "launcher Settings > Platforms page (editor-level), or pin it in the project manifest.");
        List<string> problems = ToolchainManifest.Validate(manifest);
        if (problems.Count > 0)
            throw new InvalidOperationException($"Invalid toolchain manifest: {string.Join(" ", problems)}");

        bool localBundle = !string.IsNullOrWhiteSpace(options.LocalBundleArchivePath) && File.Exists(options.LocalBundleArchivePath);
        string? bundleArchiveOverride = options.SkipBundle ? null
            : localBundle ? options.LocalBundleArchivePath!
            : null;

        // Run the core with a bundle-less view when a local archive/SkipBundle replaces the feed;
        // the manifest overload lets the channel install exactly this plan without touching the
        // module package on disk.
        PlatformToolchainInstallResult core = await PlatformToolchainService.InstallAsync(
            bundleArchiveOverride != null || options.SkipBundle ? FilterBundleComponent(manifest) : manifest,
            options.CacheRootOverride, progress, options.CancellationToken).ConfigureAwait(false);
        // A failed remote bundle is guidance, not a hard stop (the feed is not hosted yet); every
        // other failed component is.
        bool hardFailure = core.Components.Any(c =>
            c.Status == PlatformToolchainComponentStatus.Failed && c.Id != "sdk-bundle");
        if (hardFailure)
            return new HarmonyOSChannelResult { Core = core, Error = "component download failed" };

        // 2. Official SDK root semantics: the resolver expects <sdkRoot>/<folder>/native/… — the
        //    archive's layout already provides that under the managed root, so the root we hand
        //    over is the parent of the component layout ("ohos-sdk" itself when layout matches).
        string sdkComponentRoot = core.Components
            .FirstOrDefault(c => c.Id == "ohos-sdk")?.Root ?? "";
        string? officialRoot = null;
        string? hdcPath = null;
        if (sdkComponentRoot.Length > 0 && Directory.Exists(sdkComponentRoot))
        {
            officialRoot = Directory.GetParent(sdkComponentRoot)?.FullName;
            hdcPath = LocateHdc(sdkComponentRoot);
        }

        // 3. SDK Bundle through the existing verified importer (local archive), or guidance.
        string? bundleError = null;
        if (!options.SkipBundle)
        {
            if (bundleArchiveOverride != null)
            {
                if (options.Project == null)
                {
                    // Launcher-mode install (no project open): the bundle imports into a project via
                    // the verified importer, so record guidance instead — the SDK itself is installed.
                    bundleError = "SDK Bundle import skipped (no project open) — import the local bundle "
                        + "from a project's HarmonyOS Build profile to finish the toolchain.";
                }
                else
                {
                    Emit("bundle-import", bundleArchiveOverride);
                    try
                    {
                        HarmonyOSSdkBundleImportResult import = await new HarmonyOSSdkBundleImporter()
                            .ImportAsync(new HarmonyOSSdkBundleImportRequest(
                                options.Project.RootPath, bundleArchiveOverride))
                            .ConfigureAwait(false);
                        Emit("bundle-imported", $"release {import.Lock.ReleaseId} at {import.Lock.BundleRelativePath}");
                    }
                    catch (HarmonyOSBundleException exception)
                    {
                        bundleError = $"SDK Bundle import failed: {exception.Message}";
                    }
                }
            }
            else
            {
                ToolchainComponent? bundleComponent = manifest.Components.FirstOrDefault(c => c.Id == "sdk-bundle");
                var bundleStatus = core.Components.FirstOrDefault(c => c.Id == "sdk-bundle");
                if (bundleComponent != null && bundleStatus is { Status: PlatformToolchainComponentStatus.Failed })
                {
                    bundleError = "The XEngine-Toolchains GitHub Releases feed does not host the SDK Bundle yet. " +
                        "Pass a local bundle archive (HarmonyOSSdkBundleSourcePath / --harmonyos-bundle) to import it through the verified path, " +
                        $"or host the self-built bundle at the manifest URL. Source error: {bundleStatus.Error}";
                }
            }
        }

        // 4. Settings write-back (empty slots or already-managed paths only).
        if (options.WriteEditorSettings && officialRoot != null)
        {
            EditorSettings settings = EditorSettings.Instance;
            settings.HarmonyOSOfficialSdkRoot = string.IsNullOrWhiteSpace(settings.HarmonyOSOfficialSdkRoot)
                || settings.HarmonyOSOfficialSdkRoot.StartsWith(core.InstallRoot, StringComparison.OrdinalIgnoreCase)
                ? officialRoot
                : settings.HarmonyOSOfficialSdkRoot;
            settings.Save();
        }

        Emit(officialRoot == null ? "partial" : "installed",
            officialRoot == null ? "ohos-sdk component missing" : $"SDK root {officialRoot}; hdc: {hdcPath ?? "not found"}");
        return new HarmonyOSChannelResult
        {
            Core = core,
            OfficialSdkRoot = officialRoot,
            HdcPath = hdcPath,
            BundleImportError = bundleError,
        };
    }

    private static ToolchainManifest FilterBundleComponent(ToolchainManifest manifest)
    {
        var filtered = new ToolchainManifest
        {
            ManifestVersion = manifest.ManifestVersion,
            Platform = manifest.Platform,
            HostRequirement = manifest.HostRequirement,
            Notes = manifest.Notes,
        };
        filtered.Components.AddRange(manifest.Components.Where(c => c.Id != "sdk-bundle"));
        filtered.Locators.AddRange(manifest.Locators);
        return filtered;
    }

    private static string? LocateHdc(string sdkComponentRoot)
    {
        // <root>/toolchains/hdc(.exe) directly, or nested one level (windows subfolder).
        string direct = Path.Combine(sdkComponentRoot, "toolchains",
            OperatingSystem.IsWindows() ? "hdc.exe" : "hdc");
        if (File.Exists(direct)) return direct;
        foreach (string nested in Directory.EnumerateDirectories(Path.Combine(sdkComponentRoot, "toolchains"), "*", SearchOption.TopDirectoryOnly))
        {
            string candidate = Path.Combine(nested, OperatingSystem.IsWindows() ? "hdc.exe" : "hdc");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
