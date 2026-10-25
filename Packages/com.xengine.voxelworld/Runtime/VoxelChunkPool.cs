using System;
using System.Collections.Generic;

namespace XEngine.VoxelWorld;

public static class VoxelChunkPool
{
    private static readonly Stack<VoxelChunk> s_pool = new();

    public static int PooledCount
    {
        get
        {
            lock (s_pool)
                return s_pool.Count;
        }
    }

    public static VoxelChunk Rent()
    {
        lock (s_pool)
            return s_pool.Count > 0 ? s_pool.Pop() : new VoxelChunk();
    }

    public static void Return(VoxelChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        chunk.Clear();
        lock (s_pool)
            s_pool.Push(chunk);
    }
}
