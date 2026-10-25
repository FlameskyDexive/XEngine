// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace XEngine.Runtime.AssetStreaming;

/// <summary>
/// Read-only provider over a ZIP-compatible package. The constructor takes ownership of the input
/// stream and materializes it in memory only when the platform stream is not seekable.
/// </summary>
public sealed class ZipAssetStreamProvider : IAssetStreamProvider
{
    private readonly object _gate = new();
    private readonly Stream _packageStream;
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);
    private readonly string[] _paths;
    private bool _disposed;

    public ZipAssetStreamProvider(Stream packageStream, string description)
    {
        ArgumentNullException.ThrowIfNull(packageStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Description = description;

        if (packageStream.CanSeek)
        {
            _packageStream = packageStream;
        }
        else
        {
            var seekable = new MemoryStream();
            try
            {
                packageStream.CopyTo(seekable);
                seekable.Position = 0;
                _packageStream = seekable;
            }
            catch
            {
                seekable.Dispose();
                throw;
            }
            finally
            {
                packageStream.Dispose();
            }
        }

        try
        {
            _archive = new ZipArchive(_packageStream, ZipArchiveMode.Read, leaveOpen: true);
            var paths = new List<string>(_archive.Entries.Count);
            for (int i = 0; i < _archive.Entries.Count; i++)
            {
                ZipArchiveEntry entry = _archive.Entries[i];
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    continue;
                string path = AssetStreamPath.Normalize(entry.FullName);
                if (!_entries.TryAdd(path, entry))
                    throw new InvalidDataException($"Asset package contains duplicate path '{path}'.");
                paths.Add(path);
            }
            paths.Sort(StringComparer.Ordinal);
            _paths = paths.ToArray();
        }
        catch
        {
            _packageStream.Dispose();
            throw;
        }
    }

    public string Description { get; }

    public Stream? OpenRead(string path)
    {
        string normalized = AssetStreamPath.Normalize(path);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(normalized, out ZipArchiveEntry? entry))
                return null;
            if (entry.Length > int.MaxValue)
                throw new InvalidDataException($"Asset package entry is too large to materialize: {normalized}");

            var output = new MemoryStream(checked((int)entry.Length));
            try
            {
                using Stream input = entry.Open();
                input.CopyTo(output);
                output.Position = 0;
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
    }

    public IReadOnlyList<string> GetFiles(string directory, string extension)
    {
        string normalizedDirectory = AssetStreamPath.Normalize(directory, allowEmpty: true);
        string normalizedExtension = AssetStreamPath.NormalizeExtension(extension);
        string prefix = normalizedDirectory.Length == 0 ? string.Empty : normalizedDirectory + "/";
        lock (_gate)
        {
            ThrowIfDisposed();
            var result = new List<string>();
            for (int i = 0; i < _paths.Length; i++)
            {
                string path = _paths[i];
                if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
                    !path.EndsWith(normalizedExtension, StringComparison.Ordinal))
                {
                    continue;
                }
                string remainder = path[prefix.Length..];
                if (!remainder.Contains('/'))
                    result.Add(path);
            }
            return result;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _archive.Dispose();
            _packageStream.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
