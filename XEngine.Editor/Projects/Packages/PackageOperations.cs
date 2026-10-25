// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

namespace XEngine.Editor.Projects.Packages;

public readonly record struct PackageOperationResult(bool Success, string Message)
{
    public static PackageOperationResult Ok(string message) => new(true, message);
    public static PackageOperationResult Fail(string message) => new(false, message);
}

/// <summary>Transactional manifest mutations used by the Package Manager UI.</summary>
public static class PackageOperations
{
    public static PackageOperationResult Install(Project project, string packageName, string specifier)
    {
        if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(specifier))
            return PackageOperationResult.Fail("Package name and version/source specifier are required.");

        return Mutate(project, manifest => manifest.Dependencies[packageName] = specifier,
            resolved => FindPackage(resolved, packageName) == null
                ? $"Package '{packageName}' was not present after resolution."
                : ValidateDependencyClosure(resolved),
            $"Installed {packageName}.", $"Package '{packageName}' could not be resolved from '{specifier}'.");
    }

    public static PackageOperationResult Remove(Project project, string packageName)
    {
        IReadOnlyList<ResolvedPackage> before = PackageManager.Resolve(project);
        ProjectPackageManifest manifest = ProjectPackageManifest.ReadOrDefault(
            Path.Combine(project.PackagesPath, "manifest.json"));
        List<string> blockers = BuildDependencyChains(before, manifest, packageName);
        if (blockers.Count != 0)
        {
            blockers.Sort(StringComparer.OrdinalIgnoreCase);
            return PackageOperationResult.Fail(
                $"Cannot remove '{packageName}'; required by {string.Join(", ", blockers)}.");
        }

        return Mutate(project, manifest => manifest.Dependencies.Remove(packageName),
            ValidateDependencyClosure, $"Removed {packageName} from direct dependencies.",
            $"Could not remove '{packageName}'.");
    }

    public static PackageOperationResult Update(Project project, string packageName, string version)
        => Install(project, packageName, version);

    private static PackageOperationResult Mutate(Project project, Action<ProjectPackageManifest> mutation,
        Func<IReadOnlyList<ResolvedPackage>, string?> validate, string successMessage, string failureMessage)
    {
        string manifestPath = Path.Combine(project.PackagesPath, "manifest.json");
        string lockPath = PackageLockFile.GetPath(project);
        byte[]? previousManifest = ReadIfPresent(manifestPath);
        byte[]? previousLock = ReadIfPresent(lockPath);

        try
        {
            ProjectPackageManifest manifest = ProjectPackageManifest.ReadOrDefault(manifestPath);
            mutation(manifest);
            manifest.Write(manifestPath);
            PackageManager.Invalidate();
            IReadOnlyList<ResolvedPackage> resolved = PackageManager.Resolve(project);
            string? validationError = validate(resolved);
            if (validationError == null)
            {
                // Upgrades/downgrades/uninstalls leave the previous version's cache behind; the
                // fresh lock is the survival contract, everything else under PackageCache goes.
                PackageCachePruner.Prune(project);
                return PackageOperationResult.Ok(successMessage);
            }
            failureMessage += " " + validationError;
        }
        catch (Exception ex)
        {
            Restore(manifestPath, previousManifest);
            Restore(lockPath, previousLock);
            PackageManager.Invalidate();
            return PackageOperationResult.Fail($"{failureMessage} {ex.Message}");
        }

        Restore(manifestPath, previousManifest);
        Restore(lockPath, previousLock);
        PackageManager.Invalidate();
        _ = PackageManager.Resolve(project);
        return PackageOperationResult.Fail(failureMessage);
    }

    /// <summary>
    /// Returns an error when two resolved packages declare a mutual exclusion via
    /// <see cref="ResolvedPackage.Conflicts"/>. Either side listing the other is enough.
    /// </summary>
    internal static string? FindPackageConflict(IReadOnlyList<ResolvedPackage> packages)
    {
        for (int i = 0; i < packages.Count; i++)
        {
            ResolvedPackage left = packages[i];
            for (int j = i + 1; j < packages.Count; j++)
            {
                ResolvedPackage right = packages[j];
                if (DeclaresConflict(left, right.Name) || DeclaresConflict(right, left.Name))
                    return $"'{left.Name}' conflicts with '{right.Name}'.";
            }
        }

        return null;
    }

    private static bool DeclaresConflict(ResolvedPackage package, string otherName)
    {
        List<string> conflicts = package.Conflicts;
        for (int i = 0; i < conflicts.Count; i++)
        {
            if (string.Equals(conflicts[i], otherName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? ValidateDependencyClosure(IReadOnlyList<ResolvedPackage> packages)
    {
        string? conflict = FindPackageConflict(packages);
        if (conflict != null)
            return conflict;

        var byName = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < packages.Count; i++)
            byName[packages[i].Name] = packages[i];

        for (int i = 0; i < packages.Count; i++)
        {
            ResolvedPackage package = packages[i];
            foreach (KeyValuePair<string, string> dependency in package.DependencySpecifiers)
            {
                if (!byName.TryGetValue(dependency.Key, out ResolvedPackage? resolvedDependency))
                    return $"'{package.Name}' requires missing package '{dependency.Key}'.";

                VersionRange? range = VersionRange.TryParse(dependency.Value);
                if (range != null
                    && SemanticVersion.TryParse(resolvedDependency.Version, out SemanticVersion resolvedVersion)
                    && !range.Value.Satisfies(resolvedVersion))
                {
                    return $"'{package.Name}' requires '{dependency.Key}' {dependency.Value}, "
                        + $"but {resolvedDependency.Version} was resolved.";
                }
            }
        }

        return null;
    }

    private static List<string> BuildDependencyChains(IReadOnlyList<ResolvedPackage> packages,
        ProjectPackageManifest manifest, string target)
    {
        var byName = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < packages.Count; i++)
            byName[packages[i].Name] = packages[i];

        var chains = new List<string>();
        var seenChains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in manifest.Dependencies.Keys)
        {
            if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase)
                || !byName.ContainsKey(root))
                continue;

            var path = new List<string> { root };
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
            FindDependencyChains(root, target, byName, path, visiting, chains, seenChains);
        }
        return chains;
    }

    private static void FindDependencyChains(string current, string target,
        IReadOnlyDictionary<string, ResolvedPackage> packages, List<string> path,
        HashSet<string> visiting, List<string> chains, HashSet<string> seenChains)
    {
        if (!packages.TryGetValue(current, out ResolvedPackage? package))
            return;
        foreach (string dependency in package.DependencySpecifiers.Keys)
        {
            path.Add(dependency);
            if (string.Equals(dependency, target, StringComparison.OrdinalIgnoreCase))
            {
                string chain = string.Join(" -> ", path);
                if (seenChains.Add(chain))
                    chains.Add(chain);
            }
            else if (visiting.Add(dependency))
            {
                FindDependencyChains(dependency, target, packages, path, visiting, chains, seenChains);
                visiting.Remove(dependency);
            }
            path.RemoveAt(path.Count - 1);
        }
    }

    private static ResolvedPackage? FindPackage(IReadOnlyList<ResolvedPackage> packages, string name)
    {
        for (int i = 0; i < packages.Count; i++)
        {
            if (string.Equals(packages[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return packages[i];
        }
        return null;
    }

    private static byte[]? ReadIfPresent(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private static void Restore(string path, byte[]? content)
    {
        if (content == null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        string temporaryPath = path + ".rollback";
        File.WriteAllBytes(temporaryPath, content);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
