// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Resolved descriptor for an asset load request: the manifest entry, the requested .NET type,
/// and the addressable key the caller used. Passed to providers by <see cref="AssetManager"/>.
/// </summary>
public sealed class AssetLoadInfo
{
    /// <summary>The manifest asset being loaded.</summary>
    public ManifestAsset Asset { get; init; } = null!;

    /// <summary>The requested asset type (for type-filtered loads).</summary>
    public Type AssetType { get; init; } = null!;

    /// <summary>The address the caller used (location string). For diagnostics and awaiter keys.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>The asset's GUID. Equal to <see cref="ManifestAsset.AssetGuid"/>.</summary>
    public Guid AssetGuid => Asset.AssetGuid;
}
