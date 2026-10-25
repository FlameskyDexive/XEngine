// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build.Android;
using XEngine.Editor.Build.Ios;
using XEngine.Editor.Projects.Packages.EditorModules;

namespace XEngine.Editor.Projects.Build.PlatformToolchains;

/// <summary>Coarse states of a one-click platform install (module + SDK toolchain).</summary>
public enum PlatformModuleInstallState
{
    Idle,
    /// <summary>Fetching/installing the com.xengine.platform.* module into the editor module store.</summary>
    InstallingModule,
    /// <summary>Android only: Google SDK licenses must be consented before sdkmanager runs.</summary>
    AwaitingConsent,
    /// <summary>Downloading/verifying/extracting SDK components through the platform channel.</summary>
    InstallingSdk,
    Succeeded,
    /// <summary>SDK ready but something non-fatal needs guidance (e.g. HarmonyOS SDK Bundle import).</summary>
    Partial,
    Failed,
    Canceled,
}

/// <summary>Immutable view of a running/finished install, polled by the launcher settings UI.</summary>
public sealed record PlatformModuleInstallSnapshot(
    PlatformModuleInstallState State = PlatformModuleInstallState.Idle,
    string? Message = null,
    string? Error = null,
    /// <summary>Non-fatal guidance (HarmonyOS bundle import path); shown amber when Partial.</summary>
    string? Guidance = null,
    long Current = 0,
    long Total = 0,
    string? SdkRoot = null)
{
    public static readonly PlatformModuleInstallSnapshot Idle = new();
    public bool Running => State is PlatformModuleInstallState.InstallingModule
        or PlatformModuleInstallState.InstallingSdk;
}

public sealed class PlatformModuleInstallOptions
{
    public required PlatformToolchainKind Platform { get; init; }
    /// <summary>Project supplying a legacy manifest-pinned module and the HarmonyOS bundle import
    /// target; null in launcher mode (editor-level module + no bundle import).</summary>
    public Project? Project { get; init; }
    /// <summary>Android: explicit consent to the Google Android SDK license terms.</summary>
    public bool AcceptAndroidLicenses { get; init; }
    /// <summary>HarmonyOS: local SDK Bundle archive routed through the verified importer.</summary>
    public string? LocalBundleArchivePath { get; init; }
    public string? CacheRootOverride { get; init; }
    /// <summary>Test seams: skip EditorSettings write-back and inject a fake sdkmanager runner.</summary>
    public bool WriteEditorSettings { get; init; } = true;
    internal IAndroidToolchainProcessRunner? Runner { get; init; }
    /// <summary>Registry override for the module npm install (tests point this at a FakeNpmServer).</summary>
    public string? ModuleRegistryUrl { get; init; }
}

/// <summary>
/// One-click editor-level platform install: module (npm → editor module store) → [Android license
/// gate] → SDK toolchain channel (JDK/SDK/NDK, ohos-sdk, or the iOS locator), driven from the
/// launcher Settings page — no project required. Runs on a worker task and publishes immutable
/// snapshots; cancellation flows into both phases. This is the launcher counterpart of the build
/// profiles' download sessions, generalized across all three platforms.
/// </summary>
public sealed class PlatformModuleInstallSession
{
    private volatile PlatformModuleInstallSnapshot _snapshot = PlatformModuleInstallSnapshot.Idle;
    private CancellationTokenSource? _cancellation;

    public PlatformModuleInstallSnapshot Snapshot => _snapshot;

    public bool Running => _snapshot.Running;

    public void Cancel() => _cancellation?.Cancel();

    /// <summary>Run the full flow on a worker task. Returns the task (for tests); the UI polls
    /// <see cref="Snapshot"/> instead of awaiting.</summary>
    public Task Start(PlatformModuleInstallOptions options)
    {
        if (Running) return Task.CompletedTask;
        _cancellation = new CancellationTokenSource();
        _snapshot = new PlatformModuleInstallSnapshot(PlatformModuleInstallState.InstallingModule,
            "Installing platform module…");
        return Task.Run(() => RunAsync(options, _cancellation.Token));
    }

