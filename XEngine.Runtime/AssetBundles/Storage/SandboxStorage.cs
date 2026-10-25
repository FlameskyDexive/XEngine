// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// SandboxStorage - the Host-mode "downloads cache" storage strategy. It owns a SandboxBundleStore
// under Application.PersistentDataPath and serves bundles the host has downloaded. In Host play mode
// it is paired with a BuiltinStorage (which serves the baseline bundles shipped in the package); the
// StorageHost routes each bundle to whichever storage can accept it.

using System;
using System.IO;
using System.Threading.Tasks;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Storage strategy backed by a writable <see cref="SandboxBundleStore"/>. Used as the main storage
/// in Host play mode (alongside a BuiltinStorage for the shipped baseline). The manifest and version
/// are fetched from a remote endpoint via the configured <see cref="IRemoteService"/>.
/// </summary>
public sealed class SandboxStorage : IAssetStorage
{
    private string _packageName = string.Empty;
    private string _root = string.Empty;
    private SandboxBundleStore? _store;
    private IBundleDecryptor? _bundleDecryptor;
    private IManifestDecryptor? _manifestDecryptor;
    private IRemoteService? _remote;
    private string? _builtinPackageRoot; // where to find the baseline version/manifest file

    public string Name => "sandbox";
    public string PackageName => _packageName;

    public Task OnCreateAsync(string packageName, string packageRoot)
    {
        _packageName = packageName ?? throw new ArgumentNullException(nameof(packageName));
        _root = packageRoot ?? throw new ArgumentNullException(nameof(packageRoot));
        return Task.CompletedTask;
    }

    /// <summary>Configure with the parameters resolved from <see cref="StorageParameters"/>.</summary>
    public void Configure(IBundleDecryptor? bundleDecryptor, IManifestDecryptor? manifestDecryptor,
        IRemoteService? remote, string? builtinPackageRoot)
    {
        _bundleDecryptor = bundleDecryptor;
        _manifestDecryptor = manifestDecryptor;
        _remote = remote;
        _builtinPackageRoot = builtinPackageRoot;
    }

    public async Task InitializeStores(AssetBundlePlatform platform)
    {
        _store = new SandboxBundleStore(_root, platform, _bundleDecryptor);
        await _store.InitializeAsync();
    }

    public bool CanAcceptBundle(BundleInfo bundle) => _store?.IsCached(bundle.BundleGuid) ?? false;

    /// <summary>A bundle requires download if the sandbox doesn't have it AND there's a remote
    /// service configured to fetch it. (Builtin bundles are never downloaded by the sandbox.)</summary>
    public bool IsDownloadRequired(BundleInfo bundle) => _remote != null && !CanAcceptBundle(bundle);

    public AsyncOperationBase<string?> RequestPackageVersionAsync()
    {
        var op = new SandboxVersionOp(this);
        AsyncOperationSystem.StartOperation(op);
        return op;
    }

    public AsyncOperationBase<PackageManifest?> LoadPackageManifestAsync(string version, IManifestDecryptor? manifestDecryptor)
    {
        var op = new SandboxManifestOp(this, version, manifestDecryptor ?? _manifestDecryptor);
        AsyncOperationSystem.StartOperation(op);
        return op;
    }

    public AsyncOperationBase<IBundleHandle?> LoadBundleAsync(BundleInfo bundle, IBundleDecryptor? decryptor)
    {
        if (_store == null) throw new InvalidOperationException("SandboxStorage not initialized; call InitializeStores first.");
        return _store.LoadBundleAsync(bundle, decryptor ?? _bundleDecryptor);
    }

    public SandboxBundleStore? Store => _store;
    public IRemoteService? Remote => _remote;

    public Task DownloadBundleAsync(BundleInfo bundle)
    {
        // Direct download path (without the downloader's queue/retry). Used by simple one-off fetches.
        if (_remote == null) throw new InvalidOperationException("No remote service configured.");
        if (_store == null) throw new InvalidOperationException("SandboxStorage not initialized.");
        return DownloadBundleInternalAsync(bundle);
    }

    private async Task DownloadBundleInternalAsync(BundleInfo bundle)
    {
        var urls = _remote!.GetRemoteUrls(bundle.FileName);
        var backend = new HttpClientDownloadBackend();
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                string tempPath = Path.Combine(_root, bundle.BundleGuid.ToString() + ".bundle.part");
                await backend.DownloadAsync(url, tempPath, progress: null, cancellation: default);
                await _store!.VerifyAndPromoteAsync(bundle, tempPath, default);
                return;
            }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new IOException($"Failed to download {bundle.BundleName}: no URLs available.");
    }

    public Task ClearCacheAsync() => _store?.ClearCacheAsync() ?? Task.CompletedTask;
    public void OnDestroy() { /* store handles its own file handles via the manager */ }

    // ----------------------------------------------------------------
    //  Inner ops
    // ----------------------------------------------------------------

    private sealed class SandboxVersionOp : AsyncOperationBase<string?>
    {
        private readonly SandboxStorage _owner;
        public SandboxVersionOp(SandboxStorage owner) { _owner = owner; }
        protected override void InternalStart() { }
        protected override void InternalUpdate()
        {
            // Prefer a locally-cached version file (from a previous RequestPackageVersion), else fall
            // back to the builtin baseline's version. The full remote version check is a future
            // enhancement (would block on HTTP here); for now the host is expected to have synced the
            // version file into the sandbox root before initializing.
            string localVersion = Path.Combine(_owner._root, PackageFileNames.VersionFile(_owner._packageName));
            if (File.Exists(localVersion)) Result = File.ReadAllText(localVersion).Trim();
            else if (_owner._builtinPackageRoot != null)
            {
                string builtinVersion = Path.Combine(_owner._builtinPackageRoot, PackageFileNames.VersionFile(_owner._packageName));
                Result = File.Exists(builtinVersion) ? File.ReadAllText(builtinVersion).Trim() : null;
            }
            SetResult();
        }
    }

    private sealed class SandboxManifestOp : AsyncOperationBase<PackageManifest?>
    {
        private readonly SandboxStorage _owner;
        private readonly string _version;
        private readonly IManifestDecryptor? _decryptor;
        public SandboxManifestOp(SandboxStorage owner, string version, IManifestDecryptor? decryptor)
        { _owner = owner; _version = version; _decryptor = decryptor; }
        protected override void InternalStart() { }
        protected override void InternalUpdate()
        {
            try
            {
                // Look in the sandbox first (a downloaded update), then the builtin baseline.
                string path = Path.Combine(_owner._root, PackageFileNames.ManifestFile(_owner._packageName, _version));
                if (!File.Exists(path) && _owner._builtinPackageRoot != null)
                    path = Path.Combine(_owner._builtinPackageRoot, PackageFileNames.ManifestFile(_owner._packageName, _version));
                if (!File.Exists(path)) { SetError($"Manifest not found for version {_version}."); return; }
                byte[] bytes = File.ReadAllBytes(path);
                if (_decryptor != null) bytes = _decryptor.Decrypt(bytes);
                Result = PackageManifestSerializer.Deserialize(bytes);
                SetResult();
            }
            catch (Exception ex) { SetError($"Failed to load manifest: {ex.Message}"); }
        }
    }
}
