// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

namespace XEngine.Editor.Projects.Packages;

/// <summary>
/// Deletes <c>Library/PackageCache/&lt;name&gt;@&lt;ver&gt;</c> directories the current lock no longer
/// references — the leftovers of upgrades, downgrades, and uninstalls. Git clones and built-in
/// materializations live in the same cache and are protected the same way: anything recorded in
/// <c>packages-lock.json</c> stays, everything else goes. Best-effort: locked files log a warning and
/// are retried once; the next operation prunes them.
/// </summary>
public static class PackageCachePruner
{
    /// <summary>Prune the project's package cache against the current lock. Returns how many
    /// directories were removed.</summary>
    public static int Prune(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        string cacheRoot = Path.Combine(project.LibraryPath, "PackageCache");
        if (!Directory.Exists(cacheRoot)) return 0;

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PackageLockFile? lockFile = PackageLockFile.Read(project);
        if (lockFile != null)
        {
            foreach (PackageLockEntry entry in lockFile.Packages)
            {
                if (string.IsNullOrWhiteSpace(entry.ResolvedPath)) continue;
                string full = Path.GetFullPath(Path.Combine(project.RootPath,
                    entry.ResolvedPath.Replace('/', Path.DirectorySeparatorChar)));
                referenced.Add(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }

        int removed = 0;
        foreach (string dir in Directory.EnumerateDirectories(cacheRoot))
        {
            string name = Path.GetFileName(dir);
            // Interrupted npm extractions land in .staging-* siblings; they are never referenced.
            if (name.StartsWith(".staging-", StringComparison.OrdinalIgnoreCase))
            {
                if (TryDelete(dir)) removed++;
                continue;
            }

            string trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (referenced.Contains(trimmed)) continue;
            // Only prune versioned cache entries (name@version); an unexpected directory shape is
            // left alone rather than guessed at.
            int at = name.IndexOf('@');
            if (at <= 0 || !SemanticVersion.TryParse(name[(at + 1)..], out _)) continue;
            if (File.Exists(Path.Combine(dir, "package.json")) && TryDelete(dir)) removed++;
        }
        return removed;
    }

    private static bool TryDelete(string dir)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                Runtime.Debug.Log($"[Packages] Pruned unreferenced package cache '{Path.GetFileName(dir)}'.");
                return true;
            }
            catch (IOException) when (attempt == 0)
            {
                // Watchers/compilers can hold a handle briefly; retry once before giving up.
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogWarning($"[Packages] Could not prune package cache '{dir}': {ex.Message}");
                return false;
            }
        }
        Runtime.Debug.LogWarning($"[Packages] Could not prune package cache '{dir}' (still locked).");
        return false;
    }
}
