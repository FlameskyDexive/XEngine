// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace XEngine.Runtime.Test;

/// <summary>Minimal RGBA8 PNG writer for render-acceptance screenshot evidence.</summary>
public static class TestPngWriter
{
    public static void WritePng(string path, byte[] rgba, int width, int height)
    {
        using var memory = new MemoryStream();
        memory.WriteByte(0x89); memory.WriteByte(0x50); memory.WriteByte(0x4E); memory.WriteByte(0x47);
        memory.WriteByte(0x0D); memory.WriteByte(0x0A); memory.WriteByte(0x1A); memory.WriteByte(0x0A);

        byte[] ihdr = new byte[13];
        WriteBigEndianInt32(ihdr, 0, width);
        WriteBigEndianInt32(ihdr, 4, height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type: RGBA
        WritePngChunk(memory, "IHDR", ihdr);

        byte[] raw = new byte[(width * 4 + 1) * height];
        int srcRow = width * 4;
        for (int y = 0; y < height; y++)
        {
            raw[y * (srcRow + 1)] = 0; // filter: none
            Buffer.BlockCopy(rgba, y * srcRow, raw, y * (srcRow + 1) + 1, srcRow);
        }
        byte[] idat;
        using (var deflate = new MemoryStream())
        {
            deflate.WriteByte(0x78);
            deflate.WriteByte(0x9C);
            using (var compressor = new DeflateStream(deflate, CompressionLevel.Optimal, true))
            {
                compressor.Write(raw, 0, raw.Length);
            }
            uint adler = Adler32(raw);
            byte[] adlerBytes = new byte[4];
            WriteBigEndianInt32(adlerBytes, 0, (int)adler);
            deflate.Write(adlerBytes, 0, 4);
            idat = deflate.ToArray();
        }
        WritePngChunk(memory, "IDAT", idat);
        WritePngChunk(memory, "IEND", Array.Empty<byte>());
        File.WriteAllBytes(path, memory.ToArray());
    }

    private static void WriteBigEndianInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset + 0] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    private static void WritePngChunk(Stream stream, string type, byte[] data)
    {
        byte[] lengthBytes = new byte[4];
        WriteBigEndianInt32(lengthBytes, 0, data.Length);
        stream.Write(lengthBytes, 0, 4);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, 4);
        if (data.Length > 0)
            stream.Write(data, 0, data.Length);
        uint crc = Crc32(typeBytes, data);
        byte[] crcBytes = new byte[4];
        WriteBigEndianInt32(crcBytes, 0, (int)crc);
        stream.Write(crcBytes, 0, 4);
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte t in data)
        {
            a = (a + t) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static uint[]? _crcTable;

    private static uint Crc32(byte[] type, byte[] data)
    {
        _crcTable ??= BuildCrcTable();
        uint crc = 0xFFFFFFFF;
        foreach (byte t in type)
            crc = _crcTable[(crc ^ t) & 0xFF] ^ (crc >> 8);
        foreach (byte t in data)
            crc = _crcTable[(crc ^ t) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
