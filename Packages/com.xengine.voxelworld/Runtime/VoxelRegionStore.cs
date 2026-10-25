using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>
/// World-root store: region/r.rx.rz.vwr for edited columns only.
/// Unedited generated terrain is omitted and regenerated from seed on load.
/// </summary>
public sealed class VoxelRegionStore
{
    public const int RegionSize = VoxelRegionFile.ColumnsPerAxis;

    public string Root { get; }
    public int MinChunkY { get; set; }
    public int MaxChunkY { get; set; } = 3;

    public VoxelRegionStore(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = Path.GetFullPath(root);
    }

    public string RegionDirectory => Path.Combine(Root, "region");

    public static void RegionCoord(int chunkX, int chunkZ, out int rx, out int rz, out int localX, out int localZ)
    {
        rx = VoxelCoords.FloorDiv(chunkX, RegionSize);
        rz = VoxelCoords.FloorDiv(chunkZ, RegionSize);
        localX = chunkX - rx * RegionSize;
        localZ = chunkZ - rz * RegionSize;
    }

    public string RegionPath(int rx, int rz) => Path.Combine(RegionDirectory, $"r.{rx}.{rz}.vwr");

    public bool TryLoadSection(Int3 chunkCoord, out VoxelPalette blocks)
    {
        blocks = VoxelPalette.Fill(0);
        RegionCoord(chunkCoord.X, chunkCoord.Z, out int rx, out int rz, out int localX, out int localZ);
        string path = RegionPath(rx, rz);
        if (!File.Exists(path))
            return false;

        using var region = VoxelRegionFile.Open(path);
        if (!region.TryReadColumn(localX, localZ, out byte[] payload))
            return false;

        List<VoxelPalette?> sections = VoxelColumnCodec.Decode(payload, out int minY);
        int index = chunkCoord.Y - minY;
        if ((uint)index >= (uint)sections.Count)
            return false;
        VoxelPalette? section = sections[index];
        if (section == null)
            return false;
        blocks = section;
        return true;
    }

    public void SaveSection(Int3 chunkCoord, VoxelPalette blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (chunkCoord.Y < MinChunkY || chunkCoord.Y > MaxChunkY)
            throw new ArgumentOutOfRangeException(nameof(chunkCoord), "Section Y outside store MinChunkY..MaxChunkY.");

        RegionCoord(chunkCoord.X, chunkCoord.Z, out int rx, out int rz, out int localX, out int localZ);
        Directory.CreateDirectory(RegionDirectory);
        string path = RegionPath(rx, rz);

        int count = MaxChunkY - MinChunkY + 1;
        var sections = new List<VoxelPalette?>(count);
        for (int i = 0; i < count; i++)
            sections.Add(null);

        using (var region = VoxelRegionFile.Open(path))
        {
            if (region.TryReadColumn(localX, localZ, out byte[] existing))
            {
                List<VoxelPalette?> loaded = VoxelColumnCodec.Decode(existing, out int loadedMinY);
                for (int i = 0; i < loaded.Count; i++)
                {
                    int cy = loadedMinY + i;
                    if (cy < MinChunkY || cy > MaxChunkY)
                        continue;
                    sections[cy - MinChunkY] = loaded[i];
                }
            }

            sections[chunkCoord.Y - MinChunkY] = blocks;

            bool any = false;
            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i] != null)
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                region.DeleteColumn(localX, localZ);
                return;
            }

            byte[] payload = VoxelColumnCodec.Encode(sections, MinChunkY);
            region.WriteColumn(localX, localZ, payload);
        }
    }

    public void ClearColumn(int chunkX, int chunkZ)
    {
        RegionCoord(chunkX, chunkZ, out int rx, out int rz, out int localX, out int localZ);
        string path = RegionPath(rx, rz);
        if (!File.Exists(path))
            return;
        using var region = VoxelRegionFile.Open(path);
        region.DeleteColumn(localX, localZ);
    }

    /// <summary>Rewrite edited column keys after an origin shift of world cells.</summary>
    public void ShiftWorld(Int3 worldShift)
    {
        if (worldShift.X == 0 && worldShift.Y == 0 && worldShift.Z == 0)
            return;
        if (worldShift.X % VoxelCoords.ChunkSize != 0 ||
            worldShift.Y % VoxelCoords.ChunkSize != 0 ||
            worldShift.Z % VoxelCoords.ChunkSize != 0)
            throw new ArgumentException("World shift must be a multiple of chunk size.", nameof(worldShift));

        Int3 chunkShift = new(
            worldShift.X / VoxelCoords.ChunkSize,
            worldShift.Y / VoxelCoords.ChunkSize,
            worldShift.Z / VoxelCoords.ChunkSize);

        if (!Directory.Exists(RegionDirectory))
            return;

        var moved = new List<(Int3 coord, VoxelPalette blocks)>();
        foreach (string path in Directory.EnumerateFiles(RegionDirectory, "r.*.*.vwr"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            // r.rx.rz
            string[] parts = name.Split('.');
            if (parts.Length != 3 || parts[0] != "r")
                continue;
            if (!int.TryParse(parts[1], out int rx) || !int.TryParse(parts[2], out int rz))
                continue;

            using var region = VoxelRegionFile.Open(path);
            for (int lz = 0; lz < RegionSize; lz++)
            {
                for (int lx = 0; lx < RegionSize; lx++)
                {
                    if (!region.TryReadColumn(lx, lz, out byte[] payload))
                        continue;
                    List<VoxelPalette?> sections = VoxelColumnCodec.Decode(payload, out int minY);
                    int chunkX = rx * RegionSize + lx;
                    int chunkZ = rz * RegionSize + lz;
                    for (int i = 0; i < sections.Count; i++)
                    {
                        if (sections[i] == null)
                            continue;
                        var oldCoord = new Int3(chunkX, minY + i, chunkZ);
                        var newCoord = new Int3(
                            oldCoord.X - chunkShift.X,
                            oldCoord.Y - chunkShift.Y,
                            oldCoord.Z - chunkShift.Z);
                        moved.Add((newCoord, sections[i]!));
                    }
                    region.DeleteColumn(lx, lz);
                }
            }
        }

        for (int i = 0; i < moved.Count; i++)
            SaveSection(moved[i].coord, moved[i].blocks);
    }
}
