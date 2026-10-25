using System;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

public enum VoxelPoiKind : byte
{
    None = 0,
    WatchTower = 1,
    Shrine = 2,
    Ruins = 3,
}

public readonly struct VoxelPoi
{
    public VoxelPoi(VoxelPoiKind kind, Int3 origin)
    {
        Kind = kind;
        Origin = origin;
    }

    public VoxelPoiKind Kind { get; }
    /// <summary>World-cell base of the structure (ground level).</summary>
    public Int3 Origin { get; }

    /// <summary>Height the player must reach to "claim" a watch tower.</summary>
    public int ClaimY => Origin.Y + 14;
}

/// <summary>
/// Deterministic points of interest stamped into chunk paints: watch towers (climb to
/// reveal the map), shrines (guardian trials) and village ruins. POIs sit on a
/// <see cref="CellSize"/> grid — one per cell, kind and offset chosen by position hash so
/// every chunk paints its slice identically without cross-chunk coordination.
/// </summary>
public static class VoxelStructures
{
    public const int CellSize = 192;
    public const int TowerHeight = 16;

    /// <summary>POI for the structure cell containing world position (x, z).</summary>
    public static VoxelPoi PoiAt(int x, int z, int seed)
    {
        int cx = (int)MathF.Floor(x / (float)CellSize);
        int cz = (int)MathF.Floor(z / (float)CellSize);
        return PoiForCell(cx, cz, seed);
    }

    public static VoxelPoi PoiForCell(int cellX, int cellZ, int seed)
    {
        int h = Hash(cellX, cellZ, seed);
        int ox = 32 + (h & 63) + 16;
        int oz = 32 + ((h >> 6) & 63) + 16;
        var kind = (VoxelPoiKind)(1 + (((h >> 12) & 0xff) % 3));
        return new VoxelPoi(kind, new Int3(cellX * CellSize + ox, 0, cellZ * CellSize + oz));
    }

