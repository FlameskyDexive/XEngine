using Spine;

namespace XEngine.Spine;

/// <summary>
/// Loads skeleton data with real but texture-less attachments. Spine 4.x requires every attachment
/// referenced by skins/animation timelines to exist during parse ("Timeline attachment not found"),
/// so <see cref="SpineNullAttachmentLoader"/> fails on exports whose deform timelines reference
/// attachments outside the default skin. Use this when only structure (bones/skins/animations) is
/// needed — e.g. the importer, which stores raw bytes and re-parses with the real atlas at runtime.
/// </summary>
public sealed class SpineStubAttachmentLoader : AttachmentLoader
{
    public static readonly SpineStubAttachmentLoader Instance = new();

    private SpineStubAttachmentLoader() { }

    public RegionAttachment NewRegionAttachment(Skin skin, string placeholder, string name, string path, Sequence sequence)
        => new(name, sequence ?? new Sequence(1, false));

    public MeshAttachment NewMeshAttachment(Skin skin, string placeholder, string name, string path, Sequence sequence)
        => new(name, sequence ?? new Sequence(1, false));

    public BoundingBoxAttachment NewBoundingBoxAttachment(Skin skin, string placeholder, string name)
        => new(name);

    public PathAttachment NewPathAttachment(Skin skin, string placeholder, string name)
        => new(name);

    public PointAttachment NewPointAttachment(Skin skin, string placeholder, string name)
        => new(name);

    public ClippingAttachment NewClippingAttachment(Skin skin, string placeholder, string name)
        => new(name);
}
