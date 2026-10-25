// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using XEngine.Editor.Projects.Scripting;
using XEngine.PackageRegistry;

namespace XEngine.Editor.Projects.Packages;

/// <summary>
/// Reads <c>Packages/manifest.json</c>, project embed packages, and Editor-shipped packages, then
/// resolves each to concrete on-disk content for the existing compiler / plugin scanner / asset
/// database. The package system owns no compilation or indexing of its own — it only extends the
/// roots those subsystems walk.
///
/// <para>P0 scope: only <see cref="PackageSource.Embed"/> (packages living directly under
/// <c>Packages/&lt;name&gt;/</c>) and basic <c>file:</c> local references. Git/registry land in P1+.
/// A <c>Packages/&lt;dir&gt;/</c> containing a <c>package.json</c> is auto-discovered as embed even
/// when absent from the manifest, for backward compatibility with projects that drop packages in
/// directly.</para>
/// </summary>
public static class PackageManager
{
    private const string BuiltInPackagesDirectoryName = "Packages";

    /// <summary>Cache of the last successful resolution for the current project, or null if not yet
    /// resolved. Reset by <see cref="Invalidate"/>.</summary>
    private static List<ResolvedPackage>? s_cached;
    private static string? s_cachedRoot;
    private static string? s_cachedInputHash;

    // Tests inject an isolated Editor installation root. In a macOS app bundle, package source
    // belongs in Contents/Resources so codesign treats it as data rather than nested code.
    internal static string? BuiltInPackagesPathOverride { get; set; }

    internal static string BuiltInPackagesPath
        => Path.GetFullPath(BuiltInPackagesPathOverride
            ?? ResolveBuiltInPackagesPath(AppContext.BaseDirectory));

