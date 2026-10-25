using System;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Paint a world cell to a type (Colorizer demo brush, not Kronnect).</summary>
public static class VoxelColorizer
{
    public static bool Paint(VoxelWorldGrid grid, Int3 cell, ushort type)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return grid.TrySetVoxel(cell, type == 0 ? Voxel.Empty : Voxel.Solid(type));
    }

    public static bool Paint(VoxelChunkStreamer streamer, Int3 cell, ushort type)
    {
        ArgumentNullException.ThrowIfNull(streamer);
        if (!Paint(streamer.Grid, cell, type))
            return false;
        streamer.SyncViews();
        return true;
    }
}
