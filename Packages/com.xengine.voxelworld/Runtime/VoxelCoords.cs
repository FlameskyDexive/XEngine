using System;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>16³ chunk addressing. Layout is Y-major, then Z, then X.</summary>
public static class VoxelCoords
{
    public const int ChunkSize = 16;
    public const int ChunkVoxelCount = ChunkSize * ChunkSize * ChunkSize;
    public const int OneZRow = ChunkSize;
    public const int OneYRow = ChunkSize * ChunkSize;

    public static int Index(int x, int y, int z)
    {
        if ((uint)x >= ChunkSize || (uint)y >= ChunkSize || (uint)z >= ChunkSize)
            throw new ArgumentOutOfRangeException($"Local voxel ({x},{y},{z}) is outside 0..{ChunkSize - 1}.");
        return y * OneYRow + z * OneZRow + x;
    }

    public static Int3 LocalFromIndex(int index)
    {
        if ((uint)index >= ChunkVoxelCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        int y = index / OneYRow;
        int rem = index - y * OneYRow;
        int z = rem / OneZRow;
        int x = rem - z * OneZRow;
        return new Int3(x, y, z);
    }

    public static int FloorDiv(int value, int divisor)
    {
        int q = value / divisor;
        int r = value % divisor;
        if (r != 0 && ((r < 0) != (divisor < 0)))
            q--;
        return q;
    }

    public static Int3 ChunkCoord(Int3 world) =>
        new(FloorDiv(world.X, ChunkSize), FloorDiv(world.Y, ChunkSize), FloorDiv(world.Z, ChunkSize));

    public static Int3 ChunkCoord(Float3 world) =>
        ChunkCoord(new Int3((int)MathF.Floor(world.X), (int)MathF.Floor(world.Y), (int)MathF.Floor(world.Z)));

    public static Int3 ChunkMin(Int3 chunkCoord) =>
        new(chunkCoord.X * ChunkSize, chunkCoord.Y * ChunkSize, chunkCoord.Z * ChunkSize);

    public static void WorldToLocal(Int3 world, out Int3 chunk, out int index)
    {
        chunk = ChunkCoord(world);
        Int3 min = ChunkMin(chunk);
        index = Index(world.X - min.X, world.Y - min.Y, world.Z - min.Z);
    }
}
