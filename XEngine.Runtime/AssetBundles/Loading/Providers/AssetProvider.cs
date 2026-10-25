// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Provider that loads a single asset from its bundle. Started by <see cref="AssetManager"/>; once
/// the bundle handle is ready, issues <see cref="IBundleHandle.LoadAssetAsync"/> and stores the
/// result for <see cref="AssetHandle.AssetObject"/>.
/// </summary>
internal sealed class AssetProvider : AssetProviderBase
{
    private AsyncOperationBase<EngineObject?>? _loadAssetOp;

    /// <summary>The loaded asset (null until the bundle+asset are both loaded).</summary>
    public EngineObject? AssetResult { get; private set; }

    public AssetProvider(AssetManager manager, string providerKey, AssetLoadInfo info) : base(manager, providerKey, info) { }

    protected override void ProcessBundleHandle()
    {
        var handle = LoadedBundleHandle;
        if (handle == null) { SetError("Bundle handle was null after load."); return; }

        if (_loadAssetOp == null)
        {
            _loadAssetOp = handle.LoadAssetAsync(Info.AssetGuid, Info.AssetType);
        }

        if (!_loadAssetOp.IsDone)
        {
            if (IsWaitForCompletion) _loadAssetOp.WaitForCompletion();
            else return;
        }

        if (_loadAssetOp.Status == EOperationStatus.Failed)
        {
            SetError(_loadAssetOp.Error ?? "asset load failed");
            return;
        }

        AssetResult = _loadAssetOp.Result;
        SetResult();
    }

    /// <summary>Create the typed handle handed to the caller.</summary>
    public AssetHandle CreateHandle() => CreateHandle<AssetHandle>(p => new AssetHandle((AssetProvider)p));
}
