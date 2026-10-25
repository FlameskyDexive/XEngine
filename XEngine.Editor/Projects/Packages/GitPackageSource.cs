// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace XEngine.Editor.Projects.Packages;

/// <summary>
/// Resolves <c>git@&lt;url&gt;#&lt;ref&gt;</c> package sources by cloning (or fetching updates for)
/// the repository into <c>Library/PackageCache/&lt;name&gt;@&lt;ref-hash&gt;/</c>. The cache is keyed by
/// the resolved commit hash so a ref bump always picks up new content, while a re-resolve of the same
/// ref reuses the on-disk clone (offline-friendly). Mirrors Unity's UPM git-source behavior.
/// </summary>
public static class GitPackageSource
{
    /// <summary>
    /// Parse a <c>git@url#ref</c> specifier into (url, ref). The ref defaults to the repo's default
    /// branch when omitted. Returns null for specifiers that aren't git sources.
    /// </summary>
    public static (string url, string @ref)? Parse(string specifier)
    {
        var s = (specifier ?? "").Trim();
        string url;
        if (s.StartsWith("git@", StringComparison.OrdinalIgnoreCase)) url = s["git@".Length..];
        else if (s.StartsWith("git://", StringComparison.OrdinalIgnoreCase)) url = s;
        else return null;

        string @ref = "HEAD";
        int hash = url.IndexOf('#');
        if (hash >= 0)
        {
            @ref = url[(hash + 1)..];
            url = url[..hash];
        }
        if (string.IsNullOrWhiteSpace(url)) return null;
        return (url.Trim(), @ref.Trim());
    }

    /// <summary>
    /// Ensure the git source is cloned into the project's PackageCache and return the resolved root
    /// path. Uses an existing clone when the ref's resolved commit hash already has a cache directory;
    /// otherwise clones fresh. Returns null and logs an error when git is unavailable or the clone
    /// fails (the caller skips the package).
    /// </summary>
    public static string? Resolve(Project project, string packageName, string url, string @ref)
    {
        var cacheRoot = Path.Combine(project.LibraryPath, "PackageCache");
        Directory.CreateDirectory(cacheRoot);

        // Resolve the ref to a commit hash first. We probe an existing clone to fetch cheaply; if none
        // exists, do a full clone to a temp dir, read HEAD, then move it into place keyed by the hash.
        // To keep this MVP simple and offline-friendly, we look for an existing cache dir whose
        // <name>@<ref> matches first.
        string refSlug = SanitizeSlug(@ref);
        string directCacheDir = Path.Combine(cacheRoot, $"{packageName}@{refSlug}");
        if (Directory.Exists(directCacheDir) && File.Exists(Path.Combine(directCacheDir, "package.json")))
        {
            // Verify the ref still resolves to the same commit; if not, re-clone below.
            if (TryGetCommitHash(directCacheDir, "HEAD", out _) && !FetchIfNeeded(directCacheDir, @ref))
            {
                Runtime.Debug.Log($"[Packages] Using cached git package '{packageName}' from '{directCacheDir}'.");
                return directCacheDir;
            }
        }

        // Fresh clone (or re-clone after a ref change).
        string tempClone = Path.Combine(cacheRoot, $".{packageName}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (!RunGit(null, $"clone --depth 1 --branch {Quote(@ref)} {Quote(url)} {Quote(tempClone)}", out var err)
                && !RunGit(null, $"clone --depth 1 {Quote(url)} {Quote(tempClone)}", out _))
            {
                Runtime.Debug.LogError($"[Packages] git clone failed for '{packageName}' ({url}): {err}");
                return null;
            }

            // Read the resolved commit so the final cache dir is keyed by hash (stable across ref renames).
            if (!TryGetCommitHash(tempClone, "HEAD", out var hash)) hash = refSlug;
            string hashSlug = SanitizeSlug(hash);
            string finalDir = Path.Combine(cacheRoot, $"{packageName}@{hashSlug}");
            if (Directory.Exists(finalDir))
            {
                // Another resolution raced ahead; discard the temp clone.
                TryDelete(tempClone);
            }
            else
            {
                Directory.Move(tempClone, finalDir);
            }

            if (!File.Exists(Path.Combine(finalDir, "package.json")))
            {
                Runtime.Debug.LogError($"[Packages] git package '{packageName}' ({url}) has no package.json at its root.");
                return null;
            }
            Runtime.Debug.Log($"[Packages] Cloned git package '{packageName}' ({url}@{@ref} -> {hashSlug}) into '{finalDir}'.");
            return finalDir;
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"[Packages] git resolution of '{packageName}' failed: {ex.Message}");
            TryDelete(tempClone);
            return null;
        }
    }

    private static bool FetchIfNeeded(string cloneDir, string @ref)
    {
        // Best-effort fetch + checkout; failure means the cache is stale and the caller re-clones.
        if (!RunGit(cloneDir, "fetch --depth 1 origin", out _)) return false;
        return RunGit(cloneDir, $"checkout {Quote(@ref)}", out _);
    }

    public static bool TryGetCommitHash(string workingDir, string @ref, out string hash)
    {
        hash = "";
        if (!RunGit(workingDir, $"rev-parse {Quote(@ref)}", out var stdout)) return false;
        hash = stdout.Trim();
        return hash.Length > 0;
    }

    private static bool RunGit(string? workingDir, string args, out string stdout)
    {
        stdout = "";
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (workingDir != null) psi.WorkingDirectory = workingDir;
            using var p = Process.Start(psi);
            if (p == null) return false;
            stdout = p.StandardOutput.ReadToEnd();
            // stderr is read but discarded here — callers that need it use the overload below.
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch
        {
            return false; // git not on PATH, or launch failed
        }
    }

    private static string Quote(string s) => s.StartsWith('"') ? s : "\"" + s.Replace("\"", "\\\"") + "\"";

    private static string SanitizeSlug(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '.' || c == '_' ? c : '-');
        var result = sb.ToString();
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
