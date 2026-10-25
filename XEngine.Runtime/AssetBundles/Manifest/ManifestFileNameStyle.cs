// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// How bundle file names are derived at build time. Mirrors YooAsset's EFileNameStyle. Determines
/// the on-disk filename stored in <see cref="BundleInfo.FileName"/>.
/// </summary>
public enum ManifestFileNameStyle : byte
{
    /// <summary>Hashable name: the bundle's FileHash hex string. Single flat namespace, no path info.</summary>
    HashName = 0,

    /// <summary>Lowercased bundle name without path separators (e.g. "grass.bundle").</summary>
    ShortName = 1,

    /// <summary>Full bundle name with path separators preserved (e.g. "assets/textures/grass.bundle").</summary>
    FullName = 2,

    /// <summary>BundleName with path separators replaced by underscores (web/flat-friendly).</summary>
    FlatName = 3,
}
