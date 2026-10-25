// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

namespace XEngine.Runtime.AssetStreaming;

/// <summary>Read-only provider rooted at one physical directory.</summary>
public sealed class DirectoryAssetStreamProvider : IWritableAssetStreamProvider
{
    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly StringComparison _pathComparison;

    public DirectoryAssetStreamProvider(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    public string Description => _root;

    public Stream? OpenRead(string path)
    {
        string normalized = AssetStreamPath.Normalize(path);
        string physicalPath = Resolve(normalized, allowEmpty: false);
        return File.Exists(physicalPath) && HasExactPathCasing(normalized)
            ? new FileStream(
                physicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
    }

    public IReadOnlyList<string> GetFiles(string directory, string extension)
    {
        string normalizedDirectory = AssetStreamPath.Normalize(directory, allowEmpty: true);
        string normalizedExtension = AssetStreamPath.NormalizeExtension(extension);
        string physicalDirectory = Resolve(normalizedDirectory, allowEmpty: true);
        if (!Directory.Exists(physicalDirectory) || !HasExactPathCasing(normalizedDirectory))
            return Array.Empty<string>();

        string[] physicalFiles = Directory.GetFiles(physicalDirectory, "*", SearchOption.TopDirectoryOnly);
        var logicalFiles = new List<string>(physicalFiles.Length);
        for (int i = 0; i < physicalFiles.Length; i++)
        {
            if (!physicalFiles[i].EndsWith(normalizedExtension, StringComparison.Ordinal))
                continue;
            string relative = Path.GetRelativePath(_root, physicalFiles[i]).Replace('\\', '/');
            logicalFiles.Add(AssetStreamPath.Normalize(relative));
        }
        logicalFiles.Sort(StringComparer.Ordinal);
        return logicalFiles;
    }

    public Stream OpenWrite(string path, bool overwrite = true)
    {
        string normalized = AssetStreamPath.Normalize(path);
        string physicalPath = Resolve(normalized, allowEmpty: false);
        string parent = Path.GetDirectoryName(physicalPath)!;
        Directory.CreateDirectory(parent);
        if (File.Exists(physicalPath) && !HasExactPathCasing(normalized))
            throw new IOException($"Asset path casing does not match an existing file: {path}");
        return new FileStream(
            physicalPath,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous);
    }

    public bool Delete(string path)
    {
        string normalized = AssetStreamPath.Normalize(path);
        string physicalPath = Resolve(normalized, allowEmpty: false);
        if (!File.Exists(physicalPath) || !HasExactPathCasing(normalized))
            return false;
        File.Delete(physicalPath);
        return true;
    }

    public void CreateDirectory(string directory)
    {
        string normalized = AssetStreamPath.Normalize(directory, allowEmpty: true);
        Directory.CreateDirectory(Resolve(normalized, allowEmpty: true));
    }

    public void Dispose()
    {
    }

    private string Resolve(string path, bool allowEmpty)
    {
        string normalized = AssetStreamPath.Normalize(path, allowEmpty);
        if (normalized.Length == 0)
            return _root;

        string physicalPath = Path.GetFullPath(
            Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!physicalPath.StartsWith(_rootPrefix, _pathComparison))
            throw new IOException($"Asset path escapes provider root: {path}");
        return physicalPath;
    }

    private bool HasExactPathCasing(string normalizedPath)
    {
        if (normalizedPath.Length == 0)
            return true;

        // Windows AND default APFS macOS volumes are case-insensitive: File.Exists alone accepts
        // wrong-cased requests, and such assets would break the moment they ship to a
        // case-sensitive target (or a case-sensitive APFS volume). Require an exact-Ordinal
        // match on every path segment; on case-sensitive filesystems (Linux) the walk is a
        // cheap no-op because File.Exists already enforced the casing.
        string current = _root;
        int segmentStart = 0;
        while (segmentStart < normalizedPath.Length)
        {
            int separator = normalizedPath.IndexOf('/', segmentStart);
            int segmentEnd = separator < 0 ? normalizedPath.Length : separator;
            string segment = normalizedPath[segmentStart..segmentEnd];
            string? exactEntry = null;
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (string.Equals(Path.GetFileName(entry), segment, StringComparison.Ordinal))
                {
                    exactEntry = entry;
                    break;
                }
            }
            if (exactEntry == null)
                return false;
            current = exactEntry;
            segmentStart = separator < 0 ? normalizedPath.Length : separator + 1;
        }
        return true;
    }
}
