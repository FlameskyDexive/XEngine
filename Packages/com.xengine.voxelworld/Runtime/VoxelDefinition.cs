using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

[CreateAssetMenu("Voxel World/Voxel Definition")]
public sealed class VoxelDefinition : ScriptableObject
{
    public string Title = "Voxel";
    public VoxelRenderType RenderType = VoxelRenderType.Opaque;
    public AssetRef<Texture2D> TextureTop;
    public AssetRef<Texture2D> TextureSide;
    public AssetRef<Texture2D> TextureBottom;
    public Color TintColor = Color.White;
    public float Alpha = 1f;
    public byte LightIntensity;
    public bool Hidden;
    public bool CanBeCollected = true;
}
