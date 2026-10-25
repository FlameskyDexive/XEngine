using Spine;

namespace XEngine.Spine38;

/// <summary>Loads skeleton data without resolving atlas regions.</summary>
public sealed class SpineNullAttachmentLoader : AttachmentLoader
{
    public static readonly SpineNullAttachmentLoader Instance = new();

    public RegionAttachment NewRegionAttachment(Skin skin, string name, string path) => null!;
    public MeshAttachment NewMeshAttachment(Skin skin, string name, string path) => null!;
    public BoundingBoxAttachment NewBoundingBoxAttachment(Skin skin, string name) => null!;
    public PathAttachment NewPathAttachment(Skin skin, string name) => null!;
    public PointAttachment NewPointAttachment(Skin skin, string name) => null!;
    public ClippingAttachment NewClippingAttachment(Skin skin, string name) => null!;
}
