// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.AssetBundles;

/// <summary>Constants for the PackageManifest binary format.</summary>
public static class PackageManifestConsts
{
    /// <summary>File magic "XEPK" (XEngine Package) little-endian = 0x4B504558.</summary>
    public const uint FileMagic = 0x4B504558u;

    /// <summary>Current manifest binary format version.</summary>
    public const int FileVersion = 1;

    /// <summary>Reasonable maximum manifest size (sanity bound on the magic read).</summary>
    public const long MaxFileSize = 100L * 1024 * 1024;

    /// <summary>Sanity minimum: a manifest smaller than this cannot be valid.</summary>
    public const long MinFileSize = 16;
}
