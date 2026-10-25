using Spine;

namespace XEngine.Spine38;

/// <summary>Texture-less attachments so import can list bones / skins / animations without an atlas.</summary>
public sealed class SpineStubAttachmentLoader : AttachmentLoader
{
    public static readonly SpineStubAttachmentLoader Instance = new();

    private SpineStubAttachmentLoader() { }

    public RegionAttachment NewRegionAttachment(Skin skin, string name, string path) => new(name);
    public MeshAttachment NewMeshAttachment(Skin skin, string name, string path) => new(name);
    public BoundingBoxAttachment NewBoundingBoxAttachment(Skin skin, string name) => new(name);
    public PathAttachment NewPathAttachment(Skin skin, string name) => new(name);
    public PointAttachment NewPointAttachment(Skin skin, string name) => new(name);
    public ClippingAttachment NewClippingAttachment(Skin skin, string name) => new(name);
}
