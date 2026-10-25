// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Metadata for one bundle within a <see cref="PackageManifest"/>. Mirrors YooAsset's PackageBundle
/// (renamed to avoid the ambiguous "Package" prefix and to align with XEngine naming).
/// </summary>
public sealed class BundleInfo
{
    /// <summary>Stable bundle name (e.g. "assets/textures/grass.bundle"). Used as the on-disk filename stem.</summary>
    public string BundleName { get; set; } = string.Empty;

    /// <summary>
    /// The bundle's file name on disk (derived from <see cref="BundleName"/> and the manifest's
    /// OutputNameStyle). The storage layer resolves this against its root path.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// A GUID identifying this bundle. Equals the bundle's content hash at build time (so it
    /// doubles as a cache key and a version key). Stored as a <see cref="BundleHash"/>.
    /// </summary>
    public BundleHash BundleGuid { get; set; }

    /// <summary>Content hash of the bundle file (FNV-128). Used for download verification.</summary>
    public BundleHash FileHash { get; set; }

    /// <summary>CRC32 of the bundle file. Used by the storage layer's verify step.</summary>
    public uint FileCrc { get; set; }

    /// <summary>Bundle file size in bytes.</summary>
    public long FileSize { get; set; }

    /// <summary>True if the bundle was encrypted at build time.</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>True if the bundle holds raw file assets (loaded via LoadBundleFile rather than LoadAsset).</summary>
    public bool IsRawFile { get; set; }

    /// <summary>
    /// Indices into <see cref="PackageManifest.BundleList"/> of this bundle's DIRECT dependencies.
    /// Transitive dependencies are resolved by <see cref="AssetBundleManifest.GetAllDependencies"/>.
    /// </summary>
    public int[] DependentBundleIds { get; set; } = Array.Empty<int>();

    /// <summary>User-assigned tags (for grouping downloads / eviction).</summary>
    public string[] Tags { get; set; } = Array.Empty<string>();

    // ---- Runtime-only (built on load by PackageManifest.BuildIndexes) ----

    /// <summary>Indices of bundles that directly depend on this one. Used by the loader to prevent
    /// unloading a bundle that a live bundle still references.</summary>
    public int[] ReferrerBundleIds { get; internal set; } = Array.Empty<int>();
}
