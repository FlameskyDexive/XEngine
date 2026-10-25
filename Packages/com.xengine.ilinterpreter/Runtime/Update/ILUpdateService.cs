using System;
using System.IO;
using System.Security.Cryptography;

namespace XEngine.ILInterpreter.Update;

/// <summary>Result of an update attempt (plan §6 acceptance states).</summary>
public enum ILUpdateStatus
{
    Success,
    InvalidSignature,
    VersionRegression,
    HashMismatch,
    BindingsMissing,
    DiskFull,
    Busy,
}

/// <summary>
/// A/B content-slot update service (plan §6).
///
/// Directory layout under the player content root:
///   ILInterpreter/Modules/   — active slot (baseline or promoted update)
///   ILInterpreter/.updates/  — incoming slot staging (download target)
///   ILInterpreter/.state/    — atomic state files: version, last-known-good marker, update lock
///
/// Safety rules:
///   • Incoming sets are staged fully, verified (signature, hashes, bindings) and then
///     swapped into the active slot in one rename — a crash mid-update keeps either the
///     old or the new complete set, never a partial one.
///   • Activation is version-monotonic: lower versions are refused (downgrade attacks).
///   • Domain-level failures after activation roll back to the last known good set.
///   • A lock file refuses concurrent updates (Busy).
/// </summary>
public sealed class ILUpdateService
{
    public required string ContentRoot { get; init; }
    /// <summary>Public key XML baked into the player (editor keeps the private key).</summary>
    public required string PublicKeyXml { get; init; }
    /// <summary>Binding IDs the running host can provide (activation refuses sets
    /// whose required IDs are not all present).</summary>
    public required System.Collections.Generic.ISet<string> HostBindingIds { get; init; }

    private string UpdatesDir => Path.Combine(ContentRoot, ".updates");
    private string StateDir => Path.Combine(ContentRoot, ".state");
    private string LockPath => Path.Combine(StateDir, "update.lock");
    private string VersionPath => Path.Combine(StateDir, "version");
    private string GoodMarkerPath => Path.Combine(StateDir, "last-known-good");
    private string ActiveSlotPath => Path.Combine(StateDir, "active-slot");
    private string BaselineModules => Path.Combine(ContentRoot, "Modules");

    /// <summary>Directory of the ACTIVE module set: the slot the active-slot pointer
    /// names, or the bundled baseline when no update has been promoted.</summary>
    public string ResolveActiveModulesDir()
    {
        string slot = File.Exists(ActiveSlotPath)
            ? File.ReadAllText(ActiveSlotPath).Trim()
            : "";
        return slot.Length > 0 && Directory.Exists(Path.Combine(UpdatesDir, slot))
            ? Path.Combine(UpdatesDir, slot)
            : BaselineModules;
    }

    public long ActiveVersion => File.Exists(VersionPath)
        ? long.Parse(File.ReadAllText(VersionPath).Trim())
        : 0;