    /// <summary>Nearest POI of any kind within <paramref name="radius"/> of the position.</summary>
    public static VoxelPoi NearbyPoi(int x, int z, int seed, int radius = 24)
    {
        int cx = (int)MathF.Floor(x / (float)CellSize);
        int cz = (int)MathF.Floor(z / (float)CellSize);
        VoxelPoi best = default;
        float bestDist = radius;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var poi = PoiForCell(cx + dx, cz + dz, seed);
                if (poi.Kind == VoxelPoiKind.None)
                    continue;
                Float3 delta = new(poi.Origin.X - x, 0, poi.Origin.Z - z);
                float dist = Float3.Length(delta);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = poi;
                }
            }
        return best;
    }

    /// <summary>Stamps every structure overlapping this chunk. Call after terrain/caves so
    /// structures override the landscape. Ground snap uses the generator's own sampling.</summary>
    public static void StampForChunk(
        Span<Voxel> voxels,
        Int3 chunkMin,
        int seed,
        Func<int, int, (int ground, VoxelBiomeKind biome)> sampleColumn,
        VoxelBiomeKind fallbackBiome = VoxelBiomeKind.Forest)
    {
        int cx = (int)MathF.Floor(chunkMin.X / (float)CellSize);
        int cz = (int)MathF.Floor(chunkMin.Z / (float)CellSize);
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var poi = PoiForCell(cx + dx, cz + dz, seed);
                switch (poi.Kind)
                {
                    case VoxelPoiKind.WatchTower:
                        StampTower(voxels, chunkMin, poi.Origin, seed, sampleColumn);
                        break;
                    case VoxelPoiKind.Shrine:
                        StampShrine(voxels, chunkMin, poi.Origin, seed, sampleColumn);
                        break;
                    case VoxelPoiKind.Ruins:
                        StampRuins(voxels, chunkMin, poi.Origin, seed, sampleColumn);
                        break;
                }
                _ = fallbackBiome;
            }
    }

    private static void StampTower(
        Span<Voxel> voxels,
        Int3 chunkMin,
        Int3 origin,
        int seed,
        Func<int, int, (int ground, VoxelBiomeKind biome)> sample)
    {
        var (ground, _) = sample(origin.X + 2, origin.Z + 2);
        for (int level = 0; level < TowerHeight; level++)
        {
            int y = ground + level;
            for (int x = 0; x < 5; x++)
                for (int z = 0; z < 5; z++)
                {
                    bool wall = x == 0 || x == 4 || z == 0 || z == 4;
                    bool door = level < 2 && x == 2 && z == 0;
                    bool window = level % 5 == 3 && (x == 2 || z == 2) && !wall;
                    if (!wall || door || (wall && window && x == 2 && z == 0))
                        continue;
                    if (wall && window)
                        continue;
                    TrySet(voxels, chunkMin, origin.X + x, y, origin.Z + z, Voxel.Solid(4));
                }
        }
        // top platform + rim (the "reveal" spot)
        for (int x = 0; x < 5; x++)
            for (int z = 0; z < 5; z++)
                TrySet(voxels, chunkMin, origin.X + x, ground + TowerHeight, origin.Z + z, Voxel.Solid(4));
        for (int x = 0; x < 5; x++)
            for (int z = 0; z < 5; z++)
            {
                bool rim = x == 0 || x == 4 || z == 0 || z == 4;
                if (rim)
                    TrySet(voxels, chunkMin, origin.X + x, ground + TowerHeight + 1, origin.Z + z, Voxel.Solid(4));
            }
    }

    private static void StampShrine(
        Span<Voxel> voxels,
        Int3 chunkMin,
        Int3 origin,
        int seed,
        Func<int, int, (int ground, VoxelBiomeKind biome)> sample)
    {
        var (ground, _) = sample(origin.X + 3, origin.Z + 3);
        for (int x = 0; x < 7; x++)
            for (int z = 0; z < 7; z++)
            {
                bool wall = x == 0 || x == 6 || z == 0 || z == 6;
                bool doorway = x == 3 && z == 0;
                if (!wall || doorway)
                    continue;
                for (int level = 0; level < 4; level++)
                {
                    // broken crenellation: skip some wall tops
                    if (level == 3 && (x + z + seed) % 3 == 0)
                        continue;
                    TrySet(voxels, chunkMin, origin.X + x, ground + level, origin.Z + z, Voxel.Solid(4));
                }
            }
        // altar
        TrySet(voxels, chunkMin, origin.X + 3, ground, origin.Z + 3, Voxel.Solid(4));
        TrySet(voxels, chunkMin, origin.X + 3, ground + 1, origin.Z + 3, Voxel.Solid(16));
    }

    private static void StampRuins(
        Span<Voxel> voxels,
        Int3 chunkMin,
        Int3 origin,
        int seed,
        Func<int, int, (int ground, VoxelBiomeKind biome)> sample)
    {
        var (ground, biome) = sample(origin.X + 4, origin.Z + 4);
        ushort mossy = biome == VoxelBiomeKind.Forest ? (ushort)5 : (ushort)4;
        for (int i = 0; i < 12; i++)
        {
            int x = i < 6 ? i : 0;
            int z = i < 6 ? 0 : i - 6;
            int height = 1 + ((Hash(origin.X + x, origin.Z + z, seed) & 0x7) % 3);
            for (int level = 0; level < height; level++)
                TrySet(voxels, chunkMin, origin.X + x, ground + level, origin.Z + z, Voxel.Solid(level == 0 ? (ushort)4 : mossy));
        }
        // collapsed roof tiles scattered on the ground
        for (int i = 0; i < 6; i++)
        {
            int h = Hash(origin.X + i * 7, origin.Z - i * 3, seed + 5);
            int x = 1 + (h & 3);
            int z = 1 + ((h >> 4) & 3);
            TrySet(voxels, chunkMin, origin.X + x, ground, origin.Z + z, Voxel.Solid(4));
        }
    }

    private static void TrySet(Span<Voxel> voxels, Int3 chunkMin, int worldX, int worldY, int worldZ, Voxel voxel)
    {
        int x = worldX - chunkMin.X;
        int y = worldY - chunkMin.Y;
        int z = worldZ - chunkMin.Z;
        if ((uint)x >= VoxelCoords.ChunkSize || (uint)y >= VoxelCoords.ChunkSize || (uint)z >= VoxelCoords.ChunkSize)
            return;
        voxels[VoxelCoords.Index(x, y, z)] = voxel;
    }

    private static int Hash(int x, int z, int seed)
    {
        int h = unchecked(x * 374761393 + z * 668265263 + seed * 1274126177);
        h = (h ^ (h >> 13)) * 1274126177;
        return h;
    }
}
