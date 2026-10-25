using System;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

[CreateAssetMenu("Voxel World/Flat Terrain Generator")]
public sealed class FlatTerrainGenerator : VoxelTerrainGenerator
{
    public int Altitude = 50;
    public ushort SurfaceTypeIndex;
    public AssetRef<VoxelDefinition> TerrainVoxel;
    public Color TintA = Color.White;
    public Color TintB = Color.White;

    public override void PaintChunk(Span<Voxel> voxels, Int3 chunkMin, ushort terrainTypeIndex)
    {
        if (voxels.Length < VoxelCoords.ChunkVoxelCount)
            throw new ArgumentException($"Chunk buffer must hold {VoxelCoords.ChunkVoxelCount} voxels.", nameof(voxels));

        for (int y = 0; y < VoxelCoords.ChunkSize; y++)
        {
            int worldY = chunkMin.Y + y;
            bool solid = worldY >= 0 && worldY < Altitude;
            ushort type = terrainTypeIndex;
            if (solid && SurfaceTypeIndex != 0 && worldY == Altitude - 1)
                type = SurfaceTypeIndex;
            Voxel cell = solid ? Voxel.Solid(type) : Voxel.Empty;
            for (int z = 0; z < VoxelCoords.ChunkSize; z++)
            {
                for (int x = 0; x < VoxelCoords.ChunkSize; x++)
                    voxels[VoxelCoords.Index(x, y, z)] = cell;
            }
        }
    }
}
