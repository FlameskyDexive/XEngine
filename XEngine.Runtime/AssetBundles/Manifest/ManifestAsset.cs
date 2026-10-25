// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// One asset entry in a <see cref="PackageManifest"/>. Named <c>ManifestAsset</c> (rather than
/// PackageAsset/AssetEntry) to avoid collision with the editor's <c>AssetEntry</c> database record.
/// </summary>
public sealed class ManifestAsset
{
    /// <summary>Addressable address. Equal to <see cref="AssetPath"/> when addressables are disabled.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Original source asset path (e.g. "Assets/Textures/Grass.png").</summary>
    public string AssetPath { get; set; } = string.Empty;

    /// <summary>The asset's project GUID - the canonical runtime key.</summary>
    public Guid AssetGuid { get; set; }

    /// <summary>Index into <see cref="PackageManifest.BundleList"/> of the bundle containing this asset.</summary>
    public int BundleId { get; set; }

    /// <summary>Indices into <see cref="PackageManifest.BundleList"/> of additional bundles this asset
    /// needs loaded (besides its own). Usually empty; populated when an asset spans bundles.</summary>
    public int[] DependentBundleIds { get; set; } = Array.Empty<int>();

    /// <summary>User-assigned tags.</summary>
    public string[] Tags { get; set; } = Array.Empty<string>();
}
