// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using XEngine.Async;

namespace XEngine.Runtime.AssetStreaming;

/// <summary>Mount table for platform-independent asset paths such as Assets/Bundles/main.bundle.</summary>
public sealed class AssetVirtualFileSystem : IAssetStreamProvider
{
    private readonly Dictionary<string, Mount> _mounts = new(StringComparer.Ordinal);
    private bool _disposed;

    public string Description => "asset vfs";

    public void MountProvider(string mountName, IAssetStreamProvider provider, bool ownsProvider = false)
    {
        ArgumentNullException.ThrowIfNull(provider);
        string normalized = AssetStreamPath.Normalize(mountName);
        if (normalized.Contains('/'))
            throw new ArgumentException("A VFS mount name must be one path segment.", nameof(mountName));
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_mounts.TryAdd(normalized, new Mount(provider, ownsProvider)))
            throw new InvalidOperationException($"Asset VFS mount '{normalized}' already exists.");
    }

    public Stream? OpenRead(string virtualPath)
    {
        (Mount mount, string relativePath) = ResolveFile(virtualPath);
        return mount.Provider.OpenRead(relativePath);
    }

    public XTask<Stream?> OpenReadAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        (Mount mount, string relativePath) = ResolveFile(virtualPath);
        return mount.Provider.OpenReadAsync(relativePath, cancellationToken);
    }

    public IReadOnlyList<string> GetFiles(string virtualDirectory, string extension)
    {
        (string mountName, Mount mount, string relativeDirectory) = ResolveDirectory(virtualDirectory);
        IReadOnlyList<string> relativeFiles = mount.Provider.GetFiles(relativeDirectory, extension);
        if (relativeFiles.Count == 0)
            return Array.Empty<string>();
        var result = new string[relativeFiles.Count];
        for (int i = 0; i < relativeFiles.Count; i++)
            result[i] = mountName + "/" + relativeFiles[i];
        return result;
    }

    public bool IsWritableMount(string mountName)
    {
        string normalized = AssetStreamPath.Normalize(mountName);
        if (normalized.Contains('/'))
            throw new ArgumentException("A VFS mount name must be one path segment.", nameof(mountName));
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _mounts.TryGetValue(normalized, out Mount mount) &&
            mount.Provider is IWritableAssetStreamProvider;
    }

    public Stream OpenWrite(string virtualPath, bool overwrite = true)
    {
        (Mount mount, string relativePath) = ResolveFile(virtualPath);
        return GetWritableProvider(mount, virtualPath).OpenWrite(relativePath, overwrite);
    }

    public bool Delete(string virtualPath)
    {
        (Mount mount, string relativePath) = ResolveFile(virtualPath);
        return GetWritableProvider(mount, virtualPath).Delete(relativePath);
    }

    public void CreateDirectory(string virtualDirectory)
    {
        (_, Mount mount, string relativeDirectory) = ResolveDirectory(virtualDirectory);
        GetWritableProvider(mount, virtualDirectory).CreateDirectory(relativeDirectory);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (Mount mount in _mounts.Values)
        {
            if (mount.OwnsProvider)
                mount.Provider.Dispose();
        }
        _mounts.Clear();
    }

    private (Mount Mount, string RelativePath) ResolveFile(string virtualPath)
    {
        string normalized = AssetStreamPath.Normalize(virtualPath);
        int separator = normalized.IndexOf('/');
        if (separator <= 0 || separator == normalized.Length - 1)
            throw new ArgumentException("A VFS file path must include a mount and relative path.", nameof(virtualPath));
        string mountName = normalized[..separator];
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_mounts.TryGetValue(mountName, out Mount mount))
            throw new DirectoryNotFoundException($"Asset VFS mount '{mountName}' is not registered.");
        return (mount, normalized[(separator + 1)..]);
    }

    private (string MountName, Mount Mount, string RelativeDirectory) ResolveDirectory(string virtualDirectory)
    {
        string normalized = AssetStreamPath.Normalize(virtualDirectory);
        int separator = normalized.IndexOf('/');
        string mountName = separator < 0 ? normalized : normalized[..separator];
        string relativeDirectory = separator < 0 ? string.Empty : normalized[(separator + 1)..];
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_mounts.TryGetValue(mountName, out Mount mount))
            throw new DirectoryNotFoundException($"Asset VFS mount '{mountName}' is not registered.");
        return (mountName, mount, relativeDirectory);
    }

    private static IWritableAssetStreamProvider GetWritableProvider(Mount mount, string virtualPath) =>
        mount.Provider as IWritableAssetStreamProvider ??
        throw new NotSupportedException($"Asset VFS path '{virtualPath}' belongs to a read-only mount.");

    private readonly record struct Mount(IAssetStreamProvider Provider, bool OwnsProvider);
}
