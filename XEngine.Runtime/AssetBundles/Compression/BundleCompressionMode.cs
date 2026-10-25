// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Compression algorithm stored in an AssetBundle's container header. The header records this byte
/// so the reader knows how to decompress the data region. <see cref="Lz4Block"/> is the default for
/// new bundles; <see cref="None"/> is used for bundles that must stream without decompression cost
/// (e.g. very large scene bundles) and is the fallback when LZ4 encoding fails.
/// </summary>
public enum BundleCompressionMode : byte
{
    /// <summary>No compression. Payload bytes are stored verbatim. Fastest load, largest disk size.</summary>
    None = 0,

    /// <summary>
    /// LZ4 block compression (via K4os.Compression.LZ4). Decompression is extremely fast and the
    /// container stores a block table so individual blocks can be decompressed on demand for
    /// streaming. This is the default for new bundles.
    /// </summary>
    Lz4Block = 1,
}
