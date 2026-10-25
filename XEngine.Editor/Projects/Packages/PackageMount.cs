// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

namespace XEngine.Editor.Projects.Packages;

/// <summary>
/// Stable logical view of a resolved package. The physical root may move between embed, local,
/// git, and registry sources; assets and serialized data always address the package through
/// <see cref="LogicalRoot"/>.
/// </summary>
public sealed record PackageMount
{
    public required string Name { get; init; }
    public required string LogicalRoot { get; init; }
    public required string PhysicalRoot { get; init; }
    public required PackageSource Source { get; init; }
    public required bool ReadOnly { get; init; }
    public required string ContentHash { get; init; }

    public static string NormalizeLogicalRoot(string logicalRoot)
    {
        string value = (logicalRoot ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        if (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        return value;
    }

    /// <summary>Return the physical path for an asset addressed beneath this logical package root.</summary>
    public bool TryResolve(string logicalPath, out string physicalPath)
    {
        physicalPath = string.Empty;
        string rootPath = NormalizeLogicalRoot(LogicalRoot);
        string normalizedLogicalPath = (logicalPath ?? string.Empty).Replace('\\', '/');
        if (!normalizedLogicalPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) return false;

        int rootLength = rootPath.Length;
        if (normalizedLogicalPath.Length > rootLength && normalizedLogicalPath[rootLength] != '/') return false;

        string relative = normalizedLogicalPath.Length == rootLength ? string.Empty : normalizedLogicalPath[(rootLength + 1)..];
        if (Path.IsPathRooted(relative)) return false;

        string candidate = Path.GetFullPath(Path.Combine(PhysicalRoot, relative));
        string root = Path.GetFullPath(PhysicalRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            return false;

        physicalPath = candidate;
        return true;
    }
}
