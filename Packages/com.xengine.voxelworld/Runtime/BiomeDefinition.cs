using XEngine.Runtime;

namespace XEngine.VoxelWorld;

[CreateAssetMenu("Voxel World/Biome Definition")]
public sealed class BiomeDefinition : ScriptableObject
{
    public AssetRef<VoxelDefinition> VoxelTop;
    public AssetRef<VoxelDefinition> VoxelDirt;
    public float AltitudeMin;
    public float AltitudeMax = 1f;
    public float MoistureMin;
    public float MoistureMax = 1f;
}
