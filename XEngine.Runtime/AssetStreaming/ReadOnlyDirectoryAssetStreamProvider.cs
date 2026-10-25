// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

namespace XEngine.Runtime.AssetStreaming;

/// <summary>
/// Read-only view of a directory. Used for packaged iOS <c>Content/</c> so the VFS
/// <c>Assets</c> mount is not writable.
/// </summary>
public sealed class ReadOnlyDirectoryAssetStreamProvider : IAssetStreamProvider
{
    private readonly DirectoryAssetStreamProvider _inner;

    public ReadOnlyDirectoryAssetStreamProvider(string root)
    {
        _inner = new DirectoryAssetStreamProvider(root);
    }

    public string Description => _inner.Description;

    public Stream? OpenRead(string path) => _inner.OpenRead(path);

    public IReadOnlyList<string> GetFiles(string directory, string extension) =>
        _inner.GetFiles(directory, extension);

    public void Dispose() => _inner.Dispose();
}
