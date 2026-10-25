// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// IBundleStore - the byte-level storage abstraction. One implementation per storage location
// (the app package's read-only Bundles/ folder, the writable downloads cache, an in-editor virtual
// store). The resource layer asks a store to load/verify a bundle's bytes; the store decides where
// to find them.
//
// Independent rewrite of YooAsset's IBundleCache (Apache-2.0). The method set
// (InitializeAsync/WriteCacheAsync/LoadBundleAsync/IsCached/GetCacheFilePath + read-only/space
// properties) mirrors that interface; concrete implementations are XEngine-specific.

using System.Threading.Tasks;

namespace XEngine.Runtime.AssetBundles;

/// <summary>Byte-level storage for bundles: where the bytes live and how to read/write/verify them.</summary>
public interface IBundleStore
{
    /// <summary>Human-readable name for diagnostics (e.g. "builtin", "sandbox", "editor").</summary>
    string Name { get; }

    /// <summary>Root directory of this store's on-disk layout.</summary>
    string RootPath { get; }

    /// <summary>True if the store is read-only (e.g. the app-package builtin store).</summary>
    bool IsReadOnly { get; }

    /// <summary>Initialize the store (create directories, load catalogs, etc.).</summary>
    Task InitializeAsync();

    /// <summary>True if the bundle is present in this store.</summary>
    bool IsCached(BundleHash bundleGuid);

    /// <summary>The on-disk path of the cached bundle file, or null if not present.</summary>
    string? GetCacheFilePath(BundleHash bundleGuid);

    /// <summary>Load a bundle's bytes into an IBundleHandle, asynchronously. The result is null on
    /// failure; the store sets the operation's error.</summary>
    AsyncOperationBase<IBundleHandle?> LoadBundleAsync(BundleInfo bundle, IBundleDecryptor? decryptor);

    /// <summary>Write a downloaded bundle's bytes to this store (download path). Read-only stores throw.</summary>
    Task WriteCacheAsync(BundleInfo bundle, byte[] data);

    /// <summary>Verify every cached file's CRC/hash; repair or remove corrupt files.</summary>
    Task VerifyCacheAsync();

    /// <summary>Delete all cached files.</summary>
    Task ClearCacheAsync();

    /// <summary>Number of files currently in the store.</summary>
    long FileCount { get; }

    /// <summary>Total bytes occupied.</summary>
    long SpaceOccupied { get; }
}
