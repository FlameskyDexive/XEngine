using System;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Hide ceiling voxels of a type when the camera is inside that volume.</summary>
public static class VoxelSeeThrough
{
    public static bool ShouldHide(Int3 cameraCell, Int3 voxelCell, ushort hideType, ushort voxelType) =>
        hideType != 0 &&
        voxelType == hideType &&
        voxelCell.X == cameraCell.X &&
        voxelCell.Z == cameraCell.Z &&
        voxelCell.Y >= cameraCell.Y;
}
