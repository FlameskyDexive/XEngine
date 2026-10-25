// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Projects.Packages.EditorModules;

/// <summary>An installable editor-level module as presented by catalog listings.</summary>
public sealed class EditorModuleInfo
{
    /// <summary>Engine package name (<c>com.xengine.platform.android</c>).</summary>
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Category grouping for the settings UI ("Platforms" today).</summary>
    public string Category { get; init; } = "";
    /// <summary>Toolchain platform id ("android" / "harmonyos" / "ios") when the module drives a
    /// platform toolchain; the Build-side maps this to its own kind enum.</summary>
    public string? PlatformId { get; init; }
}

/// <summary>
/// Catalog of editor-level modules. The known platform modules are hardcoded so the launcher
/// settings page renders instantly and works fully offline; the registry is only consulted to
/// enrich entries with their latest published version (update checks). This is deliberately
/// separate from the project Package Manager catalog: editor modules are properties of the Editor
/// installation, not of any project.
/// </summary>
public static class EditorModuleCatalog
{
    /// <summary>The platform build-support modules (com.xengine.platform.*), keyed for the launcher
    /// settings page. Display strings mirror the packages' own manifests.</summary>
    public static IReadOnlyList<EditorModuleInfo> KnownPlatformModules { get; } = new[]
    {
        new EditorModuleInfo
        {
            Name = "com.xengine.platform.android",
            DisplayName = "Android Build Support",
            Description = "JDK 17, Android cmdline-tools, SDK packages (platform-tools, build-tools, "
                + "platform, NDK) and bundletool, downloaded from vendor sources into the managed SDK root.",
            Category = "Platforms",
            PlatformId = "android",
        },
        new EditorModuleInfo
        {
            Name = "com.xengine.platform.harmonyos",
            DisplayName = "HarmonyOS Build Support",
            Description = "OpenHarmony SDK (native + toolchains incl. hdc) from the Huawei Cloud mirror "
                + "plus the XEngine SDK Bundle, imported into the managed SDK root.",
            Category = "Platforms",
            PlatformId = "harmonyos",
        },
        new EditorModuleInfo
        {
            Name = "com.xengine.platform.ios",
            DisplayName = "iOS Build Support",
            Description = "Xcode locator and signing preflight. Apple licensing forbids redistributing "
                + "Xcode, so this module downloads nothing — it locates, validates, and guides.",
            Category = "Platforms",
            PlatformId = "ios",
        },
    };

    /// <summary>Look up a known module entry by engine package name.</summary>
    public static EditorModuleInfo? FindKnown(string name)
    {
        foreach (EditorModuleInfo module in KnownPlatformModules)
            if (string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase))
                return module;
        return null;
    }

    /// <summary>
    /// Latest published version per module name (absent when unpublished or unreachable). Used by
    /// the settings UI to show "update available"; never throws — an offline machine just gets an
    /// empty map and keeps showing installed versions.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> QueryLatestVersionsAsync(
        IEnumerable<string> names, string? registryUrl = null, CancellationToken cancellation = default)
    {
        var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            string? version = await EditorModuleInstaller.QueryLatestVersionAsync(name, registryUrl, cancellation)
                .ConfigureAwait(false);
            if (version != null)
                latest[name] = version;
        }
        return latest;
    }
}
