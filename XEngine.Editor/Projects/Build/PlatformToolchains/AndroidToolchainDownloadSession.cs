// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Core.Progress;

namespace XEngine.Editor.Projects.Build.PlatformToolchains;

public enum AndroidToolchainDownloadState
{
    Idle,
    Running,
    Succeeded,
    Failed,
    /// <summary>The license checkbox was not ticked — the channel stopped before sdkmanager.</summary>
    NeedsConsent,
}

public sealed record AndroidToolchainDownloadSnapshot(
    AndroidToolchainDownloadState State,
    string? Message = null,
    string? InstallRoot = null,
    string? Error = null)
{
    public static readonly AndroidToolchainDownloadSnapshot Idle = new(AndroidToolchainDownloadState.Idle);
}

/// <summary>
/// UI session for the managed Android toolchain download: runs
/// <see cref="AndroidToolchainChannel"/> on a worker task, surfaces stage/byte progress through
/// <see cref="EditorProgress"/> (corner card + status bar, cancellable), and keeps an immutable
/// snapshot for the build panel to poll — the same pattern as
/// <c>AndroidToolchainImportSession</c>.
/// </summary>
public sealed class AndroidToolchainDownloadSession
{
    private readonly object _gate = new();
    private AndroidToolchainDownloadSnapshot _snapshot = AndroidToolchainDownloadSnapshot.Idle;
    private CancellationTokenSource? _cancellation;

    public AndroidToolchainDownloadSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public bool IsRunning => Snapshot.State == AndroidToolchainDownloadState.Running;

    public void Start(Project project, bool acceptLicenses, string? cacheRootOverride = null)
    {
        lock (_gate)
        {
            if (_snapshot.State == AndroidToolchainDownloadState.Running) return;
            _cancellation = new CancellationTokenSource();
            _snapshot = new AndroidToolchainDownloadSnapshot(AndroidToolchainDownloadState.Running,
                Message: "Preparing managed toolchain install…");
        }

        CancellationTokenSource cancellation = _cancellation!;
        _ = Task.Run(() => RunAsync(project, acceptLicenses, cacheRootOverride, cancellation));
    }

    public void Cancel()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    private async Task RunAsync(Project project, bool acceptLicenses, string? cacheRootOverride,
        CancellationTokenSource cancellation)
    {
        ProgressHandle handle = EditorProgress.Start(EditorProgressKind.Custom,
            "Android Toolchain Install", total: 0, EditorProgressVisibility.ImmediatePopup);
        EditorProgress.RegisterCancel(handle, Cancel);
        try
        {
            var progress = new Progress<PlatformToolchainProgress>(p =>
            {
                string detail = p.Detail ?? p.Stage;
                if (p.Total > 0)
                    detail += $" ({p.Current / 1024 / 1024} / {p.Total / 1024 / 1024} MB)";
                EditorProgress.Report(handle, 0, $"[{p.ComponentId}] {detail}");
            });

            AndroidChannelResult result = await AndroidToolchainChannel.InstallAsync(
                new AndroidChannelOptions
                {
                    Project = project,
                    CacheRootOverride = cacheRootOverride,
                    AcceptLicenses = acceptLicenses,
                    Progress = progress,
                    CancellationToken = cancellation.Token,
                }).ConfigureAwait(false);

            if (result.NeedsLicenseConsent)
            {
                Set(new AndroidToolchainDownloadSnapshot(AndroidToolchainDownloadState.NeedsConsent,
                    Message: "Accept the Google Android SDK licenses to let sdkmanager run.",
                    InstallRoot: result.Core.InstallRoot));
                EditorProgress.Finish(handle, success: false,
                    "Android toolchain: license consent required.");
                return;
            }
            if (!result.Success)
            {
                Set(new AndroidToolchainDownloadSnapshot(AndroidToolchainDownloadState.Failed,
                    InstallRoot: result.Core.InstallRoot, Error: result.Error));
                EditorProgress.Finish(handle, success: false, "Android toolchain install failed.");
                return;
            }

            Set(new AndroidToolchainDownloadSnapshot(AndroidToolchainDownloadState.Succeeded,
                Message: $"{result.InstalledPackages.Count} SDK packages + components ready.",
                InstallRoot: result.Core.InstallRoot));
            EditorProgress.Finish(handle, success: true, "Android toolchain installed.");
        }
        catch (OperationCanceledException)
        {
            Set(new AndroidToolchainDownloadSnapshot(AndroidToolchainDownloadState.Failed,
                Error: "Cancelled."));
            EditorProgress.Finish(handle, success: false, "Android toolchain install cancelled.");
        }
        catch (Exception ex)
        {
            Set(new AndroidToolchainDownloadSnapshot(AndroidToolchainDownloadState.Failed,
                Error: ex.Message));
            EditorProgress.Finish(handle, success: false, "Android toolchain install failed.");
        }
    }

    private void Set(AndroidToolchainDownloadSnapshot snapshot)
    {
        lock (_gate) _snapshot = snapshot;
    }
}
