using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XEngine.VoxelWorld;

/// <summary>VW01 column payload: minY + present flags + VoxelPalette blobs. Not Mojang NBT.</summary>
public static class VoxelColumnCodec
{
    public static readonly byte[] Magic = [(byte)'V', (byte)'W', (byte)'0', (byte)'1'];

    public static byte[] Encode(IReadOnlyList<VoxelPalette?> sections, int minY)
    {
        ArgumentNullException.ThrowIfNull(sections);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(minY);
            w.Write(sections.Count);
            for (int i = 0; i < sections.Count; i++)
            {
                VoxelPalette? section = sections[i];
                if (section == null)
                {
                    w.Write((byte)0);
                    continue;
                }

                w.Write((byte)1);
                section.WriteTo(ms);
            }
        }
        return ms.ToArray();
    }

    public static List<VoxelPalette?> Decode(ReadOnlySpan<byte> payload, out int minY)
    {
        using var ms = new MemoryStream(payload.ToArray());
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        Span<byte> magic = stackalloc byte[4];
        if (r.Read(magic) != 4 || !magic.SequenceEqual(Magic))
            throw new InvalidDataException("Column payload missing VW01 magic.");
        minY = r.ReadInt32();
        int count = r.ReadInt32();
        if (count < 0 || count > 512)
            throw new InvalidDataException($"Invalid section count {count}.");
        var sections = new List<VoxelPalette?>(count);
        for (int i = 0; i < count; i++)
        {
            byte present = r.ReadByte();
            if (present == 0)
            {
                sections.Add(null);
                continue;
            }
            if (present != 1)
                throw new InvalidDataException($"Invalid present flag {present}.");
            sections.Add(VoxelPalette.ReadFrom(ms));
        }
        return sections;
    }
}
