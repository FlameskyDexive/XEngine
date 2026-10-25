// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// Two sample IBundleDecryptor implementations that ship with the engine. Real games typically write
// their own (the decryption logic is deliberately game-specific); these cover the two cheapest
// schemes and serve as both reference and test fixtures.

using System;
using System.IO;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// No-op decryptor: bundles are stored unencrypted. This is the default when no decryptor is
/// configured; <see cref="BuiltinBundleStore"/> / <see cref="SandboxBundleStore"/> treat it as
/// "no decryption needed" by checking the sub-interface type (a raw IBundleDecryptor with no
/// sub-interface triggers the plain AssetBundle.LoadFromFile path).
/// </summary>
public sealed class NoneDecryptor : IBundleDecryptor { }

/// <summary>
/// "Junk header" offset decryptor: pretends each bundle has a fixed-length garbage prefix that must
/// be seeked past. The cheapest possible obfuscation - no CPU cost at load time, just defeats naive
/// byte-signature scanners. Set <see cref="JunkHeaderSize"/> to the prefix length the build pipeline
/// prepended.
/// </summary>
public sealed class OffsetDecryptor : IBundleOffsetDecryptor
{
    /// <summary>Number of junk bytes prepended to every bundle at build time.</summary>
    public int JunkHeaderSize { get; set; } = 64;

    public long GetFileOffset(BundleDecryptArgs args) => JunkHeaderSize;
}

/// <summary>
/// XOR-with-key memory decryptor: decrypts the whole bundle in memory by XORing each byte with a
/// repeating key. Trivially breakable (this is obfuscation, not security) but illustrates the
/// IBundleMemoryDecryptor path. The build pipeline must XOR-encrypt with the same key.
/// </summary>
public sealed class XorMemoryDecryptor : IBundleMemoryDecryptor
{
    /// <summary>The repeating-key bytes the build pipeline XOR-encrypted with.</summary>
    public byte[] Key { get; set; } = Array.Empty<byte>();

    public byte[] GetDecryptedData(BundleDecryptArgs args)
    {
        byte[] data;
        if (args.FileData != null) data = args.FileData;
        else if (!string.IsNullOrEmpty(args.FilePath)) data = File.ReadAllBytes(args.FilePath);
        else throw new ArgumentException("OffsetDecryptor needs FileData or FilePath", nameof(args));

        if (Key == null || Key.Length == 0) return data;
        var result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ Key[i % Key.Length]);
        return result;
    }
}
