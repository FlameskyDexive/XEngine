using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

public enum ItemCategory
{
    Voxel = 0,
    Torch = 1,
    Model = 2,
    General = 3,
}

[CreateAssetMenu("Voxel World/Item Definition")]
public sealed class ItemDefinition : ScriptableObject
{
    public string Title = "Item";
    public ItemCategory Category = ItemCategory.Voxel;
    public AssetRef<VoxelDefinition> VoxelType;
    public AssetRef<ModelDefinition> Model;
    public AssetRef<Texture2D> Icon;
    public Color Color = Color.White;
    public bool CanBePicked = true;
}
