using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Platform;

namespace XHub.Services;

/// <summary>
/// Install commit and uninstall for hub-managed editor payloads (§5):
/// 下载 → SHA-256 校验（DownloadService）→ 临时解压（越界防护）→ 检查布局与能力
/// （--hub-info ≥ minHubProtocol）→ 提交版本目录 → 登记安装。磁盘空间在解压前检查；
/// 安装串行执行（§3.5）。
/// </summary>
public sealed class InstallService
{
    private static readonly SemaphoreSlim InstallGate = new(1, 1); // serial installs

    private readonly InstallationLibrary _installations;
    private readonly Func<InstallationRecord, bool>? _isInUse;

    /// <summary><paramref name="isInUse"/> lets the caller veto uninstalling a live install.</summary>
    public InstallService(InstallationLibrary installations, Func<InstallationRecord, bool>? isInUse = null)
    {
        _installations = installations;
        _isInUse = isInUse;
    }

    public async Task<(bool Ok, string ErrorKey, string Detail)> InstallAsync(
        ReleaseEntry entry, string packagePath, IProgress<string>? progress, CancellationToken ct)
    {
        await InstallGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(AppPaths.EditorsRoot);

            // Disk space (§5): package + extracted payload + working margin.
            long packageSize = new FileInfo(packagePath).Length;
            DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(AppPaths.EditorsRoot))!);
            if (drive.AvailableFreeSpace < packageSize * 3)
                return (false, "install.error.diskSpace", $"{drive.AvailableFreeSpace / (1024L * 1024)} MB free");

            string staging = Path.Combine(AppPaths.EditorsRoot, $".staging-{Guid.NewGuid():N}");
            string commitDir = Path.Combine(AppPaths.EditorsRoot, $"{entry.CanonicalVersion}-{Guid.NewGuid().ToString("N")[..8]}");
            int hubProtocolVersion = 0;
            try
            {
                progress?.Report("extract");
                ExtractSafe(packagePath, staging);

                progress?.Report("layout");
                string editorExe = Path.Combine(staging, entry.EditorPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(editorExe))
                    return (false, "install.error.layout", entry.EditorPath);

                progress?.Report("capability");
                HubProtocol.HubInfo? info = HubProtocol.ProbeEditor(editorExe);
                if (info == null)
                    return (false, "install.error.capability", "no --hub-info response");
                if (info.ProtocolVersion < entry.MinHubProtocol)
                    return (false, "install.error.protocolTooOld", $"{info.ProtocolVersion} < {entry.MinHubProtocol}");
                // The capability probe doubles as the hub-protocol stamp for the new record.
                hubProtocolVersion = info.ProtocolVersion;

                progress?.Report("commit");
                Directory.Move(staging, commitDir);

                var record = new InstallationRecord
                {
                    Path = commitDir,
                    EditorExePath = Path.Combine(commitDir, entry.EditorPath.Replace('/', Path.DirectorySeparatorChar)),
                    VersionDisplay = entry.DisplayVersion,
                    CanonicalVersion = entry.CanonicalVersion,
                    Channel = entry.Channel,
                    Architecture = ReleaseFeedService.CurrentRid,
                    Source = "hub",
                    ManagedByHub = true,
                    HubProtocolVersion = hubProtocolVersion,
                };
                _installations.AddRecord(record);
                HubLog.Info($"Installed {entry.DisplayVersion} → {commitDir}");
                return (true, "", commitDir);
            }
            catch (OperationCanceledException)
            {
                TryDelete(commitDir);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(commitDir);
                return (false, "install.error.failed", ex.Message);
            }
            finally
            {
                TryDelete(staging);
            }
        }
        finally
        {
            InstallGate.Release();
        }
    }

    /// <summary>
    /// Zip extraction with directory-traversal protection (§5 解压目录越界防护): every entry
    /// must resolve inside the target root.
    /// </summary>
    internal static void ExtractSafe(string packagePath, string targetRoot)
    {
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        string root = Path.GetFullPath(targetRoot) + Path.DirectorySeparatorChar;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string destination = Path.GetFullPath(Path.Combine(targetRoot, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(destination, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"zip entry escapes target: {entry.FullName}");
            }
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith(Path.DirectorySeparatorChar))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>
    /// Uninstalls a hub-managed installation: refuses non-hub records, live sessions, and any
    /// path outside the editors root (§5 安装归属目录校验 / 正在使用版本禁止卸载).
    /// </summary>
    public (bool Ok, string ErrorKey) Uninstall(InstallationRecord record)
    {
        if (!record.ManagedByHub)
            return (false, "uninstall.error.notHubManaged");
        if (_isInUse?.Invoke(record) == true)
            return (false, "uninstall.error.inUse");

        string fullPath = Path.GetFullPath(record.Path);
        if (!AppPaths.IsUnderEditorsRoot(fullPath))
            return (false, "uninstall.error.outsideRoot");

        try
        {
            Directory.Delete(fullPath, recursive: true);
        }
        catch (Exception ex)
        {
            HubLog.Error("Uninstall delete failed.", ex);
            return (false, "uninstall.error.deleteFailed");
        }
        _installations.Remove(record.Id);
        HubLog.Info($"Uninstalled {record.VersionDisplay} ({record.Path})");
        return (true, "");
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch { /* best effort */ }
    }
}
