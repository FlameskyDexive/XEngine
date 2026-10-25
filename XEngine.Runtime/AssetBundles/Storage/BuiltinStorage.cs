// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Threading.Tasks;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Offline / Host-baseline storage strategy: serves bundles shipped inside the app package via a
/// <see cref="BuiltinBundleStore"/>. The package version comes from a catalog file in the root (no
/// remote request); the manifest is loaded from a file named per <see cref="PackageFileNames"/>.
/// </summary>
public sealed class BuiltinStorage : IAssetStorage
{
    private string _packageName = string.Empty;
    private string _root = string.Empty;
    private BuiltinBundleStore? _store;
    private IBundleDecryptor? _bundleDecryptor;
    private IManifestDecryptor? _manifestDecryptor;
    private string? _cachedVersion;

    public string Name => "builtin";
    public string PackageName => _packageName;

    public Task OnCreateAsync(string packageName, string packageRoot)
    {
        _packageName = packageName ?? throw new ArgumentNullException(nameof(packageName));
        _root = packageRoot ?? throw new ArgumentNullException(nameof(packageRoot));
        return Task.CompletedTask;
    }

    /// <summary>Configure with parameters resolved from <see cref="StorageParameters"/> by the host.</summary>
    public void Configure(IBundleDecryptor? bundleDecryptor, IManifestDecryptor? manifestDecryptor, string? cachedVersion = null)
    {
        _bundleDecryptor = bundleDecryptor;
        _manifestDecryptor = manifestDecryptor;
        _cachedVersion = cachedVersion;
    }

    public bool CanAcceptBundle(BundleInfo bundle) => _store?.IsCached(bundle.BundleGuid) ?? false;
    public bool IsDownloadRequired(BundleInfo bundle) => false; // builtin never downloads

    public async Task InitializeStores(AssetBundlePlatform platform)
    {
        _store = new BuiltinBundleStore(_root, platform, _bundleDecryptor);
        await _store.InitializeAsync();
    }

    public AsyncOperationBase<string?> RequestPackageVersionAsync()
    {
        var op = new VersionOp(this);
        AsyncOperationSystem.StartOperation(op);
        return op;
    }

    public AsyncOperationBase<PackageManifest?> LoadPackageManifestAsync(string version, IManifestDecryptor? manifestDecryptor)
    {
        var op = new ManifestOp(this, version, manifestDecryptor ?? _manifestDecryptor);
        AsyncOperationSystem.StartOperation(op);
        return op;
    }

    public AsyncOperationBase<IBundleHandle?> LoadBundleAsync(BundleInfo bundle, IBundleDecryptor? decryptor)
    {
        if (_store == null) throw new InvalidOperationException("BuiltinStorage not initialized; call InitializeStores first.");
        return _store.LoadBundleAsync(bundle, decryptor ?? _bundleDecryptor);
    }

    public Task DownloadBundleAsync(BundleInfo bundle)
        => throw new InvalidOperationException("BuiltinStorage does not download bundles.");

    public Task ClearCacheAsync() => Task.CompletedTask;
    public void OnDestroy() { /* store holds open bundle files that the manager closes; nothing here */ }

    // ----------------------------------------------------------------
    //  Inner ops
    // ----------------------------------------------------------------

    private sealed class VersionOp : AsyncOperationBase<string?>
    {
        private readonly BuiltinStorage _owner;
        public VersionOp(BuiltinStorage owner) { _owner = owner; }
        protected override void InternalStart() { }
        protected override void InternalUpdate()
        {
            // Read the .version file in the builtin root (written by the build pipeline). If absent,
            // fall back to the configured cached version, then to a constant.
            string versionPath = Path.Combine(_owner._root, PackageFileNames.VersionFile(_owner._packageName));
            if (File.Exists(versionPath)) Result = File.ReadAllText(versionPath).Trim();
            else Result = _owner._cachedVersion ?? "1.0.0";
            SetResult();
        }
    }

    private sealed class ManifestOp : AsyncOperationBase<PackageManifest?>
    {
        private readonly BuiltinStorage _owner;
        private readonly string _version;
        private readonly IManifestDecryptor? _decryptor;
        public ManifestOp(BuiltinStorage owner, string version, IManifestDecryptor? decryptor)
        { _owner = owner; _version = version; _decryptor = decryptor; }
        protected override void InternalStart() { }
        protected override void InternalUpdate()
        {
            try
            {
                string path = Path.Combine(_owner._root, PackageFileNames.ManifestFile(_owner._packageName, _version));
                if (!File.Exists(path)) { SetError($"Manifest not found: {path}"); return; }
                byte[] bytes = File.ReadAllBytes(path);
                if (_decryptor != null) bytes = _decryptor.Decrypt(bytes);
                Result = PackageManifestSerializer.Deserialize(bytes);
                SetResult();
            }
            catch (Exception ex) { SetError($"Failed to load manifest: {ex.Message}"); }
        }
    }
}

/// <summary>Canonical file names written by the build pipeline and read by the storages.</summary>
public static class PackageFileNames
{
    public static string ManifestFile(string packageName, string version) => $"{packageName}_{version}.bytes";
    public static string HashFile(string packageName, string version) => $"{packageName}_{version}.hash";
    public static string VersionFile(string packageName) => $"{packageName}.version";
    public static string ReportFile(string packageName, string version) => $"{packageName}_{version}.report";
}
