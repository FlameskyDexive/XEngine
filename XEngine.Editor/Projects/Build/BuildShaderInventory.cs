// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Packages;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Build;

/// <summary>
/// Project build defaults, independent of which optional package assemblies happen to be loaded
/// in the editor process. Explicit asset / scene / contributor usages are collected separately.
/// </summary>
internal static class BuildShaderInventory
{
    internal static IReadOnlyList<Guid> GetBuiltInShaderGuids(Project? project)
    {
        var installedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (project != null)
        {
            IReadOnlyList<ResolvedPackage> packages = PackageManager.Resolve(project);
            for (int i = 0; i < packages.Count; i++)
                installedPackages.Add(packages[i].Name);
        }

        BuiltInAssets.Initialize();
        var shaderGuids = new List<Guid>();
        foreach (KeyValuePair<Guid, BuiltInAssets.BuiltInEntry> item in BuiltInAssets.Entries)
        {
            BuiltInAssets.BuiltInEntry entry = item.Value;
            if (!typeof(Shader).IsAssignableFrom(entry.AssetType))
                continue;
            if (entry.PackageName != null && !installedPackages.Contains(entry.PackageName))
                continue;
            shaderGuids.Add(item.Key);
        }
        shaderGuids.Sort();
        return shaderGuids;
    }
}
