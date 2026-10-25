using Spine;

namespace XEngine.Spine;

/// <summary>Loads skeleton data without resolving atlas regions (slots / bones / animations still parse).</summary>
public sealed class SpineNullAttachmentLoader : AttachmentLoader
{
    public static readonly SpineNullAttachmentLoader Instance = new();

    public RegionAttachment NewRegionAttachment(Skin skin, string placeholder, string name, string path, Sequence sequence) => null!;
    public MeshAttachment NewMeshAttachment(Skin skin, string placeholder, string name, string path, Sequence sequence) => null!;
    public BoundingBoxAttachment NewBoundingBoxAttachment(Skin skin, string placeholder, string name) => null!;
    public PathAttachment NewPathAttachment(Skin skin, string placeholder, string name) => null!;
    public PointAttachment NewPointAttachment(Skin skin, string placeholder, string name) => null!;
    public ClippingAttachment NewClippingAttachment(Skin skin, string placeholder, string name) => null!;
}
