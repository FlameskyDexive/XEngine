// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.Buffers.Text;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace XEngine.PackageRegistry;

/// <summary>
/// Subresource-integrity (SRI) strings as npm uses them in <c>dist.integrity</c>
/// (<c>sha512-BASE64…</c>). Supports SHA-512 (what npm writes today) and SHA-1 (legacy packages),
/// matching the registry's historical records.
/// </summary>
public readonly struct NpmIntegrity : IEquatable<NpmIntegrity>
{
    public string Algorithm { get; }
    public byte[] Hash { get; }

    private NpmIntegrity(string algorithm, byte[] hash)
    {
        Algorithm = algorithm;
        Hash = hash;
    }

    /// <summary>Parse an SRI string; returns false for null/empty/malformed input.</summary>
    public static bool TryParse(string? sri, out NpmIntegrity integrity)
    {
        integrity = default;
        if (string.IsNullOrEmpty(sri)) return false;
        int dash = sri.IndexOf('-');
        if (dash <= 0 || dash == sri.Length - 1) return false;
        string algorithm = sri[..dash];
        if (algorithm is not ("sha512" or "sha1")) return false;
        string base64 = sri[(dash + 1)..];
        byte[] hash = new byte[Base64.GetMaxDecodedFromUtf8Length(base64.Length)];
        OperationStatus status = Base64.DecodeFromUtf8(Encoding.UTF8.GetBytes(base64), hash, out _, out int written);
        if (status != OperationStatus.Done) return false;
        Array.Resize(ref hash, written);
        int expected = algorithm == "sha512" ? 64 : 20;
        if (hash.Length != expected) return false;
        integrity = new NpmIntegrity(algorithm, hash);
        return true;
    }

    public static NpmIntegrity Parse(string sri)
        => TryParse(sri, out NpmIntegrity integrity)
            ? integrity
            : throw new FormatException($"Malformed npm integrity string: '{sri}'.");

    /// <summary>Hash a stream with the integrity's algorithm and compare.</summary>
    public bool Verify(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        byte[] actual = ComputeHash(Algorithm, content);
        return FixedTimeEquals(actual, Hash);
    }

    /// <summary>Hash bytes and compare.</summary>
    public bool Verify(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        byte[] actual = ComputeHash(Algorithm, content);
        return FixedTimeEquals(actual, Hash);
    }

    /// <summary>Produce the SRI string for a payload (used when publishing).</summary>
    public static string Compute(byte[] content, string algorithm = "sha512")
    {
        byte[] hash = ComputeHash(algorithm, content);
        return algorithm + "-" + Convert.ToBase64String(hash);
    }

    /// <summary>SHA-1 hex shasum of a payload — npm records it alongside the SRI string.</summary>
    public static string ComputeShasum(byte[] content)
    {
        byte[] hash = ComputeHash("sha1", content);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] ComputeHash(string algorithm, byte[] content)
        => algorithm switch
        {
            "sha512" => SHA512.HashData(content),
            "sha1" => SHA1.HashData(content),
            _ => throw new NotSupportedException($"Unsupported npm integrity algorithm '{algorithm}'."),
        };

    private static byte[] ComputeHash(string algorithm, Stream content)
        => algorithm switch
        {
            "sha512" => SHA512.HashData(content),
            "sha1" => SHA1.HashData(content),
            _ => throw new NotSupportedException($"Unsupported npm integrity algorithm '{algorithm}'."),
        };

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        // SHA1/SHA512 are not secrets, but comparing hashes uniformly costs nothing.
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    public bool Equals(NpmIntegrity other)
        => string.Equals(Algorithm, other.Algorithm, StringComparison.Ordinal) && Hash.AsSpan().SequenceEqual(other.Hash);

    public override string ToString() => Algorithm + "-" + Convert.ToBase64String(Hash);
}
