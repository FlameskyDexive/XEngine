// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// A <see cref="IBundleHandle"/> backed by a real XEngine <see cref="AssetBundle"/>. This is the
/// normal player-side handle. It loads assets synchronously (XEngine's AssetBundle reads are
/// already async-safe on the AssetLoader background thread) and wraps each result in a fast
/// operation that completes on its first update tick.
/// </summary>
public sealed class XEngineBundleHandle : IBundleHandle
{
    private readonly AssetBundle _bundle;

    public XEngineBundleHandle(AssetBundle bundle, BundleInfo info)
    {
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        Bundle = info ?? throw new ArgumentNullException(nameof(info));
    }

    public BundleInfo Bundle { get; }
    public AssetBundle AssetBundle => _bundle;
    public bool IsLoaded => !_bundle.IsUnloaded;

    public AsyncOperationBase<EngineObject?> LoadAssetAsync(Guid assetGuid, Type type)
    {
        var op = new SyncAssetLoadOperation(_bundle, assetGuid, type);
        AsyncOperationSystem.StartOperation(op);
        return op;
    }

    public AsyncOperationBase<IReadOnlyList<EngineObject>> LoadAllAssetsAsync(Type type)
    {
        var op = new SyncAllAssetsLoadOperation(_bundle, type);
        AsyncOperationSystem.StartOperation(op);
        return op;
    }

    public void UnloadBundle(bool unloadAllLoadedObjects) => _bundle.Unload(unloadAllLoadedObjects);

    // ----------------------------------------------------------------
    //  Internal operations - XEngine's AssetBundle loads synchronously
    //  (the AssetLoader background thread already provides asynchrony),
    //  so these complete on their first Update tick.
    // ----------------------------------------------------------------

    private sealed class SyncAssetLoadOperation : AsyncOperationBase<EngineObject?>
    {
        private readonly AssetBundle _bundle;
        private readonly Guid _guid;
        private readonly Type _type;

        public SyncAssetLoadOperation(AssetBundle bundle, Guid guid, Type type)
        {
            _bundle = bundle; _guid = guid; _type = type;
        }

        protected override void InternalStart() { /* no work until Update */ }

        protected override void InternalUpdate()
        {
            try
            {
                Result = _bundle.LoadAsset(_guid, _type);
                SetResult();
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
            }
        }
    }

    private sealed class SyncAllAssetsLoadOperation : AsyncOperationBase<IReadOnlyList<EngineObject>>
    {
        private readonly AssetBundle _bundle;
        private readonly Type _type;

        public SyncAllAssetsLoadOperation(AssetBundle bundle, Type type)
        {
            _bundle = bundle; _type = type;
        }

        protected override void InternalStart() { }

        protected override void InternalUpdate()
        {
            try
            {
                // LoadAllAssets<T> is generic; use reflection-free path via LoadAsset on every GUID.
                var list = new List<EngineObject>();
                foreach (var guid in _bundle.GetAllAssetGuids())
                {
                    var obj = _bundle.LoadAsset(guid, _type);
                    if (obj != null) list.Add(obj);
                }
                Result = list;
                SetResult();
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
            }
        }
    }
}