    private static void WriteAtomic(string path, string contents)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs))
        {
            w.Write(contents);
            w.Flush();
            fs.Flush(true); // data hits the disk before the rename
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file);
            string dest = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    /// <summary>
    /// Stage + verify + atomically activate an incoming update set.
    /// <paramref name="stagingDir"/> holds the downloaded module files and update.json.
    /// </summary>
    public ILUpdateStatus ApplyUpdate(string stagingDir, Action<string>? log = null)
    {
        // Concurrency: a lock file refuses overlapping updates.
        Directory.CreateDirectory(StateDir);
        try
        {
            using (new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        }
        catch (IOException)
        {
            return ILUpdateStatus.Busy;
        }

        try
        {
            string manifestPath = Path.Combine(stagingDir, "update.json");
            if (!File.Exists(manifestPath))
                return Fail(log, "update.json missing", ILUpdateStatus.HashMismatch);

            var manifest = ILUpdateManifest.FromJson(File.ReadAllText(manifestPath));

            // 1. Signature (before anything else — untrusted bytes stop here).
            if (!ILUpdateSigner.Verify(manifest, PublicKeyXml))
                return Fail(log, "signature verification failed", ILUpdateStatus.InvalidSignature);

            // 2. Version monotonicity (downgrade protection). Re-signing an old
            //    version does not help: the active version only ever increases.
            if (manifest.Version <= ActiveVersion)
                return Fail(log, $"version {manifest.Version} <= active {ActiveVersion}",
                    ILUpdateStatus.VersionRegression);

            // 3. Hash pinning of every module file.
            foreach (var (name, sha) in manifest.Modules)
            {
                string file = Path.Combine(stagingDir, name);
                if (!File.Exists(file))
                    return Fail(log, $"module '{name}' missing from update set", ILUpdateStatus.HashMismatch);
                string actual = ILUpdateManifest.ComputeModuleSha256(File.ReadAllBytes(file));
                if (!string.Equals(actual, sha, StringComparison.Ordinal))
                    return Fail(log, $"module '{name}' hash mismatch", ILUpdateStatus.HashMismatch);
            }

            // 4. Binding coverage: the host must already provide every required ID.
            foreach (var id in manifest.RequiredBindingIds)
            {
                if (!HostBindingIds.Contains(id))
                    return Fail(log, $"host does not provide binding {id}", ILUpdateStatus.BindingsMissing);
            }

            // 5. Stage the complete set, then swap atomically.
            string stagingSlot = Path.Combine(UpdatesDir, "incoming");
            if (Directory.Exists(stagingSlot)) Directory.Delete(stagingSlot, true);
            Directory.CreateDirectory(stagingSlot);
            foreach (var name in manifest.Modules.Keys)
            {
                string dest = Path.Combine(stagingSlot, name);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(Path.Combine(stagingDir, name), dest, overwrite: true);
            }

            // Preserve the current set as last-known-good before promoting.
            if (ActiveVersion > 0)
            {
                string current = ResolveActiveModulesDir();
                string good = Path.Combine(UpdatesDir, "last-known-good");
                if (Directory.Exists(good)) Directory.Delete(good, true);
                CopyDirectory(current, good);
                string currentSlot = Path.GetFileName(current); // 槽名相对 .updates
                WriteAtomic(GoodMarkerPath, ActiveVersion + Environment.NewLine + currentSlot);
            }

            // A/B slot promotion: the verified set becomes .updates/slot-N and the
            // atomic pointer file decides which slot is active. No in-place
            // directory replacement — a crash leaves the pointer (and thus the
            // player) on one complete set.
            string slotName = "slot-" + manifest.Version;
            string slotDir = Path.Combine(UpdatesDir, slotName);
            if (Directory.Exists(slotDir)) Directory.Delete(slotDir, true);
            CopyDirectory(stagingSlot, slotDir);
            Directory.Delete(stagingSlot, true);
            WriteAtomic(ActiveSlotPath, slotName);

            WriteAtomic(VersionPath, manifest.Version.ToString());
            log?.Invoke($"update v{manifest.Version} activated ({manifest.Modules.Count} modules)");
            return ILUpdateStatus.Success;
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            RollbackToLastKnownGood();
            return Fail(log, "disk full during update", ILUpdateStatus.DiskFull);
        }
        finally
        {
            File.Delete(LockPath);
        }
    }

    /// <summary>
    /// Called when the promoted set fails its startup health check: restores the
    /// last known good slot (or the bundled baseline when none exists).
    /// </summary>
    public bool RollbackToLastKnownGood()
    {
        if (!File.Exists(GoodMarkerPath))
        {
            // No promoted update to roll back from: the bundled baseline IS the
            // last known good — clear any promoted pointer.
            if (File.Exists(ActiveSlotPath)) File.Delete(ActiveSlotPath);
            return false;
        }

        var lines = File.ReadAllLines(GoodMarkerPath);
        if (lines.Length < 2) return false;
        WriteAtomic(VersionPath, lines[0].Trim());
        WriteAtomic(ActiveSlotPath, lines[1].Trim()); // 槽名相对 .updates
        return true;
    }

    private static bool IsDiskFull(IOException ex)
    {
        const int ERROR_DISK_FULL = 0x70;
        const int ERROR_HANDLE_DISK_FULL = 0x27;
        int code = ex.HResult & 0xFFFF;
        return code is ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL;
    }

    private static ILUpdateStatus Fail(Action<string>? log, string message, ILUpdateStatus status)
    {
        log?.Invoke($"update refused: {message}");
        return status;
    }
}
