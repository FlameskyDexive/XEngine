// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Construction parameters for one <see cref="IAssetStorage"/>. The type name is resolved via
/// Type.GetType at init; the named parameters are passed to the storage via a well-known key set.
/// Mirrors YooAsset's FileSystemParameters.
/// </summary>
public sealed class StorageParameters
{
    /// <summary>The assembly-qualified (or namespace-qualified) type name of the IAssetStorage
    /// implementation to instantiate.</summary>
    public string StorageTypeName { get; }

    /// <summary>Root directory for this storage's on-disk layout.</summary>
    public string PackageRoot { get; }

    /// <summary>Named parameters passed to the storage (decryptor, remote service, etc.).</summary>
    public Dictionary<string, object?> Parameters { get; } = new();

    public StorageParameters(string storageTypeName, string packageRoot)
    {
        StorageTypeName = storageTypeName ?? throw new ArgumentNullException(nameof(storageTypeName));
        PackageRoot = packageRoot ?? throw new ArgumentNullException(nameof(packageRoot));
    }

    public StorageParameters SetParameter(string key, object? value) { Parameters[key] = value; return this; }
    public object? GetParameter(string key) => Parameters.TryGetValue(key, out var v) ? v : null;
    public T? GetParameter<T>(string key) where T : class => GetParameter(key) as T;
}

/// <summary>Well-known keys for <see cref="StorageParameters.Parameters"/>.</summary>
public static class StorageParameterKeys
{
    public const string AssetBundleDecryptor = "assetBundleDecryptor";
    public const string RawBundleDecryptor = "rawBundleDecryptor";
    public const string ArchiveBundleDecryptor = "archiveBundleDecryptor";
    public const string AssetBundleFallbackDecryptor = "assetBundleFallbackDecryptor";
    public const string ManifestDecryptor = "manifestDecryptor";
    public const string RemoteService = "remoteService";
    public const string BuiltInPackageRoot = "builtinPackageRoot";
}
