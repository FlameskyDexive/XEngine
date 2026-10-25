// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Handle for a single-asset load. Obtained from <see cref="AssetPackage.LoadAssetAsync{T}"/>.
/// Read <see cref="AssetObject"/> after <see cref="IsDone"/>; call <see cref="Release"/> when done.
/// </summary>
public sealed class AssetHandle : AssetHandleBase
{
    private readonly AssetProvider _provider;

    internal AssetHandle(AssetProvider provider) : base(provider) => _provider = provider;

    /// <summary>The loaded asset, or null if the load failed or has not finished.</summary>
    public EngineObject? AssetObject => _provider.AssetResult;

    /// <summary>Get the loaded asset typed as <typeparamref name="T"/>. Returns null on type mismatch.</summary>
    public T? GetAssetObject<T>() where T : EngineObject => _provider.AssetResult as T;

    /// <summary>The asset's GUID.</summary>
    public Guid AssetGuid => _provider.Info.AssetGuid;
}
