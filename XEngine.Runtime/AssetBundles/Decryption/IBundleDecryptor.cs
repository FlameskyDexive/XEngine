// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// Bundle decryption interfaces. A storage can be configured with a decryptor; the bundle handle
// dispatches on the sub-interface the decryptor implements to pick the cheapest load path:
//   - IBundleOffsetDecryptor : cheapest, "junk header" encryption -> load from offset
//   - IBundleMemoryDecryptor : fully decrypt in memory -> load from bytes (also the fallback)
//   - IBundleStreamDecryptor : wrap the file stream -> load from stream (good for large files)
//
// Independent rewrite informed by YooAsset's IBundleDecryptor family (Apache-2.0). Interface shape
// matches; XEngine's XEngineBundleHandle is the single dispatch point (the only place these are
// consumed), mirroring YooAsset's LoadLocalAssetBundleOperation.

using System;
using System.IO;

namespace XEngine.Runtime.AssetBundles;

/// <summary>Arguments passed to every decrypt call.</summary>
public readonly struct BundleDecryptArgs
{
    /// <summary>The bundle being decrypted (for context: name, guid, size).</summary>
    public readonly BundleInfo Bundle;

    /// <summary>The on-disk path of the (still encrypted) bundle file.</summary>
    public readonly string FilePath;

    /// <summary>For memory decryption: the file's raw bytes. Null for offset/stream decryption.</summary>
    public readonly byte[]? FileData;

    public BundleDecryptArgs(BundleInfo bundle, string filePath, byte[]? fileData)
    {
        Bundle = bundle;
        FilePath = filePath;
        FileData = fileData;
    }
}

/// <summary>Marker base for bundle decryptors. Implement one of the sub-interfaces.</summary>
public interface IBundleDecryptor { }

/// <summary>
/// Decryptor that reveals the real bundle starts at a fixed offset (e.g. a junk header was prepended
/// at build time). Cheapest path: the loader opens the file and skips the offset. Implement when the
/// encryption scheme is a prefix the engine can simply seek past.
/// </summary>
public interface IBundleOffsetDecryptor : IBundleDecryptor
{
    /// <summary>The byte offset at which the real bundle begins in <see cref="BundleDecryptArgs.FilePath"/>.</summary>
    long GetFileOffset(BundleDecryptArgs args);
}

/// <summary>
/// Decryptor that produces the full decrypted bytes in memory. Most expensive (whole-file buffer),
/// but universally applicable. Used as the fallback when the platform's streaming load fails.
/// </summary>
public interface IBundleMemoryDecryptor : IBundleDecryptor
{
    /// <summary>Return the fully decrypted bundle bytes.</summary>
    byte[] GetDecryptedData(BundleDecryptArgs args);
}

/// <summary>
/// Decryptor that wraps the file in a decrypting Stream. Good for large bundles where a full in-memory
/// copy is undesirable. The loader reads the bundle through this stream.
/// </summary>
public interface IBundleStreamDecryptor : IBundleDecryptor
{
    /// <summary>Buffer size hint for the decrypting stream (e.g. 64 KiB).</summary>
    int GetBufferSize(BundleDecryptArgs args);
    /// <summary>Create a Stream that decrypts the bundle on the fly as it is read.</summary>
    Stream CreateDecryptionStream(BundleDecryptArgs args);
}
