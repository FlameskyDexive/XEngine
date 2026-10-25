// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

namespace XEngine.Runtime.AssetStreaming;

/// <summary>
/// Synchronous in-package files for WeChat / Douyin. Host JS fills this from
/// <c>getFileSystemManager().readFileSync</c> or after <c>downloadFile</c> / <c>loadSubpackage</c>.
/// Runtime wasm itself is never stored here — that stays on <c>WXWebAssembly.instantiate(path)</c>.
/// </summary>
public sealed class MiniGamePackageFileProvider : IAssetStreamProvider
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    public MiniGamePackageFileProvider()
    {
    }

    public MiniGamePackageFileProvider(IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (KeyValuePair<string, byte[]> pair in files)
            Add(pair.Key, pair.Value);
    }

    public string Description => "minigame-package";

    public int FileCount
    {
        get
        {
            lock (_gate)
                return _files.Count;
        }
    }

    public void Add(string path, byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized = AssetStreamPath.Normalize(path);
        ArgumentNullException.ThrowIfNull(data);
        byte[] copy = new byte[data.Length];
        Buffer.BlockCopy(data, 0, copy, 0, data.Length);
        lock (_gate)
            _files[normalized] = copy;
    }

    public Stream? OpenRead(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized = AssetStreamPath.Normalize(path);
        lock (_gate)
        {
            return _files.TryGetValue(normalized, out byte[]? value)
                ? new MemoryStream(value, writable: false)
                : null;
        }
    }

    public IReadOnlyList<string> GetFiles(string directory, string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalizedDirectory = AssetStreamPath.Normalize(directory, allowEmpty: true);
        string normalizedExtension = AssetStreamPath.NormalizeExtension(extension);
        string prefix = normalizedDirectory.Length == 0 ? string.Empty : normalizedDirectory + "/";
        var result = new List<string>();
        lock (_gate)
        {
            foreach (string path in _files.Keys)
            {
                if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
                    !path.EndsWith(normalizedExtension, StringComparison.Ordinal))
                {
                    continue;
                }

                string remainder = path[prefix.Length..];
                if (!remainder.Contains('/'))
                    result.Add(path);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
            _files.Clear();
    }
}
