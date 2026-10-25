using Spine;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Spine;

namespace XEngine.Spine.Samples.OtherExamples;

/// <summary>
/// Mock AI restyle: tint every atlas region, pack back into the same layout, swap the page.
/// Bones and .atlas text stay unchanged. Real img2img goes through <see cref="SpineAtlasRestyle.ExtractUnrotated"/>.
/// </summary>
public class AtlasRestyleDemo : MonoBehaviour
{
    public byte TintR = 80;
    public byte TintG = 180;
    public byte TintB = 255;
    public bool ApplyOnStart = true;
    public SkeletonAnimation? SkeletonAnimation;

    public override void Start()
    {
        SkeletonAnimation ??= GetComponent<SkeletonAnimation>();
        SkeletonAnimation?.Initialize(overwrite: false);
        if (ApplyOnStart)
            ApplyMockRestyle();
    }

    public override void Update()
    {
        if (Input.GetKeyDown(KeyCode.R))
            ApplyMockRestyle();
    }

    public bool ApplyMockRestyle()
    {
        SkeletonAnimation ??= GetComponent<SkeletonAnimation>();
        SkeletonDataAsset? data = SkeletonAnimation?.SkeletonDataAsset.ResWeak
            ?? SkeletonAnimation?.SkeletonDataAsset.Res;
        if (data == null || data.AtlasAssets.Count == 0)
            return false;

        SpineAtlasAsset? atlasAsset = data.AtlasAssets[0].ResWeak ?? data.AtlasAssets[0].Res;
        Texture2D? page = atlasAsset != null && atlasAsset.PageTextures.Count > 0
            ? atlasAsset.PageTextures[0].ResWeak ?? atlasAsset.PageTextures[0].Res
            : null;
        if (atlasAsset == null || page == null || Graphics.IsHeadless)
            return false;

        int width = (int)page.Width;
        int height = (int)page.Height;
        byte[] rgba = new byte[width * height * 4];
        page.GetData<byte>(rgba);
        Atlas atlas = atlasAsset.CreateAtlas();
        SpineAtlasRestyle.TintAllRegions(rgba, width, height, atlas, TintR, TintG, TintB, flipY: true);
        var next = new Texture2D((uint)width, (uint)height);
        next.SetData<byte>(rgba.AsMemory());
        SpineAtlasSwap.Apply(atlasAsset, next, SkeletonAnimation?.Skeleton);
        return true;
    }

    public static void RestyleBuffer(byte[] pageRgba, int pageW, int pageH, Atlas atlas, byte r, byte g, byte b)
        => SpineAtlasRestyle.TintAllRegions(pageRgba, pageW, pageH, atlas, r, g, b);
}
