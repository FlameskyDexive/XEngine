// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// IAssetStorage - the storage-strategy abstraction that sits ABOVE IBundleStore. A storage owns one
// or more stores and implements the high-level flows the resource layer needs: getting the package
// version, loading the manifest, fetching a bundle (deciding which store owns it, downloading if
// necessary). BuiltinStorage, SandboxStorage, EditorStorage, and WebStorage are the concrete
// strategies; AssetPlayMode selects a combination of them.
//
// Independent rewrite of YooAsset's IFileSystem (Apache-2.0). Method set mirrors that interface.

using System.Threading.Tasks;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// A storage strategy: knows how to get the package version, manifest, and bundles, possibly using
/// one or more <see cref="IBundleStore"/> instances internally. <see cref="StorageHost"/> holds an
/// ordered list of these (the last is the "main" one) and routes each bundle to the first storage
/// that accepts it.
/// </summary>
public interface IAssetStorage
{
    /// <summary>Human-readable name for diagnostics.</summary>
    string Name { get; }

    /// <summary>The package this storage serves.</summary>
    string PackageName { get; }

    /// <summary>Initialize for the given package. <paramref name="packageRoot"/> is the storage's
    /// root directory (e.g. StreamingAssetsPath/Bundles for builtin, PersistentDataPath/xengine/pkg
    /// for sandbox).</summary>
    Task OnCreateAsync(string packageName, string packageRoot);

    /// <summary>True if this storage can serve the given bundle (e.g. builtin storage says yes for
    /// bundles shipped in the package; sandbox says yes for downloaded bundles).</summary>
    bool CanAcceptBundle(BundleInfo bundle);

    /// <summary>True if the bundle is missing locally and must be downloaded before it can be loaded.</summary>
    bool IsDownloadRequired(BundleInfo bundle);

    /// <summary>Request the current package version (e.g. from a remote version endpoint, or the
    /// builtin catalog for offline mode). Returns null if unavailable.</summary>
    AsyncOperationBase<string?> RequestPackageVersionAsync();

    /// <summary>Load the manifest for the given version. Returns null if not found.</summary>
    AsyncOperationBase<PackageManifest?> LoadPackageManifestAsync(string version, IManifestDecryptor? manifestDecryptor);

    /// <summary>Load (and if necessary, download) a bundle, returning an IBundleHandle.</summary>
    AsyncOperationBase<IBundleHandle?> LoadBundleAsync(BundleInfo bundle, IBundleDecryptor? decryptor);

    /// <summary>Download a bundle's bytes into this storage (used by the downloader).</summary>
    Task DownloadBundleAsync(BundleInfo bundle);

    /// <summary>Clear all cached state.</summary>
    Task ClearCacheAsync();

    /// <summary>Tear down (close handles, dispose stores).</summary>
    void OnDestroy();
}
