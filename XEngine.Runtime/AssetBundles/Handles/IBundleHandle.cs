// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// IBundleHandle - the abstraction over "a loaded bundle you can pull assets out of". The resource
// layer (BundleLoader/AssetProvider) talks ONLY to this interface; the concrete implementations
// bridge to either a real XEngine AssetBundle (XEngineBundleHandle) or to the editor's asset
// database directly (VirtualBundleHandle, for EditorSimulate mode).
//
// This is the single porting seam between the Unity-AssetBundle world YooAsset was written for and
// XEngine: replace YooAsset's AssetBundleHandle (which calls Unity's AssetBundle.LoadFromFileAsync)
// with XEngineBundleHandle (which calls XEngine's AssetBundle.LoadFromFile). Everything above this
// interface is engine-agnostic.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// A loaded bundle that can load individual assets. Owned by a <see cref="BundleLoader"/>; the
/// loader calls <see cref="LoadAssetAsync"/> etc. to fulfil provider requests, and
/// <see cref="UnloadBundle"/> when the bundle's refcount drops to zero.
/// </summary>
public interface IBundleHandle
{
    /// <summary>The bundle this handle wraps.</summary>
    BundleInfo Bundle { get; }

    /// <summary>True once the bundle file has been opened/parsed and assets can be requested.</summary>
    bool IsLoaded { get; }

    /// <summary>Load a single asset by GUID, asynchronously. The operation's result is the
    /// deserialized <see cref="EngineObject"/> (or null on failure).</summary>
    AsyncOperationBase<EngineObject?> LoadAssetAsync(Guid assetGuid, Type type);

    /// <summary>Load all assets in the bundle asynchronously.</summary>
    AsyncOperationBase<IReadOnlyList<EngineObject>> LoadAllAssetsAsync(Type type);

    /// <summary>Release the bundle. After this, no further loads are possible.</summary>
    void UnloadBundle(bool unloadAllLoadedObjects);
}
