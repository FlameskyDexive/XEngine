using System;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Water fill, grass tufts, and a 1×4 trunk + leaf cap.</summary>
public static class VoxelDecorations
{
    public const ushort Water = 8;
    public const ushort Plant = 5;
    public const ushort Wood = 9;
    public const ushort Leaves = 5;
    public const ushort Cloud = 10;

    public static int FillWater(Span<Voxel> voxels, Int3 chunkMin, int waterLevel)
    {
        int filled = 0;
        for (int y = 0; y < VoxelCoords.ChunkSize; y++)
        {
            int worldY = chunkMin.Y + y;
            if (worldY > waterLevel)
                continue;
            for (int z = 0; z < VoxelCoords.ChunkSize; z++)
            {
                for (int x = 0; x < VoxelCoords.ChunkSize; x++)
                {
                    int index = VoxelCoords.Index(x, y, z);
                    if (!voxels[index].IsEmpty)
                        continue;
                    voxels[index] = Voxel.Solid(Water);
                    filled++;
                }
            }
        }

        return filled;
    }

    public static bool TryStampPlant(Span<Voxel> voxels, Int3 chunkMin, int localX, int localZ, int groundY)
    {
        int worldY = groundY + 1;
        int y = worldY - chunkMin.Y;
        if ((uint)localX >= VoxelCoords.ChunkSize || (uint)localZ >= VoxelCoords.ChunkSize || (uint)y >= VoxelCoords.ChunkSize)
            return false;
        int index = VoxelCoords.Index(localX, y, localZ);
        if (!voxels[index].IsEmpty)
            return false;
        voxels[index] = Voxel.Solid(Plant);
        return true;
    }

    public static int StampTree(Span<Voxel> voxels, Int3 chunkMin, int localX, int localZ, int groundY)
    {
        int written = 0;
        for (int i = 1; i <= 4; i++)
        {
            if (TrySet(voxels, chunkMin, localX, groundY + i, localZ, Wood))
                written++;
        }

        int top = groundY + 4;
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (TrySet(voxels, chunkMin, localX + dx, top, localZ + dz, Leaves))
                    written++;
                if (TrySet(voxels, chunkMin, localX + dx, top + 1, localZ + dz, Leaves))
                    written++;
            }
        }

        return written;
    }

    public static int StampClouds(Span<Voxel> voxels, Int3 chunkMin, int cloudAltitude, int seed)
    {
        int filled = 0;
        for (int z = 0; z < VoxelCoords.ChunkSize; z++)
        {
            for (int x = 0; x < VoxelCoords.ChunkSize; x++)
            {
                int worldX = chunkMin.X + x;
                int worldZ = chunkMin.Z + z;
                float n = VoxelValueNoise.Fbm(worldX * 0.07f, worldZ * 0.07f, seed + 211);
                float blob = VoxelValueNoise.Fbm(worldX * 0.028f, worldZ * 0.028f, seed + 409);
                if (n < 0.55f || blob < 0.30f)
                    continue; // sparse clouds: a full deck blankets aerial views
                int thickness = 1 + (n > 0.62f ? 1 : 0);
                for (int dy = 0; dy < thickness; dy++)
                {
                    int worldY = cloudAltitude + dy;
                    int y = worldY - chunkMin.Y;
                    if ((uint)y >= VoxelCoords.ChunkSize)
                        continue;
                    int index = VoxelCoords.Index(x, y, z);
                    if (!voxels[index].IsEmpty)
                        continue;
                    voxels[index] = Voxel.Solid(Cloud);
                    filled++;
                }
            }
        }

        return filled;
    }

    private static bool TrySet(Span<Voxel> voxels, Int3 chunkMin, int localX, int worldY, int localZ, ushort type)
    {
        int y = worldY - chunkMin.Y;
        if ((uint)localX >= VoxelCoords.ChunkSize || (uint)localZ >= VoxelCoords.ChunkSize || (uint)y >= VoxelCoords.ChunkSize)
            return false;
        int index = VoxelCoords.Index(localX, y, localZ);
        voxels[index] = Voxel.Solid(type);
        return true;
    }
}
