// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// BundleLoader (Tier B) - one per bundle, shared by every provider that needs that bundle. Owns the
// reference count and the bundle's IBundleHandle. The flow: Reference() increments RefCount and, if
// this is the first reference, enqueues the load; once the handle is ready, providers can pull assets
// from it; Release() decrements and, when RefCount hits zero AND no referrer bundle is still live,
// unloads the handle.
//
// Independent rewrite of YooAsset's LoadBundleOperation (Apache-2.0). State-machine shape
// (CheckConcurrency -> LoadBundleFile -> Done) and refcount semantics match that model; the bundle-
// file load is delegated to a factory function injected by the Storage layer (Stage C).

using System;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Reference-counted loader for a single bundle. Created and pooled by <see cref="AssetManager"/>.
/// </summary>
internal sealed class BundleLoader : AsyncOperationBase
{
    private enum ESteps : byte { None, CheckConcurrency, LoadBundleFile, Done }

    private readonly AssetManager _manager;
    private readonly Func<BundleLoader, AsyncOperationBase<IBundleHandle>> _bundleHandleFactory;

    private ESteps _steps = ESteps.None;
    private AsyncOperationBase<IBundleHandle>? _loadOp;
    private bool _forceDestroy;

    /// <summary>The bundle metadata.</summary>
    public BundleInfo BundleInfo { get; }

    /// <summary>The handle once the bundle is loaded. Null while loading or after unload.</summary>
    public IBundleHandle? BundleHandle { get; private set; }

    /// <summary>Number of live providers referencing this bundle.</summary>
    public int RefCount { get; private set; }

    /// <summary>True after Destroy() has been called by the manager.</summary>
    public bool IsDestroyed { get; private set; }

    public bool IsBundleLoaded => BundleHandle != null && BundleHandle.IsLoaded;

    public BundleLoader(AssetManager manager, BundleInfo bundleInfo, Func<BundleLoader, AsyncOperationBase<IBundleHandle>> bundleHandleFactory)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        BundleInfo = bundleInfo ?? throw new ArgumentNullException(nameof(bundleInfo));
        _bundleHandleFactory = bundleHandleFactory ?? throw new ArgumentNullException(nameof(bundleHandleFactory));
    }

    // ---- Refcount API ----

    /// <summary>Add a reference. Starts the load on the first reference.</summary>
    public void Reference()
    {
        RefCount++;
        if (RefCount == 1 && _steps == ESteps.None && !IsDone)
        {
            AsyncOperationSystem.StartOperation(this);
        }
    }

    /// <summary>Drop a reference. When it reaches zero, the manager is notified so it can release
    /// the handle when safe.</summary>
    public void Release()
    {
        if (RefCount <= 0) return;
        RefCount--;
        if (RefCount == 0) _manager.TryDestroyBundle(this);
    }

    /// <summary>Mark for destruction during package teardown, and unload the bundle handle.
    /// This is the ONLY place the handle is unloaded: completion (SetResult) does NOT release it -
    /// the bundle stays resident in memory until the manager decides no providers need it anymore
    /// (refcount zero, no live referrers).</summary>
    public void Destroy()
    {
        if (IsDestroyed) return;
        IsDestroyed = true;
        _forceDestroy = true;

        // unloadAllLoadedObjects = true: by the time the manager destroys a loader, the asset handles
        // have already been released, so any residual loaded objects should go with the bundle.
        BundleHandle?.UnloadBundle(unloadAllLoadedObjects: true);
        BundleHandle = null;
    }

    // ---- State machine ----

    protected override void InternalStart()
    {
        _steps = ESteps.CheckConcurrency;
    }

    protected override void InternalUpdate()
    {
        if (_steps == ESteps.None || _steps == ESteps.Done) return;

        if (_steps == ESteps.CheckConcurrency)
        {
            // During sync wait, skip the concurrency throttle so the op completes this frame.
            if (!IsWaitForCompletion && _manager.IsBundleLoadingBusy()) return;
            _steps = ESteps.LoadBundleFile;
        }

        if (_steps == ESteps.LoadBundleFile)
        {
            if (_loadOp == null)
            {
                _manager.IncrementBundleLoadingCounter();
                _loadOp = _bundleHandleFactory(this);
                if (_loadOp == null)
                {
                    _steps = ESteps.Done;
                    _manager.DecrementBundleLoadingCounter();
                    SetError($"Bundle handle factory returned null for '{BundleInfo.BundleName}'.");
                    return;
                }
                AsyncOperationSystem.StartOperation(_loadOp);
            }

            if (IsWaitForCompletion) _loadOp.WaitForCompletion();

            if (!_loadOp.IsDone) return;

            _manager.DecrementBundleLoadingCounter();

            if (_loadOp.Status == EOperationStatus.Failed)
            {
                _steps = ESteps.Done;
                BundleHandle = null;
                SetError(_loadOp.Error ?? $"Failed to load bundle '{BundleInfo.BundleName}'.");
                return;
            }

            BundleHandle = _loadOp.Result;
            _steps = ESteps.Done;
            if (BundleHandle == null) SetError($"Bundle '{BundleInfo.BundleName}' loaded with a null handle.");
            else SetResult();
        }
    }

    protected override void InternalDispose()
    {
        // The bundle handle is unloaded in Destroy(), NOT here. InternalDispose runs on operation
        // completion (file loaded) which is not the same as "no longer needed" - the handle must
        // stay resident until the manager destroys this loader (refcount zero). Releasing here would
        // tear down bundles the moment their file finished loading, before any provider could read them.
    }

    /// <summary>True if this loader can be safely unloaded: no references, fully loaded (or failed),
    /// and no bundle that depends on this one is still live.</summary>
    public bool CanDestroy()
    {
        if (IsDone == false) return false;
        if (RefCount > 0) return false;
        // Check referrer: if any bundle that depends on us is still loaded, keep us alive.
        foreach (int referrerId in BundleInfo.ReferrerBundleIds)
        {
            if (_manager.IsBundleLoaded(referrerId)) return false;
        }
        return true;
    }
}
