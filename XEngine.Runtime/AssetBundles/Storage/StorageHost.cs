// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Holds the ordered list of <see cref="IAssetStorage"/> for one package and routes each bundle to
/// the first storage that accepts it. The LAST storage in the list is the "main" storage - the one
/// used for version/manifest requests and downloads. This matches YooAsset's FileSystemHost model.
/// </summary>
public sealed class StorageHost
{
    private readonly List<IAssetStorage> _storages = new();

    public IReadOnlyList<IAssetStorage> Storages => _storages;

    /// <summary>The main storage (last in the list); used for version/manifest/download.</summary>
    public IAssetStorage MainStorage => _storages[^1];

    public void AddStorage(IAssetStorage storage)
    {
        if (storage == null) throw new ArgumentNullException(nameof(storage));
        _storages.Add(storage);
    }

    /// <summary>Find the storage that owns a bundle (first one whose CanAcceptBundle is true).</summary>
    public IAssetStorage? GetOwnerStorage(BundleInfo bundle)
    {
        foreach (var s in _storages)
            if (s.CanAcceptBundle(bundle)) return s;
        return null;
    }

    public AsyncOperationBase<IBundleHandle?> LoadBundleAsync(BundleInfo bundle, IBundleDecryptor? decryptor)
    {
        var owner = GetOwnerStorage(bundle);
        if (owner == null)
        {
            var op = new FailedHandleOp($"No storage accepts bundle '{bundle.BundleName}'.");
            AsyncOperationSystem.StartOperation(op);
            return op;
        }
        return owner.LoadBundleAsync(bundle, decryptor);
    }

    /// <summary>Build a factory suitable for passing to <see cref="AssetManager"/>: it resolves the
    /// owning storage for a bundle and asks it to load the bundle.</summary>
    public Func<BundleInfo, AsyncOperationBase<IBundleHandle>> CreateBundleHandleFactory(IBundleDecryptor? decryptor)
    {
        return bundle =>
        {
            var nullableOp = LoadBundleAsync(bundle, decryptor);
            // Wrap the IBundleHandle?-returning op into the IBundleHandle (non-null) shape the
            // BundleLoader expects; a null result is converted to a failure.
            var op = new NonNullHandleOp(nullableOp);
            AsyncOperationSystem.StartOperation(op);
            return op;
        };
    }

    public AsyncOperationBase<string?> RequestPackageVersionAsync() => MainStorage.RequestPackageVersionAsync();
    public AsyncOperationBase<PackageManifest?> LoadPackageManifestAsync(string version, IManifestDecryptor? decryptor)
        => MainStorage.LoadPackageManifestAsync(version, decryptor);

    public void OnDestroy()
    {
        foreach (var s in _storages) s.OnDestroy();
        _storages.Clear();
    }

    // ----------------------------------------------------------------
    //  Helper ops
    // ----------------------------------------------------------------

    private sealed class FailedHandleOp : AsyncOperationBase<IBundleHandle>
    {
        private readonly string _error;
        public FailedHandleOp(string error) { _error = error; }
        protected override void InternalStart() { }
        protected override void InternalUpdate() { SetError(_error); }
    }

    private sealed class NonNullHandleOp : AsyncOperationBase<IBundleHandle>
    {
        private readonly AsyncOperationBase<IBundleHandle?> _inner;
        public NonNullHandleOp(AsyncOperationBase<IBundleHandle?> inner) { _inner = inner; }
        protected override void InternalStart() { }
        protected override void InternalUpdate()
        {
            if (!_inner.IsDone)
            {
                if (IsWaitForCompletion) _inner.WaitForCompletion();
                else return;
            }
            if (_inner.Status == EOperationStatus.Failed) { SetError(_inner.Error ?? "bundle load failed"); return; }
            if (_inner.Result == null) { SetError("bundle load returned null handle"); return; }
            Result = _inner.Result;
            SetResult();
        }
    }
}

/// <summary>Placeholder for the manifest decryption interface (mirrors YooAsset's IManifestDecryptor).
/// Concrete implementations decrypt manifest bytes; the default is null (no decryption).</summary>
public interface IManifestDecryptor
{
    /// <summary>Decrypt raw manifest bytes. Return the decrypted bytes.</summary>
    byte[] Decrypt(byte[] encryptedBytes);
}
