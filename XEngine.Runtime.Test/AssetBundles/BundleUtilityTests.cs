// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Text;

using XEngine.Runtime.AssetBundles;

using Xunit;

namespace XEngine.Runtime.Test.AssetBundles;

/// <summary>
/// Tests for the Stage A utility layer: CRC32, BundleHash, and the varint buffer reader/writer
/// round-trip. These primitives are the foundation of both the bundle container and the manifest.
/// </summary>
public class BundleUtilityTests
{
    [Fact]
    public void StreamingChecksums_MatchBytesAcrossShortReads_AndLeaveStreamOpen()
    {
        byte[] bytes = new byte[32771];
        new Random(17).NextBytes(bytes);
        using var stream = new ShortReadStream(bytes);
        Assert.Equal(BundleHash.Compute(bytes), BundleHash.Compute(stream));
        Assert.True(stream.CanRead);
        stream.Position = 0;
        Assert.Equal(Crc32Algorithm.Compute(bytes), Crc32Algorithm.Compute(stream));
        Assert.True(stream.CanRead);
    }

    private sealed class ShortReadStream(byte[] bytes) : System.IO.MemoryStream(bytes, writable: false)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(7, buffer.Length)]);
    }
    // ------------------------------------------------------------------
    //  CRC32 (known answer against the canonical "123456789" value)
    // ------------------------------------------------------------------

    [Fact]
    public void Crc32_KnownValue_MatchesStandard()
    {
        // CRC32 of ASCII "123456789" is 0xCBF43926 (zlib/PNG standard).
        var data = Encoding.ASCII.GetBytes("123456789");
        uint crc = Crc32Algorithm.Compute(data);
        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact]
    public void Crc32_EmptyInput_ReturnsZero()
    {
        Assert.Equal(0u, Crc32Algorithm.Compute(Array.Empty<byte>()));
    }

    [Fact]
    public void Crc32_DifferentData_DifferentCrc()
    {
        Assert.NotEqual(
            Crc32Algorithm.Compute(Encoding.ASCII.GetBytes("hello")),
            Crc32Algorithm.Compute(Encoding.ASCII.GetBytes("world")));
    }

    // ------------------------------------------------------------------
    //  BundleHash
    // ------------------------------------------------------------------

    [Fact]
    public void BundleHash_RoundTripsThroughBytes()
    {
        var original = BundleHash.Compute(Encoding.ASCII.GetBytes("some content"));
        var bytes = original.ToByteArray();
        Assert.Equal(16, bytes.Length);
        var restored = BundleHash.ReadBigEndian(bytes);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void BundleHash_ParseRoundTripsThroughToString()
    {
        var original = BundleHash.Compute(Encoding.ASCII.GetBytes("parse me"));
        var hex = original.ToString();
        Assert.Equal(32, hex.Length);
        var restored = BundleHash.Parse(hex);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void BundleHash_ZeroIsInvalid()
    {
        Assert.False(BundleHash.Zero.IsValid);
        Assert.True(BundleHash.Compute(new byte[] { 1 }).IsValid);
    }

    [Fact]
    public void BundleHash_DifferentContent_DifferentHash()
    {
        Assert.NotEqual(
            BundleHash.Compute(Encoding.ASCII.GetBytes("a")),
            BundleHash.Compute(Encoding.ASCII.GetBytes("b")));
    }

    [Fact]
    public void BundleHash_EqualityAndHashing()
    {
        var a = BundleHash.Compute(new byte[] { 1, 2, 3 });
        var b = BundleHash.Compute(new byte[] { 1, 2, 3 });
        var c = BundleHash.Compute(new byte[] { 4, 5, 6 });
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.True(a != c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // ------------------------------------------------------------------
    //  Buffer reader/writer varint round-trip
    // ------------------------------------------------------------------

    [Fact]
    public void BufferReaderWriter_VarIntRoundTrip()
    {
        using var ms = new System.IO.MemoryStream();
        var w = new BundleBufferWriter(ms);
        w.WriteVarInt32(0);
        w.WriteVarInt32(1);
        w.WriteVarInt32(-1);
        w.WriteVarInt32(int.MaxValue);
        w.WriteVarInt32(int.MinValue);
        w.WriteVarInt64(long.MaxValue);
        w.WriteVarUInt64(ulong.MaxValue);

        ms.Position = 0;
        var r = new BundleBufferReader(ms);
        Assert.Equal(0, r.ReadVarInt32());
        Assert.Equal(1, r.ReadVarInt32());
        Assert.Equal(-1, r.ReadVarInt32());
        Assert.Equal(int.MaxValue, r.ReadVarInt32());
        Assert.Equal(int.MinValue, r.ReadVarInt32());
        Assert.Equal(long.MaxValue, r.ReadVarInt64());
        Assert.Equal(ulong.MaxValue, r.ReadVarUInt64());
    }

    [Fact]
    public void BufferReaderWriter_StringAndGuidRoundTrip()
    {
        using var ms = new System.IO.MemoryStream();
        var w = new BundleBufferWriter(ms);
        w.WriteString("hello/world");
        w.WriteString(string.Empty);
        w.WriteGuid(Guid.NewGuid());

        ms.Position = 0;
        var r = new BundleBufferReader(ms);
        Assert.Equal("hello/world", r.ReadString());
        Assert.Equal(string.Empty, r.ReadString());
        var g = r.ReadGuid();
        Assert.NotEqual(Guid.Empty, g);
    }

    [Fact]
    public void BufferReaderWriter_ArraysRoundTrip()
    {
        using var ms = new System.IO.MemoryStream();
        var w = new BundleBufferWriter(ms);
        w.WriteStringArray(new[] { "a", "bb", "ccc" });
        w.WriteVarInt32Array(new[] { 10, -20, 30 });

        ms.Position = 0;
        var r = new BundleBufferReader(ms);
        Assert.Equal(new[] { "a", "bb", "ccc" }, r.ReadStringArray());
        Assert.Equal(new[] { 10, -20, 30 }, r.ReadVarInt32Array());
    }
}
