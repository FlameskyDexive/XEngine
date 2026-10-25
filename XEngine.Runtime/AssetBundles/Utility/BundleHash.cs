// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// A 128-bit content hash used to identify and verify AssetBundle files and package manifests.
/// Distinct from <see cref="Guid"/> (which identifies an asset across the project): a BundleHash
/// identifies the *bytes* of a specific bundle build, so it changes whenever the content changes
/// and is what the version/diff/download flow compares. Modelled on Unity's Hash128 but stored as
/// a plain struct with no Unity dependency.
/// </summary>
[Serializable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct BundleHash : IEquatable<BundleHash>, IComparable<BundleHash>
{
    /// <summary>16 raw bytes of the hash, big-endian on disk.</summary>
    public readonly ulong Low;
    public readonly ulong High;

    public static BundleHash Zero => default;

    public bool IsValid => (Low | High) != 0UL;

    public BundleHash(ulong low, ulong high) { Low = low; High = high; }

    /// <summary>Construct from a 16-byte span (e.g. the output of a hash algorithm).</summary>
    public BundleHash(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16) throw new ArgumentException("BundleHash requires 16 bytes", nameof(bytes));
        Low = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        High = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8));
    }

    /// <summary>Derive a deterministic BundleHash from a Guid (used when a content hash is needed
    /// but only the asset identity is available, e.g. editor simulate mode).</summary>
    public static BundleHash FromGuid(Guid guid)
    {
        Span<byte> b = stackalloc byte[16];
        guid.TryWriteBytes(b);
        return new BundleHash(b);
    }

    /// <summary>Compute a 128-bit hash over arbitrary data using a 64-bit FNV-1a fold into both
    /// halves with different seeds. This is NOT cryptographic - it is a fast content fingerprint
    /// for change detection and cache keys, mirroring how Unity uses Hash128 for bundles.</summary>
    public static BundleHash Compute(ReadOnlySpan<byte> data)
    {
        // FNV-1a 64-bit parameters.
        const ulong fnvOffset = 0xcbf29ce484222325UL;
        const ulong fnvPrime = 0x100000001b3UL;

        const ulong highSeed = 0x9e3779b97f4a7c15UL; // golden-ratio seed for the high half
        const ulong highPrimeDelta = 0x100000001b2UL;
        const ulong highXorSalt = 0x9eUL;
        ulong low = fnvOffset;
        ulong high = highSeed ^ fnvOffset;
        foreach (byte b in data)
        {
            low = (low ^ b) * fnvPrime;
            high = (high ^ (b + highXorSalt)) * (fnvPrime + highPrimeDelta);
        }
        return new BundleHash(low, high);
    }

    /// <summary>Compute over a file's contents without loading the whole file twice (single pass).</summary>
    public static BundleHash ComputeFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan);
        return Compute(stream);
    }

    /// <summary>Hash from the stream's current position to its end without retaining its contents.</summary>
    public static BundleHash Compute(Stream stream)
    {
        const ulong fnvOffset = 0xcbf29ce484222325UL;
        const ulong fnvPrime = 0x100000001b3UL;
        const ulong highSeed = 0x9e3779b97f4a7c15UL;
        const ulong highPrimeDelta = 0x100000001b2UL;
        const ulong highXorSalt = 0x9eUL;
        ulong low = fnvOffset;
        ulong high = highSeed ^ fnvOffset;

        Span<byte> buffer = stackalloc byte[8192];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                byte b = buffer[i];
                low = (low ^ b) * fnvPrime;
                high = (high ^ (b + highXorSalt)) * (fnvPrime + highPrimeDelta);
            }
        }
        return new BundleHash(low, high);
    }

    /// <summary>Write the hash as 16 big-endian bytes (canonical on-disk representation).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBigEndian(Span<byte> destination)
    {
        if (destination.Length < 16) throw new ArgumentException("need 16 bytes", nameof(destination));
        BinaryPrimitives.WriteUInt64BigEndian(destination, Low);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8), High);
    }

    /// <summary>Read 16 big-endian bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BundleHash ReadBigEndian(ReadOnlySpan<byte> source)
    {
        if (source.Length < 16) throw new ArgumentException("need 16 bytes", nameof(source));
        return new BundleHash(BinaryPrimitives.ReadUInt64BigEndian(source), BinaryPrimitives.ReadUInt64BigEndian(source.Slice(8)));
    }

    public byte[] ToByteArray()
    {
        var b = new byte[16];
        WriteBigEndian(b);
        return b;
    }

    /// <summary>Canonical hex representation (32 lowercase chars). Matches Unity Hash128.ToString().</summary>
    public override string ToString()
    {
        Span<byte> b = stackalloc byte[16];
        WriteBigEndian(b);
        Span<char> c = stackalloc char[32];
        const string hex = "0123456789abcdef";
        for (int i = 0; i < 16; i++)
        {
            c[i * 2] = hex[b[i] >> 4];
            c[i * 2 + 1] = hex[b[i] & 0xF];
        }
        return new string(c);
    }

    public static BundleHash Parse(string hex)
    {
        if (hex == null || hex.Length != 32)
            throw new FormatException("BundleHash hex string must be 32 chars");
        Span<byte> b = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            b[i] = (byte)((HexVal(hex[i * 2]) << 4) | HexVal(hex[i * 2 + 1]));
        return ReadBigEndian(b);
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => throw new FormatException($"Invalid hex char '{c}'"),
    };

    public bool Equals(BundleHash other) => Low == other.Low && High == other.High;
    public override bool Equals(object? obj) => obj is BundleHash h && Equals(h);
    public override int GetHashCode() => HashCode.Combine(Low, High);
    public int CompareTo(BundleHash other) => High != other.High ? High.CompareTo(other.High) : Low.CompareTo(other.Low);

    public static bool operator ==(BundleHash a, BundleHash b) => a.Low == b.Low && a.High == b.High;
    public static bool operator !=(BundleHash a, BundleHash b) => !(a == b);
}
