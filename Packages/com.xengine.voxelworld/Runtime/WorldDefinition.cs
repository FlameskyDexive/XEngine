using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.VoxelWorld;

[CreateAssetMenu("Voxel World/World Definition")]
public sealed class WorldDefinition : ScriptableObject
{
    public int Seed;
    public AssetRef<VoxelTerrainGenerator> TerrainGenerator;
    public List<AssetRef<BiomeDefinition>> Biomes = [];
    public List<AssetRef<VoxelDefinition>> ExtraVoxels = [];
    public List<AssetRef<ItemDefinition>> Items = [];
}
