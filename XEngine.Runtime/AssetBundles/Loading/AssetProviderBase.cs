// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// AssetProviderBase (Tier A) - one per logical load (keyed by asset key). Owns the per-load
// reference count and the set of handles handed out. It references its main bundle loader plus any
// dependency bundle loaders (incrementing their refcounts), waits for them to finish loading, then
// pulls the asset out of the bundle handle and hands it back via an AssetHandle.
//
// Independent rewrite of YooAsset's ProviderBase (Apache-2.0). The state machine
// (StartBundleLoader -> WaitBundleLoader -> ProcessBundleHandle -> Done) and the refcount model
// (CreateHandle increments ProviderBase.RefCount; Release decrements; zero triggers auto-unload)
// match that design.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Base class for the per-load provider. Created and pooled by <see cref="AssetManager"/>.
/// Subclasses implement <see cref="ProcessBundleHandle"/> to actually pull assets out of the bundle
/// handle once it is loaded.
/// </summary>
internal abstract class AssetProviderBase : AsyncOperationBase
{
    private enum ESteps : byte { None, StartBundleLoader, WaitBundleLoader, ProcessBundleHandle, Done }

    private readonly AssetManager _manager;
    private readonly List<BundleLoader> _bundleLoaders = new(4);
    private readonly BundleLoader? _mainBundleLoader;
    private readonly HashSet<AssetHandleBase> _handles = new();
    private ESteps _steps = ESteps.None;

    /// <summary>The load descriptor.</summary>
    public AssetLoadInfo Info { get; }

    /// <summary>Unique key under which this provider is pooled (asset GUID + type, typically).</summary>
    public string ProviderKey { get; }

    /// <summary>Number of live handles referencing this provider.</summary>
    public int RefCount { get; private set; }

    public bool IsDestroyed { get; private set; }

    /// <summary>The loaded bundle handle (set once the main loader completes).</summary>
    protected IBundleHandle? LoadedBundleHandle { get; private set; }

    protected AssetProviderBase(AssetManager manager, string providerKey, AssetLoadInfo info)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        Info = info ?? throw new ArgumentNullException(nameof(info));
        ProviderKey = providerKey;

        // Reference the main bundle loader and every dependency loader. This MUST happen in the
        // constructor (not InternalStart) so shared loaders see correct refcounts even before the
        // provider runs - otherwise the bundle could be unloaded while we still need it.
        //
        // The asset's OWN bundle is the one whose GUID matches manifest.BundleList[info.Asset.BundleId].
        // GetAllAssetDependencies returns post-order (deps first, the asset's bundle LAST), so we
        // must look the main loader up explicitly - _bundleLoaders[0] would be a dependency.
        var loaders = _manager.GetOrCreateBundleLoaders(info.Asset);
        _bundleLoaders.AddRange(loaders);
        var mainBundleInfo = manager.GetBundleInfo(info.Asset.BundleId);
        foreach (var loader in _bundleLoaders)
        {
            if (loader.BundleInfo.BundleGuid == mainBundleInfo.BundleGuid) _mainBundleLoader = loader;
            loader.Reference();
        }
    }

    // ---- Refcount / handle API ----

    /// <summary>Create (or reuse) a typed handle for this load. The provider's refcount increments
    /// per live handle; the manager pools the provider so a second LoadAssetAsync of the same key
    /// returns the same provider and a new handle sharing the single underlying load.</summary>
    public THandle CreateHandle<THandle>(Func<AssetProviderBase, THandle> factory) where THandle : AssetHandleBase
    {
        RefCount++;
        var handle = factory(this);
        _handles.Add(handle);
        return handle;
    }

    /// <summary>Called by <see cref="AssetHandleBase.Release"/>. When refcount hits zero, optionally
    /// trigger auto-unload of the bundle, then notify the manager so it can drop the provider.</summary>
    public void ReleaseHandle(AssetHandleBase handle)
    {
        _handles.Remove(handle);
        if (RefCount > 0) RefCount--;
        if (RefCount == 0) _manager.OnProviderIdle(this);
    }

    /// <summary>Force-destroy during package teardown. This is the ONLY place bundle loader
    /// references are released: operation completion (InternalDispose) must NOT release them,
    /// because the provider stays alive in the pool to serve handle reads after its load op
    /// finishes. Releasing on completion would tear down the bundle while handles still reference
    /// the loaded asset.</summary>
    public void Destroy()
    {
        if (IsDestroyed) return;
        IsDestroyed = true;

        foreach (var loader in _bundleLoaders) loader.Release();
        _bundleLoaders.Clear();
        _handles.Clear();
    }

    // ---- State machine ----

    protected override void InternalStart() => _steps = ESteps.StartBundleLoader;

    protected override void InternalUpdate()
    {
        if (_steps == ESteps.None || _steps == ESteps.Done) return;

        if (_steps == ESteps.StartBundleLoader)
        {
            _steps = ESteps.WaitBundleLoader;
        }

        if (_steps == ESteps.WaitBundleLoader)
        {
            // Wait for every bundle loader (main + deps) to complete.
            foreach (var loader in _bundleLoaders)
            {
                if (!loader.IsDone)
                {
                    if (IsWaitForCompletion) loader.WaitForCompletion();
                    else return;
                }
                if (loader.Status == EOperationStatus.Failed)
                {
                    _steps = ESteps.Done;
                    SetError($"Dependency bundle failed to load: {loader.Error}");
                    return;
                }
            }
            // All loaders done: take the MAIN bundle's handle (the asset's own bundle, not a dep).
            LoadedBundleHandle = _mainBundleLoader?.BundleHandle;
            _steps = ESteps.ProcessBundleHandle;
        }

        if (_steps == ESteps.ProcessBundleHandle)
        {
            try
            {
                ProcessBundleHandle();
            }
            catch (Exception ex)
            {
                _steps = ESteps.Done;
                SetError(ex.Message);
                return;
            }
        }
    }

    /// <summary>Subclass hook: pull the asset(s) out of <see cref="LoadedBundleHandle"/>, set the
    /// operation result, and call SetResult/SetError. Called once after the bundle is loaded.</summary>
    protected abstract void ProcessBundleHandle();

    protected override void InternalDispose()
    {
        // NOTE: do NOT release bundle loader references here. InternalDispose runs when the load
        // OPERATION completes, but the provider stays resident in the manager's pool to serve handle
        // reads afterward. Loader references are released in Destroy(), which the manager calls when
        // the provider is actually being retired (all handles released, or package teardown).
    }

    /// <summary>True if the provider is done and has no live handles, so the manager can pool it.</summary>
    public bool IsIdle => IsDone && RefCount == 0;
}
