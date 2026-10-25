// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using System;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Build-time options for writing a single <see cref="AssetBundle"/> via <see cref="AssetBundleWriter"/>.
/// The runtime writer is shared by the editor builder and by tests; the editor's higher-level
/// AssetBundleBuilder (collector -> bundle assignment) sits on top of this.
/// </summary>
public sealed class AssetBundleBuildOptions
{
    /// <summary>Target platform. Recorded in the header so the loader can reject mismatches.</summary>
    public AssetBundlePlatform Platform { get; set; } = AssetBundlePlatform.Any;

    /// <summary>Compression mode. Defaults to LZ4 for new bundles.</summary>
    public BundleCompressionMode Compression { get; set; } = BundleCompressionMode.Lz4Block;

    /// <summary>LZ4 block size. Ignored when <see cref="Compression"/> is None.</summary>
    public int Lz4BlockSize { get; set; } = BundleCompression.DefaultBlockSize;

    /// <summary>Optional build-time payload rewrite (for example Android texture variants).</summary>
    public Func<Guid, byte[], byte[]>? PayloadTransform { get; set; }

    /// <summary>GUID of the bundle's main asset, or <see cref="System.Guid.Empty"/> for none.</summary>
    public System.Guid MainAssetGuid { get; set; }

    /// <summary>Direct dependency bundle GUIDs this bundle references (for the runtime dependency resolver).</summary>
    public List<System.Guid> DirectDependencyBundleIds { get; } = new();
}
