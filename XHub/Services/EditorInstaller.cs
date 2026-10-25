using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XHub.Infrastructure;
using XHub.Models;

namespace XHub.Services;

/// <summary>
/// Facade tying the release feed, the download queue and install/uninstall together (§5).
/// Completed downloads automatically flow into a serial install; failures keep the operation
/// on the downloads page with its error and a retry affordance.
/// </summary>
public sealed class EditorInstaller
{
    public ReleaseFeedService Feed { get; }
    public DownloadService Downloads { get; }
    public InstallationLibrary Installations { get; }
    public InstallService Installer { get; }

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public event Action<OperationProgress>? OperationProgress;

    public EditorInstaller(InstallationLibrary installations, Func<InstallationRecord, bool>? isInUse = null,
        ReleaseFeedService? feed = null, DownloadService? downloads = null)
    {
        Installations = installations;
        Feed = feed ?? new ReleaseFeedService();
        Downloads = downloads ?? new DownloadService();
        Installer = new InstallService(installations, isInUse);
        Downloads.Progress += OnDownloadProgress;
    }

    private void OnDownloadProgress(OperationProgress progress)
    {
        OperationProgress?.Invoke(progress);
        if (progress.State != OperationState.Completed) return;

        DownloadOperation? op = FindOperation(progress.Id);
        if (op == null) return;
        ReleaseEntry? entry = Feed.Find(op.CanonicalVersion);
        if (entry == null)
        {
            HubLog.Warning($"No manifest for completed download {op.CanonicalVersion}; install deferred.");
            return;
        }

        // Install runs on the background queue; UI updates flow through Installations.Changed.
        _ = Task.Run(async () =>
        {
            var report = new Progress<string>(stage => HubLog.Info($"[install {op.DisplayVersion}] {stage}"));
            var (ok, errorKey, detail) = await Installer.InstallAsync(entry, op.TempPath, report, CancellationToken.None);
            if (ok)
            {
                Downloads.MarkInstalled(op.Id);
                try { if (File.Exists(op.TempPath)) File.Delete(op.TempPath); } catch { }
            }
            else
            {
                op.State = "failed";
                op.Error = $"{errorKey}: {detail}";
                HubLog.Warning($"Install failed for {op.DisplayVersion}: {errorKey} {detail}");
            }
        });
    }

    private DownloadOperation? FindOperation(string id)
    {
        foreach (DownloadOperation op in Downloads.Operations)
            if (op.Id == id) return op;
        return null;
    }

    public async Task<IReadOnlyList<ReleaseEntry>> GetInstallableAsync(bool forceRefresh, CancellationToken ct = default) =>
        await Feed.GetEntriesAsync(forceRefresh, ct);

    /// <summary>Enqueues download + auto-install for a release entry.</summary>
    public DownloadOperation StartInstall(ReleaseEntry entry) =>
        Downloads.Enqueue(entry.DownloadUrl, entry.Size, entry.Sha256, entry.CanonicalVersion, entry.DisplayVersion);

    /// <summary>Uninstall a hub-managed install; on success the registry updates itself.</summary>
    public (bool Ok, string ErrorKey) Uninstall(InstallationRecord record) => Installer.Uninstall(record);
}
