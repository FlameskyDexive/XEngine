// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// CRC-32 (IEEE 802.3 polynomial 0xEDB88320) used for AssetBundle file integrity and download
// verification. The algorithm itself is a public-domain standard table-driven implementation
// (informative reference: YooAsset's CRC32Algorithm, Apache-2.0, which itself mirrors zlib).

using System;
using System.Runtime.CompilerServices;

namespace XEngine.Runtime.AssetBundles;

/// <summary>Table-driven CRC-32 (zlib polynomial). Not cryptographic - integrity only.</summary>
public static class Crc32Algorithm
{
    public const uint InitialValue = 0xFFFFFFFFu;

    private static readonly uint[] s_table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1u) != 0u ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            t[i] = c;
        }
        return t;
    }

    /// <summary>Continue a running CRC. Start by passing <see cref="InitialValue"/> as <paramref name="crc"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Update(uint crc, byte b)
    {
        unchecked { return s_table[(crc ^ b) & 0xFF] ^ (crc >> 8); }
    }

    /// <summary>Compute the CRC-32 of a byte span.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = InitialValue;
        foreach (byte b in data)
            crc = s_table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    /// <summary>Compute the CRC-32 of a byte array segment.</summary>
    public static uint Compute(byte[] data, int offset, int length)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (offset < 0 || length < 0 || offset + length > data.Length)
            throw new ArgumentOutOfRangeException();
        uint crc = InitialValue;
        for (int i = 0; i < length; i++)
        {
            byte b = data[offset + i];
            crc = s_table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return ~crc;
    }

    /// <summary>Stream-friendly: compute CRC-32 over a file in a single sequential pass.</summary>
    public static uint ComputeFile(string path)
    {
        using var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
            System.IO.FileShare.Read, 8192, System.IO.FileOptions.SequentialScan);
        return Compute(stream);
    }

    /// <summary>Read to the end with a bounded buffer, leaving ownership with the caller.</summary>
    public static uint Compute(System.IO.Stream stream)
    {
        uint crc = InitialValue;
        Span<byte> buffer = stackalloc byte[8192];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            for (int i = 0; i < read; i++)
                crc = s_table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
        }
        return ~crc;
    }
}
