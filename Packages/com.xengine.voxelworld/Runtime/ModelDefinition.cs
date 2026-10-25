using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

public struct ModelBit
{
    public int VoxelIndex;
    public AssetRef<VoxelDefinition> Voxel;
    public bool IsEmpty;
    public Color Color;
}

[CreateAssetMenu("Voxel World/Model Definition")]
public sealed class ModelDefinition : ScriptableObject
{
    public int SizeX = VoxelCoords.ChunkSize;
    public int SizeY = VoxelCoords.ChunkSize;
    public int SizeZ = VoxelCoords.ChunkSize;
    public int OffsetX;
    public int OffsetY;
    public int OffsetZ;
    public List<ModelBit> Bits = [];
}
