// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// AssetManager - the orchestrator. Owns the provider pool (keyed by asset key) and the bundle loader
// pool (keyed by bundle GUID). For each LoadAsset it: resolves the asset's bundles via the manifest,
// creates/looks-up the bundle loaders and increments their refcounts, creates/looks-up the provider
// and returns a handle. Drives auto-unload when providers become idle.
//
// Independent rewrite of YooAsset's ResourceManager (Apache-2.0). The dual-pool + refcount model
// matches that design; the bundle-handle factory is injected by the Storage layer (Stage C).

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Owns the provider and bundle-loader pools for one <see cref="AssetPackage"/>. Not created
/// directly - obtained from <see cref="AssetPackage"/> after the manifest is loaded.
/// </summary>
public sealed class AssetManager
{
    private readonly Dictionary<string, AssetProviderBase> _providers = new();
    private readonly Dictionary<BundleHash, BundleLoader> _bundleLoaders = new();

    private readonly PackageManifest _manifest;
    private readonly Func<BundleInfo, AsyncOperationBase<IBundleHandle>> _bundleHandleFactory;

    private int _bundleLoadingCount;
    private int _maxBundleLoadConcurrency = int.MaxValue;
    private bool _autoUnloadBundleWhenUnused;

    public AssetManager(PackageManifest manifest, Func<BundleInfo, AsyncOperationBase<IBundleHandle>> bundleHandleFactory)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _bundleHandleFactory = bundleHandleFactory ?? throw new ArgumentNullException(nameof(bundleHandleFactory));
    }

    /// <summary>Maximum number of bundle file loads that may be in flight at once.</summary>
    public int BundleLoadingMaxConcurrency
    {
        get => _maxBundleLoadConcurrency;
        set => _maxBundleLoadConcurrency = Math.Max(1, value);
    }

    /// <summary>If true, a bundle whose refcount reaches zero is unloaded immediately (rather than
    /// waiting for the next explicit UnloadUnusedAssets call).</summary>
    public bool AutoUnloadBundleWhenUnused
    {
        get => _autoUnloadBundleWhenUnused;
        set => _autoUnloadBundleWhenUnused = value;
    }

    // ---- Public load API ----

    /// <summary>Begin loading a single asset by addressable location. Returns a typed handle; the
    /// caller owns the handle and must Release() it.</summary>
    public AssetHandle LoadAssetAsync<T>(string location) where T : EngineObject
    {
        var info = Resolve(location, typeof(T));
        var provider = GetOrCreateProvider<AssetProvider>(info, (m, k, i) => new AssetProvider(m, k, i));
        return provider.CreateHandle();
    }

    /// <summary>Begin loading by GUID directly.</summary>
    public AssetHandle LoadAssetAsync<T>(Guid guid) where T : EngineObject
    {
        var info = ResolveByGuid(guid, typeof(T));
        var provider = GetOrCreateProvider<AssetProvider>(info, (m, k, i) => new AssetProvider(m, k, i));
        return provider.CreateHandle();
    }

    /// <summary>Unload every provider whose refcount is zero and every bundle loader that is
    /// releasable. Called periodically by the package / explicitly by the user.</summary>
    public void UnloadUnusedAssets()
    {
        TryDestroyIdleProviders();
        TryDestroyReleasableBundles();
    }

    /// <summary>Unload ALL providers and bundles regardless of refcount. Used by AssetPackage.Destroy.</summary>
    public void UnloadAllAssets()
    {
        foreach (var kv in _providers)
        {
            kv.Value.Destroy();
            kv.Value.AbortOperation();
        }
        _providers.Clear();

        foreach (var kv in _bundleLoaders)
        {
            kv.Value.Destroy();
            kv.Value.AbortOperation();
        }
        _bundleLoaders.Clear();
        _bundleLoadingCount = 0;
    }

    // ---- Internal: provider pool ----

    private TProvider GetOrCreateProvider<TProvider>(AssetLoadInfo info, Func<AssetManager, string, AssetLoadInfo, TProvider> factory)
        where TProvider : AssetProviderBase
    {
        string key = BuildProviderKey(info);
        if (_providers.TryGetValue(key, out var existing))
            return (TProvider)existing;

        var provider = factory(this, key, info);
        _providers[key] = provider;
        AsyncOperationSystem.StartOperation(provider);
        return provider;
    }

    private static string BuildProviderKey(AssetLoadInfo info)
        => $"{info.AssetGuid}:{info.AssetType.FullName}";

    internal void OnProviderIdle(AssetProviderBase provider)
    {
        // Drop idle providers so the next load re-creates them. (A pool-with-eviction could keep
        // them warm; for now idle = removable.) Destroy the provider here so its bundle-loader
        // references are released (which in turn lets the bundles unload).
        if (provider.IsIdle)
        {
            _providers.Remove(provider.ProviderKey);
            provider.Destroy();
            provider.AbortOperation();
        }

        if (_autoUnloadBundleWhenUnused) UnloadUnusedAssets();
    }

    private void TryDestroyIdleProviders()
    {
        List<string>? toRemove = null;
        foreach (var kv in _providers)
        {
            if (kv.Value.IsIdle)
            {
                (toRemove ??= new List<string>()).Add(kv.Key);
                kv.Value.Destroy();
                kv.Value.AbortOperation();
            }
        }
        if (toRemove != null)
            foreach (var key in toRemove) _providers.Remove(key);
    }

    // ---- Internal: bundle loader pool ----

    /// <summary>Get the main bundle loader + dependency loaders for an asset. The provider calls
    /// Reference() on each (in its constructor) and Release() in Dispose.</summary>
    internal IReadOnlyList<BundleLoader> GetOrCreateBundleLoaders(ManifestAsset asset)
    {
        var deps = _manifest.GetAllAssetDependencies(asset);
        var result = new List<BundleLoader>(deps.Count);
        foreach (var bundle in deps)
        {
            if (!_bundleLoaders.TryGetValue(bundle.BundleGuid, out var loader))
            {
                loader = new BundleLoader(this, bundle, CreateBundleHandleOperation);
                _bundleLoaders[bundle.BundleGuid] = loader;
            }
            result.Add(loader);
        }
        return result;
    }

    private AsyncOperationBase<IBundleHandle> CreateBundleHandleOperation(BundleLoader loader)
    {
        return _bundleHandleFactory(loader.BundleInfo);
    }

    /// <summary>Called by a bundle loader when its refcount hits zero.</summary>
    internal void TryDestroyBundle(BundleLoader loader)
    {
        if (!_autoUnloadBundleWhenUnused) return;
        TryDestroyReleasableBundles();
    }

    private void TryDestroyReleasableBundles()
    {
        List<BundleHash>? toRemove = null;
        foreach (var kv in _bundleLoaders)
        {
            if (kv.Value.CanDestroy())
            {
                (toRemove ??= new List<BundleHash>()).Add(kv.Key);
                kv.Value.Destroy();
                kv.Value.AbortOperation();
            }
        }
        if (toRemove != null)
            foreach (var key in toRemove) _bundleLoaders.Remove(key);
    }

    internal bool IsBundleLoaded(int bundleListIndex)
    {
        // Used by BundleLoader.CanDestroy to check referrer liveness.
        if (bundleListIndex < 0 || bundleListIndex >= _manifest.BundleList.Count) return false;
        var bundle = _manifest.BundleList[bundleListIndex];
        return _bundleLoaders.TryGetValue(bundle.BundleGuid, out var loader) && loader.IsBundleLoaded;
    }

    internal bool IsBundleLoadingBusy() => _bundleLoadingCount >= _maxBundleLoadConcurrency;
    internal void IncrementBundleLoadingCounter() => _bundleLoadingCount++;
    internal void DecrementBundleLoadingCounter() { if (_bundleLoadingCount > 0) _bundleLoadingCount--; }

    /// <summary>Lookup the bundle info by manifest index (used by providers to find the asset's own
    /// bundle among its dependency closure).</summary>
    internal BundleInfo GetBundleInfo(int bundleId)
    {
        if (bundleId < 0 || bundleId >= _manifest.BundleList.Count)
            throw new ArgumentOutOfRangeException(nameof(bundleId));
        return _manifest.BundleList[bundleId];
    }

    // ---- Resolution ----

    private AssetLoadInfo ResolveByGuid(Guid guid, Type type)
    {
        if (!_manifest.TryGetAssetByGuid(guid, out var asset))
            throw new KeyNotFoundException($"Asset GUID not found in package '{_manifest.PackageName}': {guid}");
        return new AssetLoadInfo { Asset = asset, AssetType = type, Address = guid.ToString() };
    }

    private AssetLoadInfo Resolve(string location, Type type)
    {
        if (!_manifest.TryGetAssetByAddress(location, out var asset))
            throw new KeyNotFoundException($"Asset not found in package '{_manifest.PackageName}': {location}");
        return new AssetLoadInfo { Asset = asset, AssetType = type, Address = location };
    }
}
