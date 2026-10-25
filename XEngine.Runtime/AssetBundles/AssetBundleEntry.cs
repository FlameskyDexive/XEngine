// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// One asset stored inside an <see cref="AssetBundle"/>. The index entries form a seek table the
/// loader uses to locate a single asset's Echo payload without reading the whole bundle.
/// </summary>
public readonly struct AssetBundleEntry
{
    /// <summary>The asset's project GUID. This is the canonical key the rest of the engine uses.</summary>
    public readonly Guid AssetGuid;

    /// <summary>Human-readable name (lowercased on disk for case-insensitive lookup). May be empty.</summary>
    public readonly string Name;

    /// <summary>Full name of the asset's concrete .NET type (e.g. "XEngine.Runtime.Resources.Texture2D").
    /// Used by Contains(type) lookups and as a fallback when Echo's $type is absent.</summary>
    public readonly string TypeName;

    /// <summary>Byte offset of this entry's payload within the bundle file. For LZ4 bundles this is
    /// the offset into the *decompressed* data region - the loader resolves it through the block table.</summary>
    public readonly long DataOffset;

    /// <summary>Length of this entry's payload in bytes.</summary>
    public readonly long DataLength;

    public AssetBundleEntry(Guid assetGuid, string name, string typeName, long dataOffset, long dataLength)
    {
        AssetGuid = assetGuid;
        Name = name;
        TypeName = typeName;
        DataOffset = dataOffset;
        DataLength = dataLength;
    }
}
