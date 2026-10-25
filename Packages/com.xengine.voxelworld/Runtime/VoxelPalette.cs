using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XEngine.VoxelWorld;

/// <summary>
/// 16³ paletted container aligned with Minecraft Java 1.16+ padded longs.
/// BitsPerEntry is 0 (single), 4–8 (indirect), or 16 (direct ushorts).
/// </summary>
public sealed class VoxelPalette
{
    public const int Length = VoxelCoords.ChunkVoxelCount;

    public byte BitsPerEntry { get; private set; }
    public ushort[] Palette { get; private set; } = [0];
    public ulong[] Data { get; private set; } = [];
    public int NonAirCount { get; private set; }

    public static VoxelPalette Fill(ushort type)
    {
        var p = new VoxelPalette
        {
            BitsPerEntry = 0,
            Palette = [type],
            Data = [],
        };
        p.Recount();
        return p;
    }

    public ushort Get(int index)
    {
        if ((uint)index >= Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (BitsPerEntry == 0)
            return Palette[0];

        int entriesPerLong = 64 / BitsPerEntry;
        int longIndex = index / entriesPerLong;
        int offset = (index % entriesPerLong) * BitsPerEntry;
        ulong mask = BitsPerEntry == 16 ? 0xFFFFUL : (1UL << BitsPerEntry) - 1UL;
        int local = (int)((Data[longIndex] >> offset) & mask);
        return BitsPerEntry == 16 ? (ushort)local : Palette[local];
    }

    public void Set(int index, ushort type)
    {
        if ((uint)index >= Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        ushort before = Get(index);
        if (before == type)
            return;

        if (BitsPerEntry == 0)
        {
            var types = new ushort[Length];
            types.AsSpan().Fill(Palette[0]);
            types[index] = type;
            VoxelPalette packed = PackFromTypes(types);
            BitsPerEntry = packed.BitsPerEntry;
            Palette = packed.Palette;
            Data = packed.Data;
            NonAirCount = packed.NonAirCount;
            return;
        }

        if (BitsPerEntry == 16)
        {
            WriteBits(index, type);
        }
        else
        {
            int paletteIndex = IndexOfPalette(type);
            if (paletteIndex < 0)
            {
                EnsureType(type);
                paletteIndex = IndexOfPalette(type);
            }
            WriteBits(index, (ushort)paletteIndex);
        }

        if (before == 0 && type != 0)
            NonAirCount++;
        else if (before != 0 && type == 0)
            NonAirCount--;
    }

    public static VoxelPalette CompactFrom(ReadOnlySpan<Voxel> voxels)
    {
        if (voxels.Length < Length)
            throw new ArgumentException($"Expected at least {Length} voxels.", nameof(voxels));

        var types = new ushort[Length];
        for (int i = 0; i < Length; i++)
            types[i] = voxels[i].TypeIndex;
        return PackFromTypes(types);
    }

    public void ExpandTo(Span<Voxel> dest)
    {
        if (dest.Length < Length)
            throw new ArgumentException($"Expected at least {Length} voxels.", nameof(dest));
        for (int i = 0; i < Length; i++)
        {
            ushort t = Get(i);
            dest[i] = t == 0 ? Voxel.Empty : Voxel.Solid(t);
        }
    }

    public void WriteTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(BitsPerEntry);
        w.Write(NonAirCount);
        w.Write(Palette.Length);
        for (int i = 0; i < Palette.Length; i++)
            w.Write(Palette[i]);
        w.Write(Data.Length);
        for (int i = 0; i < Data.Length; i++)
            w.Write(Data[i]);
    }

    public static VoxelPalette ReadFrom(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        byte bits = r.ReadByte();
        int nonAir = r.ReadInt32();
        int paletteLen = r.ReadInt32();
        if (paletteLen < 0 || paletteLen > Length + 1)
            throw new InvalidDataException($"Invalid palette length {paletteLen}.");
        var palette = new ushort[paletteLen];
        for (int i = 0; i < paletteLen; i++)
            palette[i] = r.ReadUInt16();
        int dataLen = r.ReadInt32();
        if (dataLen < 0 || dataLen > Length)
            throw new InvalidDataException($"Invalid data length {dataLen}.");
        var data = new ulong[dataLen];
        for (int i = 0; i < dataLen; i++)
            data[i] = r.ReadUInt64();

        ValidateBits(bits, paletteLen, dataLen);
        return new VoxelPalette
        {
            BitsPerEntry = bits,
            Palette = palette,
            Data = data,
            NonAirCount = nonAir,
        };
    }

    private void Recount()
    {
        if (BitsPerEntry == 0)
        {
            NonAirCount = Palette[0] == 0 ? 0 : Length;
            return;
        }

        int count = 0;
        for (int i = 0; i < Length; i++)
        {
            if (Get(i) != 0)
                count++;
        }
        NonAirCount = count;
    }

    private void EnsureType(ushort type)
    {
        if (IndexOfPalette(type) >= 0)
            return;

        int nextCount = Palette.Length + 1;
        byte needBits = BitsForUnique(nextCount);
        if (needBits != BitsPerEntry)
        {
            var types = new ushort[Length];
            for (int i = 0; i < Length; i++)
                types[i] = Get(i);
            // Pending cell is still old type; PackFromTypes will not include `type` yet.
            // Grow palette capacity by rebuilding with an extra unique placeholder:
            // inject `type` as a unique that appears nowhere — PackFromTypes only sees used types.
            // So rebuild manually with extended palette then leave Data as current types.
            var unique = CollectUnique(types);
            bool found = false;
            for (int u = 0; u < unique.Count; u++)
            {
                if (unique[u] == type)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                unique.Add(type);

            needBits = BitsForUnique(unique.Count);
            var palette = needBits == 16 ? Array.Empty<ushort>() : unique.ToArray();
            var data = needBits == 0 ? Array.Empty<ulong>() : new ulong[DataLongCount(needBits)];
            if (needBits == 0)
            {
                BitsPerEntry = 0;
                Palette = [unique[0]];
                Data = [];
                return;
            }

            PackTypesIntoData(types, needBits, palette, data);
            BitsPerEntry = needBits;
            Palette = palette;
            Data = data;
            return;
        }

        var grown = new ushort[nextCount];
        Array.Copy(Palette, grown, Palette.Length);
        grown[^1] = type;
        Palette = grown;
    }

    private static List<ushort> CollectUnique(ushort[] types)
    {
        var unique = new List<ushort>(16);
        for (int i = 0; i < types.Length; i++)
        {
            ushort v = types[i];
            bool found = false;
            for (int u = 0; u < unique.Count; u++)
            {
                if (unique[u] == v)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                unique.Add(v);
        }
        return unique;
    }

    private static VoxelPalette PackFromTypes(ushort[] types)
    {
        var unique = new ushort[Length];
        int uniqueCount = 0;
        for (int i = 0; i < Length; i++)
        {
            ushort v = types[i];
            bool found = false;
            for (int u = 0; u < uniqueCount; u++)
            {
                if (unique[u] == v)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                unique[uniqueCount++] = v;
        }

        byte bits = BitsForUnique(uniqueCount);
        if (bits == 0)
            return Fill(unique[0]);

        var palette = bits == 16 ? Array.Empty<ushort>() : new ushort[uniqueCount];
        if (bits != 16)
            Array.Copy(unique, palette, uniqueCount);
        var data = new ulong[DataLongCount(bits)];
        PackTypesIntoData(types, bits, palette, data);
        var p = new VoxelPalette
        {
            BitsPerEntry = bits,
            Palette = palette,
            Data = data,
        };
        p.Recount();
        return p;
    }

    private static void PackTypesIntoData(ushort[] types, byte bits, ushort[] palette, ulong[] data)
    {
        int entriesPerLong = 64 / bits;
        ulong mask = bits == 16 ? 0xFFFFUL : (1UL << bits) - 1UL;
        for (int i = 0; i < Length; i++)
        {
            ushort type = types[i];
            ulong value;
            if (bits == 16)
            {
                value = type;
            }
            else
            {
                int pi = -1;
                for (int p = 0; p < palette.Length; p++)
                {
                    if (palette[p] == type)
                    {
                        pi = p;
                        break;
                    }
                }
                if (pi < 0)
                    throw new InvalidOperationException($"Type {type} missing from palette.");
                value = (ulong)pi & mask;
            }

            int longIndex = i / entriesPerLong;
            int offset = (i % entriesPerLong) * bits;
            data[longIndex] &= ~(mask << offset);
            data[longIndex] |= value << offset;
        }
    }

    private void WriteBits(int index, ushort value)
    {
        int entriesPerLong = 64 / BitsPerEntry;
        int longIndex = index / entriesPerLong;
        int offset = (index % entriesPerLong) * BitsPerEntry;
        ulong mask = BitsPerEntry == 16 ? 0xFFFFUL : (1UL << BitsPerEntry) - 1UL;
        Data[longIndex] &= ~(mask << offset);
        Data[longIndex] |= ((ulong)value & mask) << offset;
    }

    private int IndexOfPalette(ushort type)
    {
        if (BitsPerEntry == 16)
            return type; // unused for membership
        for (int i = 0; i < Palette.Length; i++)
        {
            if (Palette[i] == type)
                return i;
        }
        return -1;
    }

    private static byte BitsForUnique(int uniqueCount)
    {
        if (uniqueCount <= 1)
            return 0;
        if (uniqueCount <= 16)
            return 4;
        if (uniqueCount <= 32)
            return 5;
        if (uniqueCount <= 64)
            return 6;
        if (uniqueCount <= 128)
            return 7;
        if (uniqueCount <= 256)
            return 8;
        return 16;
    }

    private static int DataLongCount(byte bits)
    {
        if (bits == 0)
            return 0;
        int entriesPerLong = 64 / bits;
        return (Length + entriesPerLong - 1) / entriesPerLong;
    }

    private static void ValidateBits(byte bits, int paletteLen, int dataLen)
    {
        if (bits is not (0 or 4 or 5 or 6 or 7 or 8 or 16))
            throw new InvalidDataException($"Unsupported bits {bits}.");
        if (bits == 0)
        {
            if (paletteLen != 1 || dataLen != 0)
                throw new InvalidDataException("Single-valued palette must have one entry and empty data.");
            return;
        }

        int expected = DataLongCount(bits);
        if (dataLen != expected)
            throw new InvalidDataException($"Data length {dataLen} != expected {expected} for bits {bits}.");
        if (bits == 16)
        {
            if (paletteLen != 0)
                throw new InvalidDataException("Direct palette must have empty local palette.");
        }
        else if (paletteLen < 1 || paletteLen > (1 << bits))
        {
            throw new InvalidDataException($"Indirect palette length {paletteLen} invalid for bits {bits}.");
        }
    }
}
