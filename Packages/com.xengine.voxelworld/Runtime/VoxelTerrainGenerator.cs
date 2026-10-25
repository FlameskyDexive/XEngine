using System;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

public abstract class VoxelTerrainGenerator : ScriptableObject
{
    public int MaxHeight = 255;
    public int MinHeight = -32;
    public int WaterLevel;
    public bool AddWater;

    public abstract void PaintChunk(Span<Voxel> voxels, Int3 chunkMin, ushort terrainTypeIndex);
}
