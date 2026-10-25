// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// AssetPackage - the public per-package API. Owns a StorageHost and an AssetManager. The caller
// initializes it with a play-mode-specific options object (which carries the storages to wire up),
// optionally requests the version + manifest (Host mode), then loads assets via the typed handle
// methods. Drives the manager through the AsyncOperationSystem (XAssets.Tick).
//
// Independent rewrite of YooAsset's ResourcePackage (Apache-2.0). Public surface (Initialize /
// LoadAsset / LoadScene / Unload / GetAssetInfo / CreateResourceDownloader) mirrors that design.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// One asset package: a manifest + a set of storages + a reference-counted manager. Created via
/// <see cref="XAssets.CreatePackage"/>. Initialize with a play-mode options object before loading.
/// </summary>
public sealed class AssetPackage
{
    private readonly string _name;
    private StorageHost? _storageHost;
    private AssetManager? _manager;
    private PackageManifest? _manifest;
    private AssetPlayMode _playMode = AssetPlayMode.None;
    private bool _initialized;

    public string Name => _name;
    public AssetPlayMode PlayMode => _playMode;
    public bool IsInitialized => _initialized;
    public PackageManifest? ActiveManifest => _manifest;
    public string? PackageVersion { get; private set; }

    internal AssetPackage(string name) { _name = name ?? throw new ArgumentNullException(nameof(name)); }

    // ----------------------------------------------------------------
    //  Initialization
    // ----------------------------------------------------------------

    /// <summary>Initialize the package with the given storages. The last storage is the "main" one.
    /// Called by the play-mode options' initialization operation, but exposed for direct callers
    /// (tests, custom modes).</summary>
    public void Initialize(AssetPlayMode mode, IReadOnlyList<IAssetStorage> storages,
        int bundleLoadingMaxConcurrency = int.MaxValue, bool autoUnloadBundleWhenUnused = false)
    {
        if (_initialized) throw new InvalidOperationException($"Package '{_name}' is already initialized.");
        if (storages == null || storages.Count == 0)
            throw new ArgumentException("At least one storage is required.", nameof(storages));

        _playMode = mode;
        _storageHost = new StorageHost();
        foreach (var s in storages) _storageHost.AddStorage(s);
        _initialized = true;
        // The manager is created later, after the manifest is loaded (it needs the manifest + a
        // bundle-handle factory derived from the storage host).
        _bundleLoadingMaxConcurrency = bundleLoadingMaxConcurrency;
        _autoUnloadBundleWhenUnused = autoUnloadBundleWhenUnused;
    }

    private int _bundleLoadingMaxConcurrency = int.MaxValue;
    private bool _autoUnloadBundleWhenUnused;

