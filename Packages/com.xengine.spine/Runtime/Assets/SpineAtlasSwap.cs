using System;
using Spine;

using XEngine.Runtime.Resources;

namespace XEngine.Spine;

/// <summary>Swap atlas page textures on a live skeleton. Bones, slots, and animations stay put.</summary>
public static class SpineAtlasSwap
{
    public static void Apply(SpineAtlasAsset atlasAsset, Texture2D pageTexture, Skeleton? live = null, int pageIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(atlasAsset);
        atlasAsset.SetPageTexture(pageIndex, pageTexture);
        if (live != null)
            RebindSkeletonPages(live, pageTexture);
    }

    public static int RebindSkeletonPages(Skeleton skeleton, Texture2D texture)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        int n = 0;
        ExposedList<Slot> slots = skeleton.Slots;
        Slot[] items = slots.Items;
        for (int i = 0; i < slots.Count; i++)
        {
            AtlasPage? page = PageOf(items[i].Pose.Attachment);
            if (page == null)
                continue;
            page.rendererObject = texture;
            n++;
        }

        return n;
    }

    private static AtlasPage? PageOf(Attachment? attachment)
    {
        TextureRegion? region = attachment switch
        {
            RegionAttachment r => RegionOf(r),
            MeshAttachment m => RegionOf(m),
            _ => null
        };
        return region is AtlasRegion atlasRegion ? atlasRegion.page : null;
    }

    private static TextureRegion? RegionOf(RegionAttachment attachment)
        => attachment.Sequence?.GetRegion(0);

    private static TextureRegion? RegionOf(MeshAttachment attachment)
        => attachment.Sequence?.GetRegion(0);
}
