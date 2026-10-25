// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// Varint writer - the mirror of BundleBufferReader. See that file's header for attribution.

using System;
using System.IO;
using System.Text;

namespace XEngine.Runtime.AssetBundles;

/// <summary>Writes primitives as unsigned/signed LEB128 varints with length-prefixed UTF-8 strings
/// and length-prefixed byte arrays. Pairs with <see cref="BundleBufferReader"/>.</summary>
public sealed class BundleBufferWriter
{
    private readonly BinaryWriter _writer;

    public BundleBufferWriter(Stream output) : this(new BinaryWriter(output, Encoding.UTF8, leaveOpen: true)) { }
    public BundleBufferWriter(BinaryWriter writer) { _writer = writer; }

    public long Position => _writer.BaseStream.Position;

    public void WriteByte(byte v) => _writer.Write(v);
    public void WriteSByte(sbyte v) => _writer.Write(v);
    public void WriteBool(bool v) => _writer.Write((byte)(v ? 1 : 0));
    public void WriteUInt16(ushort v) => _writer.Write(v);
    public void WriteInt16(short v) => _writer.Write(v);
    public void WriteUInt32(uint v) => _writer.Write(v);
    public void WriteInt32(int v) => _writer.Write(v);
    public void WriteUInt64(ulong v) => _writer.Write(v);
    public void WriteInt64(long v) => _writer.Write(v);
    public void WriteSingle(float v) => _writer.Write(v);
    public void WriteDouble(double v) => _writer.Write(v);

    public void WriteVarUInt64(ulong v)
    {
        while (v >= 0x80)
        {
            _writer.Write((byte)(v | 0x80));
            v >>= 7;
        }
        _writer.Write((byte)v);
    }

    public void WriteVarUInt32(uint v) => WriteVarUInt64(v);

    // Zig-zag the int, then widen via uint (NOT via long) so a negative value does not sign-extend
    // into a 10-byte ulong varint. Cast to uint first truncates to the 32-bit zig-zag pattern.
    public void WriteVarInt32(int v) => WriteVarUInt64((ulong)(uint)((v << 1) ^ (v >> 31)));
    public void WriteVarInt64(long v) => WriteVarUInt64((ulong)((v << 1) ^ (v >> 63)));

    public void WriteString(string v)
    {
        if (string.IsNullOrEmpty(v)) { WriteVarUInt64(0UL); return; }
        int len = Encoding.UTF8.GetByteCount(v);
        WriteVarUInt64((ulong)len);
        Span<byte> buf = len <= 256 ? stackalloc byte[len] : new byte[len];
        Encoding.UTF8.GetBytes(v, buf);
        _writer.Write(buf);
    }

    public void WriteByteArray(byte[] bytes)
    {
        WriteVarUInt64((ulong)bytes.Length);
        if (bytes.Length > 0) _writer.Write(bytes);
    }

    public void WriteArray<T>(T[] arr, Action<T> writeElement)
    {
        WriteVarUInt64((ulong)arr.Length);
        for (int i = 0; i < arr.Length; i++) writeElement(arr[i]);
    }

    public void WriteVarInt32Array(int[] arr) => WriteArray(arr, WriteVarInt32);
    public void WriteStringArray(string[] arr) => WriteArray(arr, WriteString);

    public void WriteGuid(Guid guid)
    {
        Span<byte> b = stackalloc byte[16];
        guid.TryWriteBytes(b);
        _writer.Write(b);
    }

    public void WriteBundleHash(BundleHash hash)
    {
        Span<byte> b = stackalloc byte[16];
        hash.WriteBigEndian(b);
        _writer.Write(b);
    }
}
