using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace XEngine.VoxelWorld;

/// <summary>
/// Minecraft Anvil-compatible region shell: 8 KiB header, 4 KiB sectors, zlib payloads.
/// Payload bytes are opaque (VW01 for VoxelWorld); this is not Mojang NBT / .mca.
/// </summary>
public sealed class VoxelRegionFile : IDisposable
{
    public const int SectorSize = 4096;
    public const int ColumnsPerAxis = 32;
    public const int ColumnCount = ColumnsPerAxis * ColumnsPerAxis;
    public const byte CompressionZlib = 2;
    public const int MaxSectorsPerColumn = 255;

    private readonly string _path;
    private readonly int[] _offsets = new int[ColumnCount];
    private readonly int[] _timestamps = new int[ColumnCount];
    private readonly List<bool> _usedSectors = [];
    private bool _dirtyHeader;
    private bool _disposed;

    private VoxelRegionFile(string path)
    {
        _path = path;
    }

    public static VoxelRegionFile Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var region = new VoxelRegionFile(path);
        if (File.Exists(path))
            region.LoadHeader();
        else
            region.EnsureSectorMap(2);
        return region;
    }

    public bool TryReadColumn(int localX, int localZ, out byte[] payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        payload = [];
        int slot = Slot(localX, localZ);
        int loc = _offsets[slot];
        if (loc == 0)
            return false;

        int sectorOffset = (loc >> 8) & 0xFFFFFF;
        int sectorCount = loc & 0xFF;
        if (sectorOffset < 2 || sectorCount == 0)
            return false;

        long byteOffset = (long)sectorOffset * SectorSize;
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (byteOffset + 5 > fs.Length)
            throw new InvalidDataException($"Column ({localX},{localZ}) points past end of file.");

        fs.Position = byteOffset;
        Span<byte> lenBuf = stackalloc byte[4];
        ReadExact(fs, lenBuf);
        int length = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (length < 1 || length > sectorCount * SectorSize - 4)
            throw new InvalidDataException($"Invalid column length {length}.");

        int compression = fs.ReadByte();
        if (compression < 0)
            throw new EndOfStreamException();
        if (compression != CompressionZlib)
            throw new InvalidDataException($"Unsupported compression type {compression}; only zlib (2) is supported.");

        byte[] compressed = new byte[length - 1];
        ReadExact(fs, compressed);
        payload = Inflate(compressed);
        return true;
    }

    public void WriteColumn(int localX, int localZ, ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int slot = Slot(localX, localZ);
        byte[] compressed = Deflate(payload);
        int lengthField = compressed.Length + 1; // compression byte
        int totalBytes = 4 + lengthField;
        int sectorsNeeded = (totalBytes + SectorSize - 1) / SectorSize;
        if (sectorsNeeded > MaxSectorsPerColumn)
            throw new InvalidOperationException($"Column payload requires {sectorsNeeded} sectors; max is {MaxSectorsPerColumn}.");

        FreeSlot(slot);
        int sectorOffset = FindFreeSectors(sectorsNeeded);
        EnsureSectorMap(sectorOffset + sectorsNeeded);
        for (int i = 0; i < sectorsNeeded; i++)
            _usedSectors[sectorOffset + i] = true;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        using (var fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            if (fs.Length < 2L * SectorSize)
            {
                fs.SetLength(2L * SectorSize);
                WriteHeaderTables(fs);
            }

            long byteOffset = (long)sectorOffset * SectorSize;
            long end = byteOffset + sectorsNeeded * (long)SectorSize;
            if (fs.Length < end)
                fs.SetLength(end);

            fs.Position = byteOffset;
            Span<byte> lenBuf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(lenBuf, lengthField);
            fs.Write(lenBuf);
            fs.WriteByte(CompressionZlib);
            fs.Write(compressed);

            int pad = sectorsNeeded * SectorSize - totalBytes;
            if (pad > 0)
            {
                Span<byte> zeros = stackalloc byte[Math.Min(pad, 256)];
                zeros.Clear();
                int remaining = pad;
                while (remaining > 0)
                {
                    int n = Math.Min(remaining, zeros.Length);
                    fs.Write(zeros[..n]);
                    remaining -= n;
                }
            }

            _offsets[slot] = (sectorOffset << 8) | sectorsNeeded;
            _timestamps[slot] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _dirtyHeader = true;
            WriteHeaderTables(fs);
            _dirtyHeader = false;
        }
    }

    public void DeleteColumn(int localX, int localZ)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int slot = Slot(localX, localZ);
        if (_offsets[slot] == 0)
            return;
        FreeSlot(slot);
        _offsets[slot] = 0;
        _timestamps[slot] = 0;
        _dirtyHeader = true;
        Flush();
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_dirtyHeader && !File.Exists(_path))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        using var fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (fs.Length < 2L * SectorSize)
            fs.SetLength(2L * SectorSize);
        WriteHeaderTables(fs);
        _dirtyHeader = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_dirtyHeader)
            Flush();
        _disposed = true;
    }

    private void LoadHeader()
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length < 2L * SectorSize)
            throw new InvalidDataException($"Region file {_path} is shorter than 8 KiB header.");

        Span<byte> header = stackalloc byte[2 * SectorSize];
        ReadExact(fs, header);
        for (int i = 0; i < ColumnCount; i++)
        {
            _offsets[i] = BinaryPrimitives.ReadInt32BigEndian(header.Slice(i * 4, 4));
            _timestamps[i] = BinaryPrimitives.ReadInt32BigEndian(header.Slice(SectorSize + i * 4, 4));
        }

        int sectorCount = (int)((fs.Length + SectorSize - 1) / SectorSize);
        EnsureSectorMap(Math.Max(sectorCount, 2));
        _usedSectors[0] = true;
        _usedSectors[1] = true;
        for (int i = 0; i < ColumnCount; i++)
        {
            int loc = _offsets[i];
            if (loc == 0)
                continue;
            int offset = (loc >> 8) & 0xFFFFFF;
            int count = loc & 0xFF;
            EnsureSectorMap(offset + count);
            for (int s = 0; s < count; s++)
                _usedSectors[offset + s] = true;
        }
    }

    private void WriteHeaderTables(FileStream fs)
    {
        Span<byte> header = stackalloc byte[2 * SectorSize];
        header.Clear();
        for (int i = 0; i < ColumnCount; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(header.Slice(i * 4, 4), _offsets[i]);
            BinaryPrimitives.WriteInt32BigEndian(header.Slice(SectorSize + i * 4, 4), _timestamps[i]);
        }
        long pos = fs.Position;
        fs.Position = 0;
        fs.Write(header);
        fs.Position = pos;
    }

    private void FreeSlot(int slot)
    {
        int loc = _offsets[slot];
        if (loc == 0)
            return;
        int offset = (loc >> 8) & 0xFFFFFF;
        int count = loc & 0xFF;
        for (int i = 0; i < count; i++)
        {
            if (offset + i < _usedSectors.Count)
                _usedSectors[offset + i] = false;
        }
        _offsets[slot] = 0;
    }

    private int FindFreeSectors(int needed)
    {
        EnsureSectorMap(2);
        for (int start = 2; start + needed <= _usedSectors.Count; start++)
        {
            bool free = true;
            for (int i = 0; i < needed; i++)
            {
                if (_usedSectors[start + i])
                {
                    free = false;
                    break;
                }
            }
            if (free)
                return start;
        }

        int append = _usedSectors.Count;
        EnsureSectorMap(append + needed);
        return append;
    }

    private void EnsureSectorMap(int count)
    {
        while (_usedSectors.Count < count)
            _usedSectors.Add(false);
        if (_usedSectors.Count >= 1)
            _usedSectors[0] = true;
        if (_usedSectors.Count >= 2)
            _usedSectors[1] = true;
    }

    private static int Slot(int localX, int localZ)
    {
        if ((uint)localX >= ColumnsPerAxis || (uint)localZ >= ColumnsPerAxis)
            throw new ArgumentOutOfRangeException($"Local column ({localX},{localZ}) outside 0..31.");
        return localX + localZ * ColumnsPerAxis;
    }

    private static byte[] Deflate(ReadOnlySpan<byte> payload)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(payload);
        return output.ToArray();
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n == 0)
                throw new EndOfStreamException();
            read += n;
        }
    }
}
