// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root and details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Editor.Projects.Scripting;

namespace XEngine.Editor.Projects.Packages;

/// <summary>Where a package was resolved from. Built-in packages ship with the Editor installation.</summary>
public enum PackageSource { Embed, Local, Git, Registry, BuiltIn }

/// <summary>
/// A package that <see cref="PackageManager.Resolve(XEngine.Editor.Projects.Project)"/> successfully
/// turned into concrete on-disk content. Each instance is a thin bundle of paths that the existing
/// <c>AssemblyDefinitionDatabase</c> / <c>PluginScanner</c> / <c>EditorAssetBackend</c> consume —
/// the package system itself owns no compilation or indexing logic.
/// </summary>
public sealed class ResolvedPackage
{
    /// <summary>Reverse-domain id from <see cref="PackageJson.Name"/>.</summary>
    public required string Name { get; init; }

    /// <summary>Resolved SemVer string (e.g. <c>1.2.3</c>); <c>"0.0.0"</c> when the package.json omitted it.</summary>
    public required string Version { get; init; }

    /// <summary>Stable mount that maps this package's logical asset path to its physical source root.</summary>
    public required PackageMount Mount { get; init; }

    public PackageSource Source => Mount.Source;

    /// <summary>Absolute on-disk root. Embed = project Packages/&lt;name&gt;; BuiltIn = Editor Packages/&lt;name&gt;.</summary>
    public string ResolvedRoot => Mount.PhysicalRoot;

    /// <summary>Stable virtual root, always <c>Packages/&lt;package-name&gt;</c>.</summary>
    public string LogicalRoot => Mount.LogicalRoot;

    /// <summary>Deterministic content hash recorded in the package lock file.</summary>
    public string ContentHash => Mount.ContentHash;

    /// <summary>The exact manifest/package source expression that produced this mount, when known.</summary>
    public string? SourceSpecifier { get; init; }

    public string? GitUrl { get; init; }
    public string? GitRef { get; init; }
    public string? GitCommit { get; init; }

    /// <summary>Settable (unlike the git fields) because lock-file recovery restores this after the
    /// package is built — a bare version pin routed through a scoped/official registry cannot carry
    /// the URL in its specifier alone.</summary>
    public string? RegistryUrl { get; set; }

    /// <summary>Human-readable display name (falls back to <see cref="Name"/>).</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Asset roots to feed the asset database. By convention the whole package root is one
    /// asset root, minus <c>~</c>-suffixed directories (Unity/npm convention for "ignore").</summary>
    public List<string> AssetRoots { get; init; } = new();

    /// <summary>Absolute paths to every <c>.asmdef</c> discovered under the package root.</summary>
    public List<string> AsmdefFiles { get; init; } = new();

    /// <summary>Plugins discovered under this package, resolved via the same PluginInfo model as
    /// project-level plugins (so the existing ScriptAssemblyManager wiring handles them unchanged).</summary>
    public List<PluginInfo> Plugins { get; init; } = new();

    /// <summary>Canonical platform names from <see cref="BuildPlatforms"/> that this package supports.
    /// Empty means "all platforms".</summary>
    public HashSet<string> SupportedPlatforms { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names of packages this package depends on (from its package.json dependencies keys).
    /// Used by the transitive-closure resolver to pull in packages that the manifest didn't list
    /// directly but a resolved package requires.</summary>
    public List<string> PkgDependencies { get; init; } = new();

    /// <summary>Dependency name to the exact package.json range used during resolution.</summary>
    public Dictionary<string, string> DependencySpecifiers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Package names this package cannot share a project with (from package.json <c>conflicts</c>).</summary>
    public List<string> Conflicts { get; init; } = new();

    /// <summary>Non-fatal resolution warnings (e.g. unsupported platform on current target, missing
    /// optional subfolders). Surfaced to the UI but do not block the package.</summary>
    public List<string> Warnings { get; init; } = new();

    public bool SupportedOn(string platform)
        => SupportedPlatforms.Count == 0
           || SupportedPlatforms.Contains(platform)
           || platform.Equals(BuildPlatforms.Editor, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="absolutePath"/> lives under this package's resolved root.</summary>
    public bool Contains(string absolutePath)
        => absolutePath.StartsWith(ResolvedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
           || absolutePath.Equals(ResolvedRoot, StringComparison.OrdinalIgnoreCase);
}
