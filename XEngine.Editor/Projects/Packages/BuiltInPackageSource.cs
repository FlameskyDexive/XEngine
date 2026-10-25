// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

namespace XEngine.Editor.Projects.Packages;

/// <summary>
/// Materializes an Editor-shipped optional package into
/// <c>Library/PackageCache/&lt;name&gt;@&lt;ver&gt;/</c> so the rest of the pipeline treats it like a
/// registry/git clone: a stable, project-local, read-only root. Always-on built-in packages
/// (<see cref="PackageJson.IsInstalledByDefault"/>) keep mounting from the Editor install directory
/// and never go through this path.
/// </summary>
public static class BuiltInPackageSource
{
    /// <summary>
    /// Copy <paramref name="builtInRoot"/> into the project package cache when the Editor-shipped
    /// version satisfies <paramref name="versionConstraint"/>. If a matching cache folder already
    /// exists it is reused. Returns the cache path, or null when no version satisfies the constraint.
    /// </summary>
    public static string? Materialize(Project project, string packageName, string versionConstraint,
        string builtInRoot)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(builtInRoot))
            return null;
        if (!Directory.Exists(builtInRoot) || !File.Exists(Path.Combine(builtInRoot, "package.json")))
        {
            Runtime.Debug.LogError($"[Packages] Built-in package '{packageName}' was not found at '{builtInRoot}'.");
            return null;
        }

        PackageJson? metadata = PackageJson.Read(Path.Combine(builtInRoot, "package.json"));
        if (metadata == null || !SemanticVersion.TryParse(metadata.Version, out SemanticVersion builtInVersion))
        {
            Runtime.Debug.LogError($"[Packages] Built-in package '{packageName}' has no usable version.");
            return null;
        }

        VersionRange range = VersionRange.TryParse(versionConstraint) ?? VersionRange.Any;
        string cacheRoot = Path.Combine(project.LibraryPath, "PackageCache");
        Directory.CreateDirectory(cacheRoot);

        if (range.Satisfies(builtInVersion))
            return CopyToCache(builtInRoot, cacheRoot, packageName, metadata.Version);

        string? cached = FindCachedVersion(cacheRoot, packageName, range);
        if (cached != null)
            return cached;

        Runtime.Debug.LogError(
            $"[Packages] Editor-shipped '{packageName}' {metadata.Version} does not satisfy '{versionConstraint}', and no matching cache folder exists.");
        return null;
    }

    private static string CopyToCache(string source, string cacheRoot, string packageName, string version)
    {
        string dest = Path.Combine(cacheRoot, packageName + "@" + version);
        if (Directory.Exists(dest) && File.Exists(Path.Combine(dest, "package.json")))
            return dest;

        CopyDirectory(source, dest);
        return dest;
    }

    private static string? FindCachedVersion(string cacheRoot, string packageName, VersionRange range)
    {
        if (!Directory.Exists(cacheRoot)) return null;
        string[] dirs;
        try { dirs = Directory.GetDirectories(cacheRoot, packageName + "@*", SearchOption.TopDirectoryOnly); }
        catch { return null; }

        string? best = null;
        SemanticVersion? bestVersion = null;
        for (int i = 0; i < dirs.Length; i++)
        {
            PackageJson? pj = PackageJson.Read(Path.Combine(dirs[i], "package.json"));
            if (pj == null || !SemanticVersion.TryParse(pj.Version, out SemanticVersion version))
                continue;
            if (!range.Satisfies(version)) continue;
            if (bestVersion == null || version > bestVersion)
            {
                bestVersion = version;
                best = dirs[i];
            }
        }
        return best;
    }

    internal static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        for (int i = 0; i < files.Length; i++)
        {
            string relative = Path.GetRelativePath(source, files[i]);
            string target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(files[i], target, overwrite: true);
        }
    }
}
