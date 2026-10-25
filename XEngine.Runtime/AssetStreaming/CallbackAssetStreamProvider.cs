// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

namespace XEngine.Runtime.AssetStreaming;

/// <summary>Validated adapter for platform stream APIs such as Android AssetManager.</summary>
public sealed class CallbackAssetStreamProvider : IAssetStreamProvider
{
    private readonly Func<string, Stream?> _openRead;
    private readonly Func<string, string, IReadOnlyList<string>> _getFiles;
    private readonly Action? _dispose;
    private bool _disposed;

    public CallbackAssetStreamProvider(
        string description,
        Func<string, Stream?> openRead,
        Func<string, string, IReadOnlyList<string>> getFiles,
        Action? dispose = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Description = description;
        _openRead = openRead ?? throw new ArgumentNullException(nameof(openRead));
        _getFiles = getFiles ?? throw new ArgumentNullException(nameof(getFiles));
        _dispose = dispose;
    }

    public string Description { get; }

    public Stream? OpenRead(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _openRead(AssetStreamPath.Normalize(path));
    }

    public IReadOnlyList<string> GetFiles(string directory, string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalizedDirectory = AssetStreamPath.Normalize(directory, allowEmpty: true);
        string normalizedExtension = AssetStreamPath.NormalizeExtension(extension);
        IReadOnlyList<string> source = _getFiles(normalizedDirectory, normalizedExtension) ??
            throw new InvalidDataException($"{Description} returned a null file list.");
        if (source.Count == 0)
            return Array.Empty<string>();

        string prefix = normalizedDirectory.Length == 0 ? string.Empty : normalizedDirectory + "/";
        var result = new string[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            string path = AssetStreamPath.Normalize(source[i]);
            if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
                !path.EndsWith(normalizedExtension, StringComparison.Ordinal) ||
                path[prefix.Length..].Contains('/'))
            {
                throw new InvalidDataException(
                    $"{Description} returned '{path}' outside directory '{normalizedDirectory}'.");
            }
            result[i] = path;
        }
        Array.Sort(result, StringComparer.Ordinal);
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _dispose?.Invoke();
    }
}