    /// <summary>Set the active manifest and create the manager. Called after LoadPackageManifestAsync
    /// completes (or directly for tests / simulate mode).</summary>
    public void SetActiveManifest(PackageManifest manifest)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        var factory = _storageHost!.CreateBundleHandleFactory(decryptor: null);
        _manager = new AssetManager(manifest, factory)
        {
            BundleLoadingMaxConcurrency = _bundleLoadingMaxConcurrency,
            AutoUnloadBundleWhenUnused = _autoUnloadBundleWhenUnused,
        };
    }

    // ----------------------------------------------------------------
    //  Version / manifest (Host mode)
    // ----------------------------------------------------------------

    public AsyncOperationBase<string?> RequestPackageVersionAsync()
    {
        if (_storageHost == null) throw NotInitialized();
        return _storageHost.RequestPackageVersionAsync();
    }

    public AsyncOperationBase<PackageManifest?> LoadPackageManifestAsync(string version, IManifestDecryptor? decryptor = null)
    {
        if (_storageHost == null) throw NotInitialized();
        PackageVersion = version;
        var op = _storageHost.LoadPackageManifestAsync(version, decryptor);
        // Wire the manifest into SetActiveManifest on completion.
        op.Completed += _ =>
        {
            if (op.Status == EOperationStatus.Succeeded && op.Result != null)
                SetActiveManifest(op.Result);
        };
        return op;
    }

    // ----------------------------------------------------------------
    //  Asset loading (delegates to AssetManager)
    // ----------------------------------------------------------------

    public AssetHandle LoadAssetAsync<T>(string location) where T : EngineObject
        => Manager().LoadAssetAsync<T>(location);

    public AssetHandle LoadAssetAsync<T>(Guid guid) where T : EngineObject
        => Manager().LoadAssetAsync<T>(guid);

    /// <summary>Synchronously load: blocks until the load completes. Pumps the async scheduler in a
    /// loop rather than calling WaitForAsyncComplete on the handle (which would re-enter through
    /// each loader's own sync-wait and can double-finalize). Bounded to avoid infinite loops.</summary>
    public AssetHandle LoadAssetSync<T>(string location) where T : EngineObject
    {
        var handle = LoadAssetAsync<T>(location);
        for (int i = 0; i < 100000 && !handle.IsDone; i++)
            AsyncOperationSystem.Update();
        return handle;
    }

    /// <summary>Resolve the exact asset identity, independent of non-unique display addresses.</summary>
    public AssetHandle LoadAssetSync<T>(Guid guid) where T : EngineObject
    {
        var handle = LoadAssetAsync<T>(guid);
        for (int i = 0; i < 100000 && !handle.IsDone; i++)
            AsyncOperationSystem.Update();
        return handle;
    }

    public void UnloadUnusedAssets() => Manager().UnloadUnusedAssets();
    public void UnloadAllAssets() => Manager().UnloadAllAssets();

    // ----------------------------------------------------------------
    //  Downloader (Host mode)
    // ----------------------------------------------------------------

    /// <summary>Build a downloader for every bundle the active manifest needs that no storage has yet.
    /// Caller invokes StartDownload then pumps XAssets.Tick. Returns null if the package has no
    /// sandbox storage configured (i.e. not in Host mode).</summary>
    public ResourceDownloader? CreateResourceDownloader(IDownloadBackend backend, IRemoteService remote, int maxConcurrency = 8)
    {
        if (_manifest == null || _storageHost == null) return null;
        // Find the SandboxStorage among the host's storages (it holds the downloads cache + remote).
        SandboxStorage? sandbox = null;
        foreach (var s in _storageHost.Storages) if (s is SandboxStorage sb) { sandbox = sb; break; }
        if (sandbox?.Store == null) return null;

        var items = new System.Collections.Generic.List<DownloadItem>();
        foreach (var b in _manifest.BundleList)
        {
            // Skip bundles any storage already has (builtin or previously downloaded).
            if (_storageHost.GetOwnerStorage(b) != null) continue;
            items.Add(new DownloadItem { Bundle = b, FileName = b.FileName });
        }
        if (items.Count == 0) return null;
        return new ResourceDownloader(backend, remote, sandbox.Store, items, maxConcurrency);
    }

    /// <summary>Convenience: build a downloader using the default HttpClient backend and the sandbox's
    /// configured remote service. Returns null if not in Host mode or nothing to download.</summary>
    public ResourceDownloader? CreateResourceDownloader(int maxConcurrency = 8)
    {
        if (_storageHost == null) return null;
        foreach (var s in _storageHost.Storages)
            if (s is SandboxStorage sb && sb.Remote != null)
                return CreateResourceDownloader(new HttpClientDownloadBackend(), sb.Remote, maxConcurrency);
        return null;
    }

    // ----------------------------------------------------------------
    //  Queries
    // ----------------------------------------------------------------

    public bool IsLocationValid(string location)
        => _manifest != null && _manifest.TryGetAssetByAddress(location, out _);

    public ManifestAsset? GetAssetInfo(string location)
        => _manifest != null && _manifest.TryGetAssetByAddress(location, out var a) ? a : null;

    public IReadOnlyList<ManifestAsset> GetAssetInfos(string tag)
        => _manifest?.GetAssetsByTag(tag) ?? Array.Empty<ManifestAsset>();

    public AssetBundleManifest? GetManifest()
        => _manifest != null ? new AssetBundleManifest(_manifest) : null;

    /// <summary>Tear down the package: unload everything and clear the storages.</summary>
    public void Destroy()
    {
        _manager?.UnloadAllAssets();
        _manager = null;
        _storageHost?.OnDestroy();
        _storageHost = null;
        _manifest = null;
        _initialized = false;
        _playMode = AssetPlayMode.None;
    }

    private AssetManager Manager()
    {
        if (_manager == null) throw new InvalidOperationException(
            $"Package '{_name}' has no active manifest. Call LoadPackageManifestAsync (or SetActiveManifest) before loading assets.");
        return _manager;
    }

    private InvalidOperationException NotInitialized()
        => new($"Package '{_name}' is not initialized. Call Initialize(...) first.");
}