    private async Task RunAsync(PlatformModuleInstallOptions options, CancellationToken cancellation)
    {
        try
        {
            // 1. Module: editor-level presence first (store or Editor bundle); npm-install when
            //    neither is there. Reuses N1's idempotent installer, so an already-installed module
            //    is a zero-network fast path.
            if (!ToolchainManifest.IsEditorModulePresent(options.Platform))
            {
                var moduleProgress = new ModuleProgressAdapter(this);
                await EditorModuleInstaller.InstallAsync(
                    ToolchainManifest.ModulePackageName(options.Platform),
                    registryUrl: options.ModuleRegistryUrl,
                    progress: moduleProgress, cancellation: cancellation).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();

            // 2. Platform dispatch. iOS downloads nothing (Apple licensing): the "SDK step" is the
            //    Xcode locator, whose outcome is guidance rather than failure on non-mac hosts.
            if (options.Platform == PlatformToolchainKind.Ios)
            {
                _snapshot = new PlatformModuleInstallSnapshot(PlatformModuleInstallState.InstallingSdk,
                    "Locating Xcode…");
                XcodeLocatorResult locator = await new XcodeLocator().LocateAsync(new Version(16, 0), cancellation)
                    .ConfigureAwait(false);
                if (locator.Found && locator.MeetsMinimum)
                    _snapshot = new PlatformModuleInstallSnapshot(PlatformModuleInstallState.Succeeded,
                        $"Xcode {locator.Version} at {locator.DeveloperDir}");
                else
                    _snapshot = new PlatformModuleInstallSnapshot(PlatformModuleInstallState.Partial,
                        "Xcode not located", Guidance: locator.Guidance);
                return;
            }

            if (options.Platform == PlatformToolchainKind.Android)
            {
                // 3. License gate — same rule as the build profile: never silently accept. Probed on
                //    the manifest's sdkmanager component root so launcher mode (no project) is covered.
                ToolchainManifest? manifest = ToolchainManifest.ReadForPlatform(options.Project, options.Platform);
                ToolchainComponent? sdkComponent = manifest?.Components.Find(
                    static c => c.Kind == ToolchainComponentKind.SdkManager);
                if (manifest != null && sdkComponent != null)
                {
                    string sdkRoot = Path.Combine(
                        PlatformToolchainService.GetInstallRoot(options.Platform, options.CacheRootOverride),
                        sdkComponent.Layout.Replace('/', Path.DirectorySeparatorChar));
                    if (!AndroidToolchainChannel.LicensesAccepted(Path.Combine(sdkRoot, "licenses"))
                        && !options.AcceptAndroidLicenses)
                    {
                        _snapshot = new PlatformModuleInstallSnapshot(
                            PlatformModuleInstallState.AwaitingConsent,
                            "Accept the Google Android SDK licenses to continue.");
                        return;
                    }
                }
            }

            // 4. SDK channel.
            _snapshot = new PlatformModuleInstallSnapshot(PlatformModuleInstallState.InstallingSdk,
                "Downloading SDK components…");
            if (options.Platform == PlatformToolchainKind.Android)
            {
                var result = await AndroidToolchainChannel.InstallAsync(new AndroidChannelOptions
                {
                    Project = options.Project,
                    CacheRootOverride = options.CacheRootOverride,
                    AcceptLicenses = options.AcceptAndroidLicenses,
                    WriteEditorSettings = options.WriteEditorSettings,
                    Runner = options.Runner,
                    Progress = new ToolchainProgressAdapter(this),
                    CancellationToken = cancellation,
                }).ConfigureAwait(false);
                if (result.NeedsLicenseConsent)
                {
                    _snapshot = new PlatformModuleInstallSnapshot(
                        PlatformModuleInstallState.AwaitingConsent,
                        "Accept the Google Android SDK licenses to continue.");
                }
                else if (result.Success)
                {
                    _snapshot = new PlatformModuleInstallSnapshot(PlatformModuleInstallState.Succeeded,
                        $"{result.InstalledPackages.Count} SDK packages ready", SdkRoot: result.Core.InstallRoot);
                }
                else
                {
                    _snapshot = new PlatformModuleInstallSnapshot(PlatformModuleInstallState.Failed,
                        Error: result.Error ?? "Android toolchain install failed",
                        SdkRoot: result.Core.InstallRoot);
                }
                return;
            }

            HarmonyOSChannelResult harmonyOS = await HarmonyOSToolchainChannel.InstallAsync(new HarmonyOSChannelOptions
            {
                Project = options.Project,
                CacheRootOverride = options.CacheRootOverride,
                LocalBundleArchivePath = options.LocalBundleArchivePath,
                WriteEditorSettings = options.WriteEditorSettings,
                Progress = new ToolchainProgressAdapter(this),
                CancellationToken = cancellation,
            }).ConfigureAwait(false);
            if (harmonyOS.Success)
            {
                _snapshot = new PlatformModuleInstallSnapshot(PlatformModuleInstallState.Succeeded,
                    $"SDK root {harmonyOS.OfficialSdkRoot}; hdc: {harmonyOS.HdcPath ?? "not found"}",
                    SdkRoot: harmonyOS.OfficialSdkRoot);
            }
            else if (harmonyOS.OfficialSdkRoot != null)
            {
                // SDK itself landed; only the bundle needs guidance — amber, not red.
                _snapshot = new PlatformModuleInstallSnapshot(PlatformModuleInstallState.Partial,
                    $"SDK ready at {harmonyOS.OfficialSdkRoot}",
                    Guidance: harmonyOS.BundleImportError, SdkRoot: harmonyOS.OfficialSdkRoot);
            }
            else
            {
                _snapshot = new PlatformModuleInstallSnapshot(PlatformModuleInstallState.Failed,
                    Error: harmonyOS.Error ?? harmonyOS.BundleImportError ?? "HarmonyOS toolchain install failed");
            }
        }
        catch (OperationCanceledException)
        {
            _snapshot = new PlatformModuleInstallSnapshot(PlatformModuleInstallState.Canceled, "Canceled.");
        }
        catch (Exception exception)
        {
            _snapshot = new PlatformModuleInstallSnapshot(PlatformModuleInstallState.Failed,
                Error: exception.Message);
        }
    }

    // ------------------------------------------------------------------
    //  SDK status detection (card display when no session is running)
    // ------------------------------------------------------------------

    /// <summary>Aggregated readiness of the managed SDK root for a platform.</summary>
    public sealed record SdkStatus(int ComponentsInstalled, int ComponentsTotal, string? Root)
    {
        public bool Ready => ComponentsTotal > 0 && ComponentsInstalled == ComponentsTotal;
        public bool Partial => ComponentsInstalled > 0 && ComponentsInstalled < ComponentsTotal;
    }

    /// <summary>
    /// Count installed components under the managed SDK root: a component counts when its
    /// <c>.state/&lt;id&gt;.json</c> exists AND the root it recorded is still on disk. Total comes from
    /// the module manifest when available (store/bundle/project), so the card can say "2/4".
    /// </summary>
    public static SdkStatus GetSdkStatus(PlatformToolchainKind platform, string? cacheRootOverride = null)
    {
        string installRoot = PlatformToolchainService.GetInstallRoot(platform, cacheRootOverride);
        string stateDir = Path.Combine(installRoot, ".state");
        int installed = 0;
        if (Directory.Exists(stateDir))
        {
            foreach (string file in Directory.EnumerateFiles(stateDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                ToolchainInstallState? state = ToolchainInstallState.Read(file);
                if (state == null) continue;
                // File-kind components (bundletool.jar) record the file itself as root.
                if (state.Root.Length > 0 && (Directory.Exists(state.Root) || File.Exists(state.Root)))
                    installed++;
            }
        }

        ToolchainManifest? manifest = ToolchainManifest.ReadForPlatform(null, platform);
        int total = manifest?.Components.Count ?? 0;
        return new SdkStatus(installed, total, Directory.Exists(installRoot) ? installRoot : null);
    }

    private sealed class ModuleProgressAdapter : IProgress<EditorModuleProgress>
    {
        private readonly PlatformModuleInstallSession _session;
        public ModuleProgressAdapter(PlatformModuleInstallSession session) => _session = session;
        public void Report(EditorModuleProgress value) => _session._snapshot = new PlatformModuleInstallSnapshot(
            PlatformModuleInstallState.InstallingModule, value.Message,
            Current: value.BytesReceived, Total: value.BytesTotal);
    }

    private sealed class ToolchainProgressAdapter : IProgress<PlatformToolchainProgress>
    {
        private readonly PlatformModuleInstallSession _session;
        public ToolchainProgressAdapter(PlatformModuleInstallSession session) => _session = session;
        public void Report(PlatformToolchainProgress value) => _session._snapshot =
            new PlatformModuleInstallSnapshot(PlatformModuleInstallState.InstallingSdk,
                value.Detail ?? value.Stage, Current: value.Current, Total: value.Total);
    }
}
