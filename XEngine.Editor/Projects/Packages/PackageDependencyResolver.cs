// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Editor.Projects.Packages;

/// <summary>
/// Resolves the transitive dependency closure for a set of package roots. Given the manifest's
/// top-level packages (each with a resolved on-disk root) plus each package's <c>package.json</c>
/// <c>dependencies</c>, this walks the graph, picks a single version per package (the highest that
/// satisfies every declared lower-bound), detects conflicts, and returns the final resolved set.
///
/// <para>P1 strategy: a package can only appear once in the closure (one version). Every reference
/// contributes a lower-bound (<see cref="VersionRange"/>); the resolver picks the highest available
/// candidate version that satisfies all of them. When candidates don't overlap, the conflict is
/// reported with the chain of demanders so the user can fix the manifest.</para>
/// </summary>
public static class PackageDependencyResolver
{
    /// <summary>
    /// Compute the resolved closure. <paramref name="roots"/> are the top-level packages already
    /// resolved to disk (by the source resolver). <paramref name="candidates"/> is a lookup from
    /// package name to the set of on-disk versions available locally (embed folder, PackageCache
    /// subdirs, etc.) — the resolver picks among these. Returns the closure plus any conflicts.
    /// </summary>
    public static Result Resolve(
        IReadOnlyList<ResolvedPackage> roots,
        Func<string, IEnumerable<(SemanticVersion version, string rootPath, PackageJson packageJson)>> candidates)
    {
        var selected = new Dictionary<string, (SemanticVersion version, string rootPath, PackageJson pj)>(StringComparer.OrdinalIgnoreCase);
        var demanders = new Dictionary<string, List<(string demander, VersionRange range)>>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<Conflict>();
        var queue = new Queue<(string name, VersionRange range, string demander)>();

        // Seed: each root demands itself at its manifest pin (a bare version like "0.1.1" becomes
        // an exact range, so a stray newer PackageCache entry cannot evict the pinned selection;
        // roots without a parsable pin — embed/local/git/auto-mounted built-ins — stay Any).
        // Transitive deps the roots declare still get their explicit ranges below.
        foreach (var root in roots)
        {
            VersionRange rootRange = ParseRootRange(root);
            queue.Enqueue((root.Name, rootRange, $"manifest:{root.Name}"));
            RegisterDemand(demanders, root.Name, $"manifest:{root.Name}", rootRange);
        }

        while (queue.Count > 0)
        {
            var (name, range, demander) = queue.Dequeue();
            RegisterDemand(demanders, name, demander, range);

            // Find the highest available candidate satisfying all accumulated demands for this name.
            var accumulated = demanders[name];
            var best = (SemanticVersion?)null;
            string? bestRoot = null;
            PackageJson? bestPj = null;
            foreach (var (ver, rootPath, pj) in candidates(name))
            {
                if (!SatisfiesAll(accumulated, ver)) continue;
                if (best == null || ver > best)
                {
                    best = ver; bestRoot = rootPath; bestPj = pj;
                }
            }

            if (best == null)
            {
                // No candidate satisfies all demands — but if we already selected a version, that
                // selection wins (the new demand is unsatisfiable but doesn't evict an existing pick).
                if (!selected.ContainsKey(name))
                {
                    conflicts.Add(new Conflict(name, accumulated, "no candidate version satisfies all demands"));
                }
                continue;
            }

            // If the selected version changed, enqueue the package's own dependencies.
            if (!selected.TryGetValue(name, out var current) || current.version != best.Value)
            {
                selected[name] = (best.Value, bestRoot!, bestPj!);
                if (bestPj!.Dependencies is { Count: > 0 })
                {
                    foreach (var (depName, depSpec) in bestPj.Dependencies)
                    {
                        var depRange = VersionRange.TryParse(depSpec) ?? VersionRange.Any;
                        queue.Enqueue((depName, depRange, name));
                    }
                }
            }
        }

        return new Result(selected, demanders, conflicts);
    }

    /// <summary>The root's manifest pin as a range: a bare version ("0.1.1") or a
    /// <c>registry:…#ver</c> pin parse exactly; other specifier forms (embed/file/git) and
    /// auto-mounted built-ins keep the historical Any.</summary>
    private static VersionRange ParseRootRange(ResolvedPackage root)
    {
        if (string.IsNullOrWhiteSpace(root.SourceSpecifier)) return VersionRange.Any;
        string specifier = root.SourceSpecifier.Trim();
        if (VersionRange.TryParse(specifier) is { } range) return range;
        if (RegistryPackageSource.Parse(specifier) is { } registry
            && !string.IsNullOrEmpty(registry.version)
            && VersionRange.TryParse(registry.version) is { } registryRange)
            return registryRange;
        return VersionRange.Any;
    }

    private static void RegisterDemand(
        Dictionary<string, List<(string, VersionRange)>> demanders,
        string name, string demander, VersionRange range)
    {
        if (!demanders.TryGetValue(name, out var list)) demanders[name] = list = new();
        list.Add((demander, range));
    }

    private static bool SatisfiesAll(List<(string demander, VersionRange range)> demands, SemanticVersion version)
    {
        foreach (var (_, range) in demands)
            if (!range.Satisfies(version)) return false;
        return true;
    }

    /// <summary>The resolved closure: package name -> (selected version, root path, package.json).</summary>
    public sealed record Result(
        IReadOnlyDictionary<string, (SemanticVersion version, string rootPath, PackageJson packageJson)> Selected,
        IReadOnlyDictionary<string, List<(string demander, VersionRange range)>> Demanders,
        IReadOnlyList<Conflict> Conflicts);

    public sealed record Conflict(
        string Package,
        IReadOnlyList<(string demander, VersionRange range)> Demands,
        string Reason);
}
