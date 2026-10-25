// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace XEngine.Editor.Importers;

/// <summary>CPU parse of the Photoshop file header (8BPS). Layer decode is a later slice.</summary>
public sealed class PsdDocument
{
    public int Width { get; }
    public int Height { get; }

    public PsdDocument(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public static PsdDocument Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 26)
            throw new InvalidDataException("Photoshop header is 26 bytes.");

        ReadOnlySpan<byte> span = bytes;
        if (span[0] != (byte)'8' || span[1] != (byte)'B' || span[2] != (byte)'P' || span[3] != (byte)'S')
            throw new InvalidDataException($"Not a Photoshop file (signature {Encoding.ASCII.GetString(span.Slice(0, 4))}).");

        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(14));
        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(18));
        return new PsdDocument(width, height);
    }
}