    internal static string ResolveBuiltInPackagesPath(string editorDirectory)
    {
        string full = Path.GetFullPath(editorDirectory);
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(full)) == "MacOS" &&
            Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full))) == "Contents")
        {
            string resources = Path.Combine(full, "..", "Resources", BuiltInPackagesDirectoryName);
            if (Directory.Exists(resources))
                return Path.GetFullPath(resources);
        }
        return Path.Combine(full, BuiltInPackagesDirectoryName);
    }

    /// <summary>Resolve every package declared by <paramref name="project"/>'s manifest (plus any
    /// auto-discovered embed packages). Idempotent within a project until the manifest changes on
    /// disk or <see cref="Invalidate"/> is called.</summary>
    public static IReadOnlyList<ResolvedPackage> Resolve(Project project)
    {
        var packagesPath = project.PackagesPath;
        var manifestPath = Path.Combine(packagesPath, "manifest.json");

        // Fast path: same project and a cheap input stamp. Do not walk package files here —
        // that used to hitch the UI thread on every Resolve (Project draw, ScanAssets, watchers).
        // Content edits invalidate through AssetWatcher / MarkPackageRootsDirty, not by rehashing.
        if (s_cached != null
            && s_cachedRoot != null
            && s_cachedRoot.Equals(project.RootPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s_cachedInputHash, ComputeCheapInputStamp(project, manifestPath), StringComparison.Ordinal))
        {
            return s_cached;
        }

        var result = ResolveUncached(project, manifestPath);
        s_cached = result;
        s_cachedRoot = project.RootPath;
        s_cachedInputHash = ComputeCheapInputStamp(project, manifestPath);
        return result;
    }

    /// <summary>
    /// Last successful resolution for <paramref name="project"/>, or null. UI draw and folder-index
    /// paths must use this instead of <see cref="Resolve"/> so a cache miss cannot walk package
    /// trees during <c>BeginGui</c> / watcher ticks.
    /// </summary>
    public static IReadOnlyList<ResolvedPackage>? PeekCached(Project project)
    {
        if (s_cached == null || s_cachedRoot == null) return null;
        if (!s_cachedRoot.Equals(project.RootPath, StringComparison.OrdinalIgnoreCase))
            return null;
        return s_cached;
    }

    /// <summary>Drop the cache so the next <see cref="Resolve"/> re-reads disk. Call after modifying
    /// the manifest or after a package is added/removed on disk.</summary>
    public static void Invalidate()
    {
        s_cached = null;
        s_cachedRoot = null;
        s_cachedInputHash = null;
    }

    /// <summary>Convenience: every resolved root path (non-null), for subsystems that just want the
    /// folders to walk.</summary>
    public static IReadOnlyList<string> ResolvedRoots(Project project)
    {
        var packages = Resolve(project);
        var roots = new List<string>(packages.Count);
        foreach (var p in packages) roots.Add(p.ResolvedRoot);
        return roots;
    }

    /// <summary>Resolve a stable <c>Packages/&lt;name&gt;/...</c> asset path to its physical package file.</summary>
    public static bool TryResolveLogicalAssetPath(Project project, string logicalPath, out string physicalPath)
    {
        var packages = Resolve(project);
        for (int i = 0; i < packages.Count; i++)
            if (packages[i].Mount.TryResolve(logicalPath, out physicalPath))
                return true;

        physicalPath = string.Empty;
        return false;
    }

    /// <summary>True when <paramref name="absolutePath"/> sits under an ignored package directory
    /// relative to <paramref name="packageRoot"/>: a folder whose name ends with "~" (Unity/npm
    /// "samples/archive" convention) or starts with "." (hidden). Such directories never produce
    /// assets, asmdefs, or scripts that should reach the project. Shared with AssemblyDefinitionDatabase
    /// and ScriptCompiler so all three subsystems apply the same skip rule.</summary>
    public static bool IsUnderIgnoredPackageDir(string absolutePath, string packageRoot)
    {
        var rel = Path.GetRelativePath(packageRoot, absolutePath);
        var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            if (seg.EndsWith("~", StringComparison.OrdinalIgnoreCase) || seg.StartsWith('.')) return true;
        }
        return false;
    }

    /// <summary>True when any path segment of <paramref name="absolutePath"/> is literally "Editor"
    /// (case-insensitive). Used to classify loose scripts (not owned by an asmdef) into the editor vs
    /// runtime default assembly, regardless of whether they live under Assets/ or a package root.</summary>
    public static bool HasEditorSegment(string absolutePath)
        => absolutePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s.Equals("Editor", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------
    //  Resolution
    // ------------------------------------------------------------------

    private static List<ResolvedPackage> ResolveUncached(Project project, string manifestPath)
    {
        var result = new List<ResolvedPackage>();
        var packagesPath = project.PackagesPath;

        var manifest = ProjectPackageManifest.ReadOrDefault(manifestPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Manifest-declared dependencies in package-name order. JSON object order is not a
        // dependency-resolution contract, so equivalent manifests produce the same closure.
        foreach (var pair in OrderedDependencies(manifest.Dependencies))
        {
            string name = pair.Key;
            string specifier = pair.Value;
            if (!seen.Add(name)) continue;
            var resolved = ResolveEntry(project, packagesPath, name, specifier);
            if (resolved != null) result.Add(resolved);
        }

        // 2. Auto-discover any embed package under Packages/ not already declared. This keeps the
        //    system friendly to projects that drop a package folder in without editing the manifest,
        //    matching the behavior documented in the spec ("default by directory name").
        DiscoverUndeclaredPackages(packagesPath, PackageSource.Embed, result, seen);

        // 3. Packages distributed with the Editor are available. Always-on packages
        // (installByDefault != false) auto-mount from the install directory. Official optional
        // packages are skipped here and only appear after a manifest pin copies them into
        // Library/PackageCache.
        DiscoverUndeclaredPackages(BuiltInPackagesPath, PackageSource.BuiltIn, result, seen);

        // 4. Transitive closure: each resolved package's package.json may declare dependencies on
        //    other packages. Pull in any not yet resolved by looking them up under Packages/ first
        //    (embed), then Library/PackageCache (git/registry clones). This is a P1-bounded closure:
        //    it brings transitively-required packages into the result so their asmdefs/plugins/assets
        //    participate in compilation and indexing. Version-conflict detection (full SemVer solve)
        //    is delegated to PackageDependencyResolver when there are multiple candidate versions;
        //    for P1 the common single-candidate case just picks the only available version.
        ResolveTransitiveDependencies(project, packagesPath, result, seen);

        var ordered = TopologicallyOrder(result);
        WriteLock(project, manifestPath, ordered);
        return ordered;
    }

    private static void DiscoverUndeclaredPackages(string packagesPath, PackageSource source,
        List<ResolvedPackage> result, HashSet<string> seen)
    {
        foreach (var dir in EnumeratePackageDirectories(packagesPath))
        {
            var dirName = Path.GetFileName(dir);
            var pj = PackageJson.Read(Path.Combine(dir, "package.json"));
            // Official optional packages ship with the Editor but stay unmounted until the project
            // installs them (manifest pin → copy into Library/PackageCache). Do not add them to
            // `seen` so a transitive demander can still pull them in.
            if (source == PackageSource.BuiltIn && pj != null && !pj.IsInstalledByDefault)
                continue;
            // A BuiltIn directory without a parsable package.json is NOT a package — it is
            // engine-local content (e.g. com.xengine.zonezero lives next to the shipped packages
            // in the dev layout). Mounting it made every project index and compile a game that
            // was never installed. Reserve the name so nothing else mounts it either.
            if (source == PackageSource.BuiltIn && pj == null)
            {
                seen.Add(dirName);
                continue;
            }
            // Track by directory name first (skip if a manifest entry already claimed it).
            if (!seen.Add(dirName)) continue;
            var pkgName = pj?.Name ?? dirName;
            // If package.json uses a canonical name different from its folder, reserve both names.
            // This also makes an earlier project package win over a same-named built-in package.
            if (!string.Equals(pkgName, dirName, StringComparison.OrdinalIgnoreCase)
                && !seen.Add(pkgName))
                continue;
            result.Add(BuildFromPackageJson(pj, dir, dirName, source));
        }
    }

    /// <summary>Pull in package.json-declared dependencies that aren't already resolved. Looks up
    /// each missing dependency name under Packages/ (embed) then Library/PackageCache (clones), and
    /// appends the first match. Stops when no new packages are added (fixpoint).</summary>
    private static void ResolveTransitiveDependencies(
        Project project, string packagesPath, List<ResolvedPackage> result, HashSet<string> seen)
    {
        bool changed = true;
        int guard = 0; // bounded fixpoint — a healthy graph terminates in a few rounds.
        while (changed && guard++ < 64)
        {
            changed = false;
            // Snapshot the current result so we iterate a stable list while appending.
            int count = result.Count;
            for (int i = 0; i < count; i++)
            {
                var pkg = result[i];
                if (pkg.PkgDependencies is not { Count: > 0 }) continue;
                foreach (var depName in pkg.PkgDependencies)
                {
                    if (!seen.Add(depName)) continue;

                    // Try embed under Packages/<depName>/ first.
                    var depRoot = Path.Combine(packagesPath, depName);
                    if (!Directory.Exists(depRoot))
                    {
                        // Built-in packages are a second first-party source, resolved before the
                        // project's mutable cache so an Editor distribution is self-contained.
                        depRoot = Path.Combine(BuiltInPackagesPath, depName);
                    }
                    if (!Directory.Exists(depRoot))
                    {
                        // Then PackageCache: any <depName>@*/ folder.
                        depRoot = FindCachedPackage(project, depName);
                    }
                    // P3: official com.xengine.* dependencies may live on the npm registry when no
                    // local copy exists. The registry's fast path reuses PackageCache, so this only
                    // touches the network the first time a dependency is pulled in.
                    string? registryLocation = null;
                    if (depRoot == null)
                    {
                        (depRoot, registryLocation) = ResolveRegistryDependency(project, pkg, depName);
                    }
                    if (depRoot == null || !Directory.Exists(depRoot))
                    {
                        Runtime.Debug.LogWarning(
                            $"[Packages] '{pkg.Name}' depends on '{depName}', but no such package was found under project Packages/, Editor built-in Packages/, Library/PackageCache/, or the registry. Transitive dependency skipped.");
                        continue;
                    }

                    var depPj = PackageJson.Read(Path.Combine(depRoot, "package.json"));
                    if (registryLocation != null)
                    {
                        // Registry-sourced dependencies mount read-only from PackageCache with the
                        // registry URL recorded for the lock, mirroring a direct registry pin.
                        result.Add(BuildFromPackageJson(depPj, depRoot, depName, PackageSource.Registry,
                            pkg.DependencySpecifiers.TryGetValue(depName, out string depRange) ? depRange : null,
                            registryLocation));
                    }
                    else
                    {
                        result.Add(BuildInferredPackage(project, depPj, depRoot, depName));
                    }
                    changed = true;
                }
            }
        }

        // Run the shared version solver over the complete graph after the source resolver has made
        // all local candidates visible. This preserves the legacy embed discovery behavior while
        // making version conflicts and selection deterministic for package-cache candidates.
        var solve = PackageDependencyResolver.Resolve(result,
            name => EnumeratePackageCandidates(project, packagesPath, result, name));
        foreach (var conflict in solve.Conflicts)
        {
            var demandParts = new List<string>(conflict.Demands.Count);
            for (int i = 0; i < conflict.Demands.Count; i++)
            {
                var demand = conflict.Demands[i];
                demandParts.Add($"{demand.demander} ({demand.range})");
            }
            string demands = string.Join(", ", demandParts);
            Runtime.Debug.LogError($"[Packages] Cannot resolve '{conflict.Package}': {conflict.Reason}. Demanded by {demands}.");
        }

        var byName = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < result.Count; i++) byName[result[i].Name] = result[i];
        foreach (var selected in solve.Selected)
        {
            var selectedValue = selected.Value;
            if (!byName.TryGetValue(selected.Key, out var current)
                || !current.ResolvedRoot.Equals(selectedValue.rootPath, StringComparison.OrdinalIgnoreCase))
            {
                var selectedPackage = BuildInferredPackage(project, selectedValue.packageJson,
                    selectedValue.rootPath, selected.Key);
                if (byName.ContainsKey(selected.Key))
                {
                    int index = result.FindIndex(p => p.Name.Equals(selected.Key, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) result[index] = selectedPackage;
                }
                else
                {
                    result.Add(selectedPackage);
                }
                byName[selected.Key] = selectedPackage;
            }
        }

        string? declaredConflict = PackageOperations.FindPackageConflict(result);
        if (declaredConflict != null)
            Runtime.Debug.LogError($"[Packages] {declaredConflict}");
    }

    private static IEnumerable<(SemanticVersion version, string rootPath, PackageJson packageJson)>
        EnumeratePackageCandidates(Project project, string packagesPath, List<ResolvedPackage> current, string name)
    {
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasProjectOverride = false;
        bool alreadyResolved = false;
        for (int i = 0; i < current.Count; i++)
        {
            var pkg = current[i];
            if (!pkg.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || !seenRoots.Add(pkg.ResolvedRoot)) continue;
            alreadyResolved = true;
            if (pkg.Source != PackageSource.BuiltIn) hasProjectOverride = true;
            if (!SemanticVersion.TryParse(pkg.Version, out var version)) continue;
            var pj = PackageJson.Read(Path.Combine(pkg.ResolvedRoot, "package.json"));
            if (pj != null) yield return (version, pkg.ResolvedRoot, pj);
        }

        string embedRoot = Path.Combine(packagesPath, name);
        if (Directory.Exists(embedRoot) && seenRoots.Add(embedRoot)
            && PackageJson.Read(Path.Combine(embedRoot, "package.json")) is { } embedPj
            && SemanticVersion.TryParse(embedPj.Version, out var embedVersion))
            yield return (embedVersion, embedRoot, embedPj);

        // A project-supplied package must remain an override even when the built-in package happens
        // to advertise a newer version. The dependency solver therefore sees built-ins only when no
        // project source has already supplied the requested name. Official optional packages that
        // are already installed keep their PackageCache pin until the user clicks Update.
        string builtInRoot = Path.Combine(BuiltInPackagesPath, name);
        if (!hasProjectOverride && Directory.Exists(builtInRoot) && seenRoots.Add(builtInRoot)
            && PackageJson.Read(Path.Combine(builtInRoot, "package.json")) is { } builtInPj
            && SemanticVersion.TryParse(builtInPj.Version, out var builtInVersion)
            && (builtInPj.IsInstalledByDefault || !alreadyResolved))
            yield return (builtInVersion, builtInRoot, builtInPj);

        string cacheRoot = Path.Combine(project.LibraryPath, "PackageCache");
        if (!Directory.Exists(cacheRoot)) yield break;
        string[] dirs;
        try { dirs = Directory.GetDirectories(cacheRoot, name + "@*", SearchOption.TopDirectoryOnly); }
        catch { yield break; }
        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < dirs.Length; i++)
        {
            string root = dirs[i];
            if (!seenRoots.Add(root)) continue;
            var pj = PackageJson.Read(Path.Combine(root, "package.json"));
            if (pj != null && SemanticVersion.TryParse(pj.Version, out var version))
                yield return (version, root, pj);
        }
    }

    /// <summary>Find the first <c>&lt;name&gt;@*</c> directory under Library/PackageCache/, or null.</summary>
    private static string? FindCachedPackage(Project project, string name)
    {
        var cacheRoot = Path.Combine(project.LibraryPath, "PackageCache");
        if (!Directory.Exists(cacheRoot)) return null;
        string? best = null;
        foreach (var dir in Directory.EnumerateDirectories(cacheRoot, name + "@*", SearchOption.TopDirectoryOnly))
        {
            if (File.Exists(Path.Combine(dir, "package.json")))
            {
                // Pick the highest version if multiple are cached; otherwise the first.
                best = dir;
                break;
            }
        }
        return best;
    }

    /// <summary>Resolve a single manifest entry. P0 handles embed + local file: references.</summary>
    private static ResolvedPackage? ResolveEntry(Project project, string packagesPath, string name, string specifier)
    {
        // Embed: explicit marker, or a version-only specifier that points at a Packages/<name>/ folder.
        var (source, rootPath, resolvedName, registryUrl) = ParseSpecifier(project, packagesPath, name, specifier);
        if (rootPath == null || !Directory.Exists(rootPath))
        {
            // A package explicitly named in a project manifest may be supplied editor-level: the
            // per-user module store (npm-installed from the launcher settings) wins over the
            // bundled copy so an updated store version is the one actually mounted. The store copy
            // only satisfies the pin when its version matches — otherwise the pin belongs to
            // another source and silently downgrading would be wrong.
            string? editorModuleRoot = TryResolveEditorModuleRoot(name, specifier);
            if (editorModuleRoot != null
                && PackageJson.Read(Path.Combine(editorModuleRoot, "package.json")) is { } modulePj)
                return BuildResolvedBuiltIn(project, modulePj, editorModuleRoot, name, specifier);

            // A package explicitly named in a project manifest may also be supplied by the Editor.
            // This keeps older manifests that pin XGUI by version working after the package becomes
            // built-in, while an existing project path always retains precedence above. The built-in
            // only satisfies the pin when its version matches — otherwise the pin belongs to a
            // registry version and silently downgrading to the bundled copy would be wrong.
            string builtInRoot = Path.Combine(BuiltInPackagesPath, name);
            if (Directory.Exists(builtInRoot)
                && BuiltinSatisfies(builtInRoot, specifier)
                && PackageJson.Read(Path.Combine(builtInRoot, "package.json")) is { } builtInPj)
                return BuildResolvedBuiltIn(project, builtInPj, builtInRoot, name, specifier);

            // A lock file is the offline recovery path for git/registry packages. If the source
            // endpoint is unavailable but the exact cache selected by the previous resolve remains,
            // use that immutable mount instead of silently dropping the package.
            var locked = TryResolveFromLock(project, name, source, specifier);
            if (locked != null) return locked;
            Runtime.Debug.LogError(
                $"[Packages] '{name}' ({specifier}) could not be resolved: path '{rootPath ?? "<null>"}' does not exist. Package skipped.");
            return null;
        }

        var pj = PackageJson.Read(Path.Combine(rootPath, "package.json"));
        return source switch
        {
            PackageSource.Embed => BuildEmbed(pj, rootPath, resolvedName, specifier),
            PackageSource.Local => BuildLocal(pj, rootPath, resolvedName, specifier),
            // Git and (future) Registry packages are read-only on disk, like Local — they live under
            // Library/PackageCache and are rebuilt by re-cloning, never edited in place.
            PackageSource.Git => BuildGit(pj, rootPath, resolvedName, specifier),
            PackageSource.BuiltIn => BuildResolvedBuiltIn(project, pj, rootPath, resolvedName, specifier),
            _ => BuildFromPackageJson(pj, rootPath, resolvedName, PackageSource.Registry, specifier, registryUrl),
        };
    }

    /// <summary>Map a manifest specifier to a (source, root, name, registry-url) tuple. P0 supports
    /// embed + local; P1 git; P2 file:// registries; P3 npm-protocol http(s) registries.</summary>
    private static (PackageSource source, string? root, string name, string? registryUrl) ParseSpecifier(
        Project project, string packagesPath, string name, string specifier)
    {
        var spec = specifier.Trim();

        if (spec.StartsWith("embed:", StringComparison.OrdinalIgnoreCase))
        {
            var embedName = spec["embed:".Length..].Trim();
            if (embedName.Length == 0) embedName = name;
            return (PackageSource.Embed, Path.Combine(packagesPath, embedName), embedName, null);
        }

        if (spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var rel = spec["file:".Length..].Trim();
            // file: references may be relative to the project root or to Packages/.
            var abs = Path.IsPathRooted(rel)
                ? rel
                : Path.GetFullPath(Path.Combine(project.RootPath, rel));
            return (PackageSource.Local, abs, name, null);
        }

        if (spec.StartsWith("git@", StringComparison.OrdinalIgnoreCase) || spec.StartsWith("git://", StringComparison.OrdinalIgnoreCase))
        {
            // P1: clone (or reuse a cached clone) into Library/PackageCache/<name>@<hash>/.
            var parsed = GitPackageSource.Parse(spec);
            if (parsed == null)
            {
                Runtime.Debug.LogError($"[Packages] Malformed git specifier for '{name}': '{spec}'.");
                return (PackageSource.Git, null, name, null);
            }
            var (url, @ref) = parsed.Value;
            var root = GitPackageSource.Resolve(project, name, url, @ref);
            return (PackageSource.Git, root, name, null);
        }

        // P2/P3: explicit registry: prefix, file:// location, or a bare registry URL.
        if (spec.StartsWith("registry:", StringComparison.OrdinalIgnoreCase)
            || spec.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || RegistryPackageSource.IsHttp(spec))
        {
            var parsed = RegistryPackageSource.Parse(spec);
            if (parsed == null)
            {
                Runtime.Debug.LogError($"[Packages] Malformed registry specifier for '{name}': '{spec}'.");
                return (PackageSource.Registry, null, name, null);
            }
            var (location, verSpec) = parsed.Value;
            // When the registry spec omits a version pin, fall back to the manifest's version-ish spec
            // (the manifest value itself, which for a registry entry is the version constraint).
            var versionConstraint = string.IsNullOrEmpty(verSpec) ? spec : verSpec;
            var root = RegistryPackageSource.Resolve(project, name, versionConstraint, location);
            return (PackageSource.Registry, root, name, location);
        }

        // Anything else (a version like "1.2.3" or "1.2.x") is treated as an embed pin first:
        // resolve to Packages/<name>/ if present, otherwise fall back to a default scoped registry
        // (the manifest's first scopedRegistry whose scopes include this package's name prefix).
        var embedRoot = Path.Combine(packagesPath, name);
        if (Directory.Exists(embedRoot))
            return (PackageSource.Embed, embedRoot, name, null);

        // An editor-level module (per-user store) supplies bare version pins before the bundled
        // copy — same satisfaction rule as the built-in check below.
        string? editorModuleRoot = TryResolveEditorModuleRoot(name, spec);
        if (editorModuleRoot != null)
            return (PackageSource.BuiltIn, editorModuleRoot, name, null);

        // A version pin for an Editor-shipped package (including official optional packages) resolves
        // against the install directory — but only when the bundled version actually satisfies the
        // pin, so a pin above the bundled version keeps falling through to the registry (P3 update
        // path). Optional packages are then copied into PackageCache.
        var builtInRoot = Path.Combine(BuiltInPackagesPath, name);
        if (Directory.Exists(builtInRoot) && BuiltinSatisfies(builtInRoot, spec))
            return (PackageSource.BuiltIn, builtInRoot, name, null);

        // P2: try a matching scoped registry (if the manifest declared one whose scopes cover this
        // package name) before giving up. P3: official com.xengine.* names additionally fall back to
        // the implicit official npm registry (configurable via XENGINE_NPM_REGISTRY; the offline
        // switch turns this off so resolution stays local-only).
        var scoped = FindScopedRegistry(project, name);
        if (scoped != null)
        {
            var root = RegistryPackageSource.Resolve(project, name, spec, scoped);
            return (PackageSource.Registry, root, name, scoped);
        }
        string? official = FindOfficialNpmRegistryUrl(project, name);
        if (official != null)
        {
            var root = RegistryPackageSource.Resolve(project, name, spec, official);
            return (PackageSource.Registry, root, name, official);
        }

        Runtime.Debug.LogWarning(
            $"[Packages] '{name}' = '{spec}' looks like a registry version, but no matching scoped registry was declared and the package isn't under Packages/. Skipped.");
        return (PackageSource.Registry, null, name, null);
    }

    /// <summary>Root of an editor-level module installed for this Editor version (per-user npm
    /// store), when the recorded version satisfies <paramref name="specifier"/> and the content is
    /// on disk. Same satisfaction semantics as the bundled-copy fallback so a pin above the stored
    /// version keeps falling through to registries.</summary>
    private static string? TryResolveEditorModuleRoot(string name, string specifier)
    {
        if (!EditorModules.EditorModuleStore.TryGetInstalled(name, out EditorModules.EditorModuleRecord? module))
            return null;
        string root = Path.Combine(EditorModules.EditorModuleStore.CacheRoot, $"{module.Name}@{module.Version}");
        if (!Directory.Exists(root)) return null;
        return BuiltinSatisfies(root, specifier) ? root : null;
    }

    /// <summary>True when the built-in package's version satisfies the pinned specifier. Specifiers
    /// that do not parse as a version range keep the legacy always-satisfied behavior.</summary>
    private static bool BuiltinSatisfies(string builtInRoot, string specifier)
    {
        var pj = PackageJson.Read(Path.Combine(builtInRoot, "package.json"));
        if (pj == null || !SemanticVersion.TryParse(pj.Version, out var version)) return true;
        var range = VersionRange.TryParse(specifier.Trim());
        return range == null || range.Value.Satisfies(version);
    }

    /// <summary>
    /// The implicit official npm registry for <c>com.xengine.*</c> names, or null when the name is
    /// third-party or npm access is disabled (<c>XENGINE_NPM_DISABLE</c>). The URL honors
    /// <c>XENGINE_NPM_REGISTRY</c> so tests and offline machines can redirect or suppress it.
    /// </summary>
    private static string? FindOfficialNpmRegistryUrl(Project project, string name)
    {
        if (!NpmPackageName.IsInternalName(name)) return null;
        NpmRegistryOptions options = NpmRegistryOptions.Load(project.RootPath);
        return options.Disabled ? null : options.RegistryUrl;
    }

    /// <summary>Find the first scoped registry location (from the manifest) whose scopes include a
    /// prefix of <paramref name="packageName"/>, or null when none match. P2: scopes are matched as
    /// reverse-domain prefix substrings (e.g. scope "com.myteam" matches "com.myteam.tools").</summary>
    private static string? FindScopedRegistry(Project project, string packageName)
    {
        string manifestPath = Path.Combine(project.PackagesPath, "manifest.json");
        if (!File.Exists(manifestPath)) return null;
        var manifest = ProjectPackageManifest.ReadOrDefault(manifestPath);
        if (manifest.ScopedRegistries == null) return null;
        foreach (var (nickname, reg) in manifest.ScopedRegistries)
        {
            if (string.IsNullOrEmpty(reg.Url)) continue;
            foreach (var scope in reg.Scopes)
            {
                if (!string.IsNullOrEmpty(scope) && packageName.StartsWith(scope, StringComparison.OrdinalIgnoreCase))
                    return reg.Url;
            }
        }
        return null;
    }

    /// <summary>
    /// Resolve a transitive dependency from the registry: a manifest scoped registry matching the
    /// dependency name first, then the implicit official npm registry for <c>com.xengine.*</c>.
    /// Returns the PackageCache root plus the registry URL used, or (null, null) when no registry
    /// applies or the attempt failed (already logged).
    /// </summary>
    private static (string? root, string? registryUrl) ResolveRegistryDependency(
        Project project, ResolvedPackage demander, string depName)
    {
        string location = FindScopedRegistry(project, depName) ?? FindOfficialNpmRegistryUrl(project, depName) ?? "";
        if (location.Length == 0) return (null, null);
        string range = demander.DependencySpecifiers.TryGetValue(depName, out string depRange) ? depRange : "*";
        string? root = RegistryPackageSource.Resolve(project, depName, range, location);
        return (root, root == null ? null : location);
    }

    private static ResolvedPackage BuildEmbed(PackageJson? pj, string root, string fallbackName,
        string? specifier = null)
        => BuildFromPackageJson(pj, root, fallbackName, PackageSource.Embed, specifier);

    private static ResolvedPackage BuildLocal(PackageJson? pj, string root, string fallbackName, string specifier)
    {
        var pkg = BuildFromPackageJson(pj, root, fallbackName, PackageSource.Local, specifier);
        if (pj == null)
            pkg.Warnings.Add($"No package.json found at '{root}'; using directory name '{fallbackName}' from manifest entry '{specifier}'.");
        return pkg;
    }

    private static ResolvedPackage BuildGit(PackageJson? pj, string root, string fallbackName, string specifier)
    {
        var pkg = BuildFromPackageJson(pj, root, fallbackName, PackageSource.Git, specifier);
        if (pj == null)
            pkg.Warnings.Add($"No package.json found at git clone '{root}'; using '{fallbackName}' from manifest entry '{specifier}'.");
        return pkg;
    }

    private static ResolvedPackage BuildResolvedBuiltIn(Project project, PackageJson? pj, string root,
        string fallbackName, string? specifier)
    {
        if (pj != null && !pj.IsInstalledByDefault)
        {
            string? cacheRoot = BuiltInPackageSource.Materialize(
                project, string.IsNullOrWhiteSpace(pj.Name) ? fallbackName : pj.Name,
                specifier ?? pj.Version, root);
            if (!string.IsNullOrEmpty(cacheRoot))
            {
                root = cacheRoot;
                pj = PackageJson.Read(Path.Combine(root, "package.json")) ?? pj;
            }
        }
        return BuildFromPackageJson(pj, root, fallbackName, PackageSource.BuiltIn, specifier);
    }

    private static ResolvedPackage BuildFromPackageJson(PackageJson? pj, string root, string fallbackName,
        PackageSource source, string? sourceSpecifier = null, string? knownRegistryUrl = null)
    {
        var name = string.IsNullOrWhiteSpace(pj?.Name) ? fallbackName : pj!.Name;
        var version = string.IsNullOrWhiteSpace(pj?.Version) ? "0.0.0" : pj!.Version;
        var display = string.IsNullOrWhiteSpace(pj?.DisplayName) ? name : pj!.DisplayName!;
        string physicalRoot = Path.GetFullPath(root);

        string? gitUrl = null;
        string? gitRef = null;
        string? gitCommit = null;
        if (source == PackageSource.Git && GitPackageSource.Parse(sourceSpecifier ?? string.Empty) is { } git)
        {
            gitUrl = git.url;
            gitRef = git.@ref;
            GitPackageSource.TryGetCommitHash(physicalRoot, "HEAD", out gitCommit!);
        }

        string? registryUrl = knownRegistryUrl;
        if (registryUrl == null
            && source == PackageSource.Registry
            && RegistryPackageSource.Parse(sourceSpecifier ?? string.Empty) is { } registry)
            registryUrl = registry.registryLocation;

        var dependencySpecifiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (pj?.Dependencies is { Count: > 0 })
            foreach (var pair in pj.Dependencies)
                dependencySpecifiers[pair.Key] = pair.Value;

        var pkg = new ResolvedPackage
        {
            Name = name,
            Version = version,
            Mount = new PackageMount
            {
                Name = name,
                LogicalRoot = "Packages/" + name,
                PhysicalRoot = physicalRoot,
                Source = source,
                ReadOnly = source != PackageSource.Embed,
                ContentHash = ComputeContentStamp(physicalRoot),
            },
            DisplayName = display,
            SourceSpecifier = sourceSpecifier,
            GitUrl = gitUrl,
            GitRef = gitRef,
            GitCommit = string.IsNullOrWhiteSpace(gitCommit) ? null : gitCommit,
            RegistryUrl = registryUrl,
            DependencySpecifiers = dependencySpecifiers,
            Conflicts = pj?.Conflicts is { Count: > 0 } ? new List<string>(pj.Conflicts) : [],
        };

        // Asset roots: the package root itself, plus first-level non-~ subfolders. We expose the root
        // and let the asset database skip ~ directories while walking, matching Unity's behavior.
        pkg.AssetRoots.Add(root);

        // Asmdefs anywhere under the root.
        foreach (var asmdef in Directory.EnumerateFiles(root, "*.asmdef", SearchOption.AllDirectories))
            pkg.AsmdefFiles.Add(asmdef);

        // Plugins: reuse PluginScanner's classification but constrained to this package's root.
        pkg.Plugins.AddRange(ScanPackagePlugins(root));

        // Supported platforms (validated against BuildPlatforms.All; unknown names warned + ignored).
        if (pj?.SupportedPlatforms is { Count: > 0 })
        {
            foreach (var plat in pj.SupportedPlatforms)
            {
                var canonical = BuildPlatforms.Normalize(plat);
                if (canonical != null) pkg.SupportedPlatforms.Add(canonical);
                else pkg.Warnings.Add($"Unknown platform '{plat}' in supportedPlatforms; ignored.");
            }
        }

        // Per-package dependencies (names only — used by the transitive-closure resolver). The
        // associated version specifiers are recorded but P1 does not run a full multi-version solve;
        // it pulls each missing dependency in by name from Packages/ or PackageCache.
        if (pj?.Dependencies is { Count: > 0 })
        {
            foreach (var depName in pj.Dependencies.Keys)
                pkg.PkgDependencies.Add(depName);
        }

        return pkg;
    }

    private static ResolvedPackage BuildInferredPackage(Project project, PackageJson? pj, string root, string fallbackName)
    {
        PackageSource source = IsUnder(BuiltInPackagesPath, root)
            ? PackageSource.BuiltIn
            : IsUnder(project.PackagesPath, root) ? PackageSource.Embed : PackageSource.Local;
        string packageCacheRoot = Path.Combine(project.LibraryPath, "PackageCache");
        if (source == PackageSource.Local
            && IsUnder(packageCacheRoot, root)
            && Directory.Exists(Path.Combine(BuiltInPackagesPath, fallbackName)))
            source = PackageSource.BuiltIn;
        if (source == PackageSource.BuiltIn && pj != null && !pj.IsInstalledByDefault
            && !IsUnder(packageCacheRoot, root))
        {
            string? cacheRoot = BuiltInPackageSource.Materialize(
                project, string.IsNullOrWhiteSpace(pj.Name) ? fallbackName : pj.Name,
                pj.Version, root);
            if (!string.IsNullOrEmpty(cacheRoot))
            {
                root = cacheRoot;
                pj = PackageJson.Read(Path.Combine(root, "package.json")) ?? pj;
            }
        }
        var lockFile = PackageLockFile.Read(project);
        if (lockFile != null)
        {
            string fullRoot = Path.GetFullPath(root);
            foreach (var entry in lockFile.Packages)
            {
                if (string.IsNullOrWhiteSpace(entry.ResolvedPath)) continue;
                string candidate = Path.GetFullPath(Path.Combine(project.RootPath,
                    entry.ResolvedPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!candidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (Enum.TryParse<PackageSource>(entry.Source, true, out var lockedSource)) source = lockedSource;
                return BuildFromPackageJson(pj, root, fallbackName, source, entry.SourceSpecifier);
            }
        }
        return BuildFromPackageJson(pj, root, fallbackName, source);
    }

    private static ResolvedPackage? TryResolveFromLock(Project project, string name,
        PackageSource source, string sourceSpecifier)
    {        var lockFile = PackageLockFile.Read(project);
        if (lockFile == null) return null;
        for (int i = 0; i < lockFile.Packages.Count; i++)
        {
            var entry = lockFile.Packages[i];
            if (!entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

            string? root = null;
            if (!string.IsNullOrWhiteSpace(entry.ResolvedPath))
                root = Path.GetFullPath(Path.Combine(project.RootPath,
                    entry.ResolvedPath!.Replace('/', Path.DirectorySeparatorChar)));
            if (root == null && source == PackageSource.Embed)
                root = Path.Combine(project.PackagesPath, name);
            if (root == null || !Directory.Exists(root)) continue;

            var pj = PackageJson.Read(Path.Combine(root, "package.json"));
            if (pj == null) continue;
            // The lock is the offline recovery path for the SAME selection: its version must still
            // satisfy the current manifest pin. A pin the registry cannot satisfy (typo'd version,
            // unpublished build) must fail loudly instead of silently succeeding at the old version.
            if (!SemanticVersion.TryParse(pj.Version, out SemanticVersion lockedVersion)) continue;
            if (!LockedVersionSatisfies(lockedVersion, sourceSpecifier)) continue;
            PackageSource lockedSource = source;
            if (Enum.TryParse<PackageSource>(entry.Source, true, out var parsedSource)) lockedSource = parsedSource;
            var recovered = BuildFromPackageJson(pj, root, name, lockedSource,
                entry.SourceSpecifier ?? sourceSpecifier);
            // A bare version pin routed through a scoped/official registry loses its URL when the
            // specifier alone no longer says so — restore it from the lock so the entry stays stable.
            if (lockedSource == PackageSource.Registry && string.IsNullOrEmpty(recovered.RegistryUrl)
                && !string.IsNullOrWhiteSpace(entry.RegistryUrl))
                recovered.RegistryUrl = entry.RegistryUrl;
            return recovered;
        }
        return null;
    }

    /// <summary>A lock entry may only recover a manifest pin it still satisfies: bare version pins
    /// and registry <c>#ver</c> pins parse exactly; other specifier forms stay Any (historical
    /// behavior for embed/local/git recovery).</summary>
    private static bool LockedVersionSatisfies(SemanticVersion lockedVersion, string? sourceSpecifier)
    {
        if (string.IsNullOrWhiteSpace(sourceSpecifier)) return true;
        string specifier = sourceSpecifier.Trim();
        if (VersionRange.TryParse(specifier) is { } range) return range.Satisfies(lockedVersion);
        if (RegistryPackageSource.Parse(specifier) is { } registry
            && !string.IsNullOrEmpty(registry.version)
            && VersionRange.TryParse(registry.version) is { } registryRange)
            return registryRange.Satisfies(lockedVersion);
        return true;
    }

    private static IEnumerable<KeyValuePair<string, string>> OrderedDependencies(
        Dictionary<string, string> dependencies)
    {
        var keys = new List<string>(dependencies.Keys);
        keys.Sort(static (a, b) =>
        {
            int c = StringComparer.OrdinalIgnoreCase.Compare(a, b);
            return c != 0 ? c : StringComparer.Ordinal.Compare(a, b);
        });
        for (int i = 0; i < keys.Count; i++)
            yield return new KeyValuePair<string, string>(keys[i], dependencies[keys[i]]);
    }

    private static List<ResolvedPackage> TopologicallyOrder(List<ResolvedPackage> packages)
    {
        var byName = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < packages.Count; i++) byName[packages[i].Name] = packages[i];
        var names = new List<string>(byName.Keys);
        names.Sort(static (a, b) =>
        {
            int c = StringComparer.OrdinalIgnoreCase.Compare(a, b);
            return c != 0 ? c : StringComparer.Ordinal.Compare(a, b);
        });

        var state = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ResolvedPackage>(packages.Count);
        void Visit(string name)
        {
            if (state.TryGetValue(name, out byte value))
            {
                if (value == 2) return;
                if (value == 1) return; // cycle is diagnosed by the dependency resolver
            }
            state[name] = 1;
            if (byName.TryGetValue(name, out var pkg))
            {
                var deps = new List<string>(pkg.PkgDependencies);
                deps.Sort(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < deps.Count; i++)
                    if (byName.ContainsKey(deps[i])) Visit(deps[i]);
                ordered.Add(pkg);
            }
            state[name] = 2;
        }
        for (int i = 0; i < names.Count; i++) Visit(names[i]);
        return ordered;
    }

    private static void WriteLock(Project project, string manifestPath, List<ResolvedPackage> packages)
    {
        var lockFile = new PackageLockFile
        {
            ManifestHash = PackageLockFile.ComputeManifestHash(manifestPath),
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
        };
        var packageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < packages.Count; i++) packageNames.Add(packages[i].Name);
        for (int i = 0; i < packages.Count; i++)
        {
            var pkg = packages[i];
            var entry = new PackageLockEntry
            {
                Name = pkg.Name,
                Version = pkg.Version,
                Source = pkg.Source.ToString().ToLowerInvariant(),
                SourceSpecifier = pkg.SourceSpecifier,
                LogicalRoot = pkg.LogicalRoot,
                ResolvedPath = PackageLockFile.ToProjectRelativePath(project, pkg.ResolvedRoot),
                ContentHash = pkg.ContentHash,
                GitUrl = pkg.GitUrl,
                GitRef = pkg.GitRef,
                GitCommit = pkg.GitCommit,
                RegistryUrl = pkg.RegistryUrl,
                Dependencies = new Dictionary<string, string>(pkg.DependencySpecifiers,
                    StringComparer.OrdinalIgnoreCase),
            };
            var closure = new List<string>();
            CollectDependencyClosure(pkg, packages, packageNames, closure,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            entry.DependencyClosure = closure;
            lockFile.Packages.Add(entry);
        }
        try
        {
            string lockPath = PackageLockFile.GetPath(project);
            var existing = PackageLockFile.Read(lockPath);
            if (existing != null && PackageLockFile.ContentEquals(existing, lockFile)) return;
            lockFile.Write(lockPath);
        }
        catch (IOException ex)
        {
            Runtime.Debug.LogWarning($"[Packages] Could not write lock file: {ex.Message}");
        }
    }

    private static void CollectDependencyClosure(ResolvedPackage package, List<ResolvedPackage> packages,
        HashSet<string> packageNames, List<string> closure, HashSet<string> visiting)
    {
        if (!visiting.Add(package.Name)) return;
        var deps = new List<string>(package.PkgDependencies);
        deps.Sort(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < deps.Count; i++)
        {
            string dep = deps[i];
            if (!packageNames.Contains(dep)) continue;
            bool alreadyAdded = false;
            for (int k = 0; k < closure.Count; k++)
            {
                if (closure[k].Equals(dep, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyAdded = true;
                    break;
                }
            }
            if (!alreadyAdded) closure.Add(dep);
            for (int j = 0; j < packages.Count; j++)
                if (packages[j].Name.Equals(dep, StringComparison.OrdinalIgnoreCase))
                    CollectDependencyClosure(packages[j], packages, packageNames, closure, visiting);
        }
    }

    private static bool IsUnder(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = Path.GetFullPath(path);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Manifest / lock metadata plus top-level package folder names. No recursive walk and no
    /// file-byte hashing — those belong on Invalidate (watcher) + <see cref="ResolveUncached"/>.
    /// </summary>
    internal static string ComputeCheapInputStamp(Project project, string manifestPath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInputFileMeta(hash, "manifest", manifestPath);
        AppendInputFileMeta(hash, "lock", PackageLockFile.GetPath(project));
        AppendTopLevelPackageNames(hash, "packages", project.PackagesPath);
        AppendTopLevelPackageNames(hash, "builtin", BuiltInPackagesPath);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendInputFileMeta(IncrementalHash hash, string key, string path)
    {
        AppendText(hash, key);
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                hash.AppendData(BitConverter.GetBytes(info.LastWriteTimeUtc.Ticks));
                hash.AppendData(BitConverter.GetBytes(info.Length));
                return;
            }
        }
        catch (IOException) { }
        AppendText(hash, "<missing>");
    }

    private static void AppendTopLevelPackageNames(IncrementalHash hash, string key, string packagesPath)
    {
        AppendText(hash, key);
        if (!Directory.Exists(packagesPath))
        {
            AppendText(hash, "<missing>");
            return;
        }

        var names = new List<string>();
        foreach (string dir in EnumeratePackageDirectories(packagesPath))
            names.Add(Path.GetFileName(dir));
        names.Sort(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            AppendText(hash, names[i]);
    }

    private static void AppendText(IncrementalHash hash, string value)
        => hash.AppendData(Encoding.UTF8.GetBytes(value + "\0"));

    /// <summary>
    /// Content fingerprint: normalized relative paths and file bytes, independent of timestamps.
    /// Used only inside
    /// <see cref="ResolveUncached"/> — never on the Resolve fast path.
    /// </summary>
    private static string ComputeContentStamp(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] separator = [0];
        var files = new List<string>();
        WalkPackageFiles(root, files.Add);
        for (int i = 0; i < files.Count; i++)
            files[i] = Path.GetRelativePath(root, files[i]).Replace('\\', '/');
        files.Sort(StringComparer.Ordinal);
        byte[] buffer = new byte[64 * 1024];
        for (int i = 0; i < files.Count; i++)
        {
            string relative = files[i];
            string file = Path.Combine(root, relative);
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData(separator);
            try
            {
                using var stream = File.OpenRead(file);
                using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int read;
                while ((read = stream.Read(buffer)) != 0)
                    fileHash.AppendData(buffer, 0, read);
                hash.AppendData(fileHash.GetHashAndReset());
            }
            catch (IOException)
            {
                AppendText(hash, "<unreadable>");
            }
            hash.AppendData(separator);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Visit package files, skipping <c>~</c> / hidden directories (same rule as
    /// <see cref="IsUnderIgnoredPackageDir"/>) without a GetFullPath per file.</summary>
    private static void WalkPackageFiles(string root, Action<string> onFile)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            if (!dir.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                ReadOnlySpan<char> name = Path.GetFileName(dir.AsSpan());
                if ((!name.IsEmpty && name[0] == '.') || (name.Length > 0 && name[^1] == '~'))
                    continue;
            }

            try
            {
                string[] files = Directory.GetFiles(dir);
                for (int i = 0; i < files.Length; i++)
                    onFile(files[i]);
                string[] children = Directory.GetDirectories(dir);
                for (int i = 0; i < children.Length; i++)
                    stack.Push(children[i]);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Enumerate top-level directories under <paramref name="packagesPath"/> that look like
    /// package roots (contain a package.json). Used for embed auto-discovery.</summary>
    private static IEnumerable<string> EnumeratePackageDirectories(string packagesPath)
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(packagesPath); }
        catch { yield break; }

        Array.Sort(dirs, static (left, right) =>
        {
            int c = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            return c != 0 ? c : StringComparer.Ordinal.Compare(left, right);
        });

        foreach (var dir in dirs)
        {
            // Skip ~ -suffixed directories and hidden folders.
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || name.EndsWith("~", StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.Combine(dir, "package.json")))
                yield return dir;
        }
    }

    /// <summary>Scan a single package root for plugins, reusing PluginScanner's file classification.
    /// Mirrors PluginScanner.ScanAll but without the project Assets/ requirement, and restricted to
    /// the package's own subtree so two packages don't pick up each other's binaries.</summary>
    private static List<Scripting.PluginInfo> ScanPackagePlugins(string packageRoot)
    {
        var result = new List<Scripting.PluginInfo>();
        string[] files;
        try { files = Directory.GetFiles(packageRoot, "*.*", SearchOption.AllDirectories); }
        catch { return result; }

        foreach (var file in files)
        {
            string ext = Path.GetExtension(file);
            if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".so", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase))
                continue;

            string relative = Path.GetRelativePath(packageRoot, file);
            string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool underPlugins = false;
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].Equals("Plugins", StringComparison.OrdinalIgnoreCase)) { underPlugins = true; break; }
            }
            if (!underPlugins) continue;

            bool underEditor = false;
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].Equals("Editor", StringComparison.OrdinalIgnoreCase)) { underEditor = true; break; }
            }

            bool isNative = !ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                            || !Scripting.PluginScanner.IsManagedAssembly(file);

            // Defaults mirror PluginInfo.Build; .meta override also mirrors it. We read the .meta so
            // a package author's platform/CPU settings are honored exactly like project-level plugins.
            bool editorOnly = underEditor;
            bool autoReferenced = true;
            bool anyPlatform = true;
            var platforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string cpu = "x64";

            string metaPath = MetaFile.GetMetaPath(file);
            if (File.Exists(metaPath))
            {
                try
                {
                    var settings = MetaFile.Read(metaPath).Settings;
                    if (settings != null)
                    {
                        if (settings.TryGet(Scripting.PluginInfo.Keys.EditorOnly, out var eo)) editorOnly = underEditor || eo.BoolValue;
                        if (settings.TryGet(Scripting.PluginInfo.Keys.AutoReferenced, out var arf)) autoReferenced = arf.BoolValue;
                        if (settings.TryGet(Scripting.PluginInfo.Keys.AnyPlatform, out var ap)) anyPlatform = ap.BoolValue;
                        if (settings.TryGet(Scripting.PluginInfo.Keys.Windows, out var w) && w.BoolValue) platforms.Add(BuildPlatforms.Windows);
                        if (settings.TryGet(Scripting.PluginInfo.Keys.Linux, out var l) && l.BoolValue) platforms.Add(BuildPlatforms.Linux);
                        if (settings.TryGet(Scripting.PluginInfo.Keys.MacOS, out var m) && m.BoolValue) platforms.Add(BuildPlatforms.MacOS);
                        if (settings.TryGet(Scripting.PluginInfo.Keys.Android, out var a) && a.BoolValue) platforms.Add(BuildPlatforms.Android);
                        if (settings.TryGet(Scripting.PluginInfo.Keys.HarmonyOS, out var h) && h.BoolValue) platforms.Add(BuildPlatforms.HarmonyOS);
                        if (settings.TryGet(Scripting.PluginInfo.Keys.Cpu, out var c) && !string.IsNullOrWhiteSpace(c.StringValue)) cpu = c.StringValue;
                    }
                }
                catch { /* fall back to defaults */ }
            }

            result.Add(new Scripting.PluginInfo
            {
                AbsolutePath = file,
                IsNative = isNative,
                EditorOnly = editorOnly,
                AutoReferenced = autoReferenced,
                AnyPlatform = anyPlatform,
                Platforms = platforms,
                Cpu = cpu,
            });
        }

        return result;
    }
}
