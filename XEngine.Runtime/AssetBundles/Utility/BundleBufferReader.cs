// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// Varint reader/writer for the compact binary PackageManifest format. Uses unsigned LEB128 for
// integers (informative reference: YooAsset's BufferReader/Writer, Apache-2.0, which uses the same
// public-domain LEB128 codec). XEngine's implementation is independent.

using System;
using System.IO;
using System.Text;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Reads primitives from a binary stream with unsigned/signed LEB128 integers and length-prefixed
/// UTF-8 strings. Used to deserialize <see cref="PackageManifest"/>. Throwing on truncated input is
/// the contract - manifests are integrity-checked (CRC/hash) before this layer runs.
/// </summary>
public sealed class BundleBufferReader
{
    private readonly BinaryReader _reader;
    private readonly byte[] _buffer = new byte[16];

    public BundleBufferReader(Stream input) : this(new BinaryReader(input, Encoding.UTF8, leaveOpen: true)) { }
    public BundleBufferReader(BinaryReader reader) { _reader = reader; }

    public long Position => _reader.BaseStream.Position;
    public long Length => _reader.BaseStream.Length;

    public byte ReadByte() => _reader.ReadByte();
    public sbyte ReadSByte() => _reader.ReadSByte();
    public bool ReadBool() => _reader.ReadByte() != 0;

    public ushort ReadUInt16() => _reader.ReadUInt16();
    public short ReadInt16() => _reader.ReadInt16();
    public uint ReadUInt32() => _reader.ReadUInt32();
    public int ReadInt32() => _reader.ReadInt32();
    public ulong ReadUInt64() => _reader.ReadUInt64();
    public long ReadInt64() => _reader.ReadInt64();

    public float ReadSingle() => _reader.ReadSingle();
    public double ReadDouble() => _reader.ReadDouble();

    /// <summary>Read an unsigned LEB128 varint.</summary>
    public ulong ReadVarUInt64()
    {
        ulong result = 0;
        int shift = 0;
        for (; ; shift += 7)
        {
            if (shift >= 64) throw new InvalidDataException("Varint too large");
            byte b = _reader.ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
        }
        return result;
    }

    public uint ReadVarUInt32() => (uint)ReadVarUInt64();
    public int ReadVarInt32()
    {
        // zig-zag decode of the low 32 bits
        ulong raw = ReadVarUInt64();
        return (int)((raw >> 1) ^ (uint)(-(long)(raw & 1)));
    }

    public long ReadVarInt64()
    {
        ulong raw = ReadVarUInt64();
        return (long)((raw >> 1) ^ (ulong)(-(long)(raw & 1)));
    }

    /// <summary>Read a UTF-8 string with an unsigned-varint length prefix.</summary>
    public string ReadString()
    {
        int len = checked((int)ReadVarUInt64());
        if (len == 0) return string.Empty;
        var bytes = _reader.ReadBytes(len);
        if (bytes.Length < len) throw new EndOfStreamException("Truncated string");
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Read a 32-bit length-prefixed byte array.</summary>
    public byte[] ReadByteArray()
    {
        int len = checked((int)ReadVarUInt64());
        if (len == 0) return Array.Empty<byte>();
        var bytes = _reader.ReadBytes(len);
        if (bytes.Length < len) throw new EndOfStreamException("Truncated byte array");
        return bytes;
    }

    /// <summary>Read an array of values, each read via <paramref name="readElement"/>.</summary>
    public T[] ReadArray<T>(Func<T> readElement)
    {
        int len = checked((int)ReadVarUInt64());
        if (len == 0) return Array.Empty<T>();
        var arr = new T[len];
        for (int i = 0; i < len; i++) arr[i] = readElement();
        return arr;
    }

    public int[] ReadVarInt32Array() => ReadArray(ReadVarInt32);
    public string[] ReadStringArray() => ReadArray(ReadString);

    public Guid ReadGuid()
    {
        var b = _reader.ReadBytes(16);
        if (b.Length < 16) throw new EndOfStreamException("Truncated Guid");
        return new Guid(b);
    }

    public BundleHash ReadBundleHash()
    {
        var b = _reader.ReadBytes(16);
        if (b.Length < 16) throw new EndOfStreamException("Truncated BundleHash");
        return BundleHash.ReadBigEndian(b);
    }
}
