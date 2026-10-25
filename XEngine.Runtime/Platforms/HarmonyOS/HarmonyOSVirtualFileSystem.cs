// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

namespace XEngine.Runtime.Platforms.HarmonyOS;

/// <summary>Roots exposed to the HarmonyOS player without leaking host filesystem paths.</summary>
public enum HarmonyOSVirtualPathRoot
{
    Content,
    Persistent,
    Cache,
    Temp,
}

/// <summary>
/// Canonically contained HarmonyOS player storage. Packaged content is always read-only; the
/// persistent, cache, and temporary roots are the only writable locations.
/// </summary>
public sealed class HarmonyOSVirtualFileSystem
{
    public HarmonyOSVirtualFileSystem(
        string contentRoot,
        string persistentRoot,
        string cacheRoot,
        string tempRoot)
    {
        ContentRoot = NormalizeRoot(contentRoot, nameof(contentRoot));
        PersistentRoot = NormalizeRoot(persistentRoot, nameof(persistentRoot));
        CacheRoot = NormalizeRoot(cacheRoot, nameof(cacheRoot));
        TempRoot = NormalizeRoot(tempRoot, nameof(tempRoot));
    }

    public string ContentRoot { get; }
    public string PersistentRoot { get; }
    public string CacheRoot { get; }
    public string TempRoot { get; }

    public Stream OpenRead(string relativePath)
    {
        string path = Resolve(HarmonyOSVirtualPathRoot.Content, relativePath);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public Stream OpenWrite(HarmonyOSVirtualPathRoot root, string relativePath)
    {
        if (root == HarmonyOSVirtualPathRoot.Content)
            throw new UnauthorizedAccessException("HarmonyOS packaged content is read-only.");
        string path = Resolve(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    }

    public string Resolve(HarmonyOSVirtualPathRoot root, string relativePath)
    {
        string baseRoot = root switch
        {
            HarmonyOSVirtualPathRoot.Content => ContentRoot,
            HarmonyOSVirtualPathRoot.Persistent => PersistentRoot,
            HarmonyOSVirtualPathRoot.Cache => CacheRoot,
            HarmonyOSVirtualPathRoot.Temp => TempRoot,
            _ => throw new ArgumentOutOfRangeException(nameof(root), root, "Unsupported HarmonyOS virtual path root."),
        };
        ValidateRelativePath(relativePath);
        string path = Path.GetFullPath(Path.Combine(baseRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = baseRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, PathComparison))
            throw new UnauthorizedAccessException("HarmonyOS virtual path escapes its root.");
        return path;
    }

    private static string NormalizeRoot(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static void ValidateRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value) ||
            value[0] == '/' || value[0] == '\\' || value.Contains('\\'))
        {
            throw new UnauthorizedAccessException("HarmonyOS virtual path must be relative and slash-normalized.");
        }
        string[] segments = value.Split('/', StringSplitOptions.None);
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0 || segments[i] == "." || segments[i] == "..")
                throw new UnauthorizedAccessException("HarmonyOS virtual path contains an unsafe segment.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
