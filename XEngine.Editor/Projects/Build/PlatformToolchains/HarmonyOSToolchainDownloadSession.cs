// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Core.Progress;

namespace XEngine.Editor.Projects.Build.PlatformToolchains;

public enum HarmonyOSToolchainDownloadState
{
    Idle,
    Running,
    Succeeded,
    /// <summary>SDK half is ready; the SDK Bundle needs a local archive or the hosted feed.</summary>
    Partial,
    Failed,
}

public sealed record HarmonyOSToolchainDownloadSnapshot(
    HarmonyOSToolchainDownloadState State,
    string? Message = null,
    string? OfficialSdkRoot = null,
    string? HdcPath = null,
    string? Error = null)
{
    public static readonly HarmonyOSToolchainDownloadSnapshot Idle = new(HarmonyOSToolchainDownloadState.Idle);
}

/// <summary>
/// UI session for the managed HarmonyOS toolchain download — the Android session's counterpart:
/// runs <see cref="HarmonyOSToolchainChannel"/> on a worker task, reports through
/// <see cref="EditorProgress"/> (cancellable), keeps an immutable snapshot for the build panel.
/// </summary>
public sealed class HarmonyOSToolchainDownloadSession
{
    private readonly object _gate = new();
    private HarmonyOSToolchainDownloadSnapshot _snapshot = HarmonyOSToolchainDownloadSnapshot.Idle;
    private CancellationTokenSource? _cancellation;

    public HarmonyOSToolchainDownloadSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public bool IsRunning => Snapshot.State == HarmonyOSToolchainDownloadState.Running;

    public void Start(Project project, string? cacheRootOverride = null, string? localBundleArchive = null)
    {
        lock (_gate)
        {
            if (_snapshot.State == HarmonyOSToolchainDownloadState.Running) return;
            _cancellation = new CancellationTokenSource();
            _snapshot = new HarmonyOSToolchainDownloadSnapshot(HarmonyOSToolchainDownloadState.Running,
                Message: "Preparing managed HarmonyOS toolchain install…");
        }

        CancellationTokenSource cancellation = _cancellation!;
        _ = Task.Run(() => RunAsync(project, cacheRootOverride, localBundleArchive, cancellation));
    }

    public void Cancel()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    private async Task RunAsync(Project project, string? cacheRootOverride, string? localBundleArchive,
        CancellationTokenSource cancellation)
    {
        ProgressHandle handle = EditorProgress.Start(EditorProgressKind.Custom,
            "HarmonyOS Toolchain Install", total: 0, EditorProgressVisibility.ImmediatePopup);
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

            HarmonyOSChannelResult result = await HarmonyOSToolchainChannel.InstallAsync(
                new HarmonyOSChannelOptions
                {
                    Project = project,
                    CacheRootOverride = cacheRootOverride,
                    LocalBundleArchivePath = localBundleArchive,
                    Progress = progress,
                    CancellationToken = cancellation.Token,
                }).ConfigureAwait(false);

            if (!result.Success)
            {
                Set(new HarmonyOSToolchainDownloadSnapshot(HarmonyOSToolchainDownloadState.Failed,
                    OfficialSdkRoot: result.OfficialSdkRoot, HdcPath: result.HdcPath,
                    Error: result.Error ?? result.BundleImportError));
                EditorProgress.Finish(handle, success: false, "HarmonyOS toolchain install failed.");
                return;
            }

            bool partial = result.BundleImportError != null;
            Set(new HarmonyOSToolchainDownloadSnapshot(partial
                    ? HarmonyOSToolchainDownloadState.Partial
                    : HarmonyOSToolchainDownloadState.Succeeded,
                Message: partial
                    ? "SDK installed; the SDK Bundle needs a local archive or the hosted feed."
                    : "SDK + Bundle ready.",
                OfficialSdkRoot: result.OfficialSdkRoot, HdcPath: result.HdcPath));
            EditorProgress.Finish(handle, success: true, "HarmonyOS toolchain installed.");
        }
        catch (OperationCanceledException)
        {
            Set(new HarmonyOSToolchainDownloadSnapshot(HarmonyOSToolchainDownloadState.Failed,
                Error: "Cancelled."));
            EditorProgress.Finish(handle, success: false, "HarmonyOS toolchain install cancelled.");
        }
        catch (Exception ex)
        {
            Set(new HarmonyOSToolchainDownloadSnapshot(HarmonyOSToolchainDownloadState.Failed,
                Error: ex.Message));
            EditorProgress.Finish(handle, success: false, "HarmonyOS toolchain install failed.");
        }
    }

    private void Set(HarmonyOSToolchainDownloadSnapshot snapshot)
    {
        lock (_gate) _snapshot = snapshot;
    }
}
