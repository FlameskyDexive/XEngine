using System;
using System.IO;

using XEngine.Async;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Spine;

namespace XEngine.Spine.Samples.OtherExamples;

/// <summary>Create <see cref="SkeletonDataAsset"/> from JSON + atlas + PNG at play time.</summary>
public class RuntimeLoadFromExportsExample : MonoBehaviour
{
    public string JsonPath = "";
    public string AtlasPath = "";
    public string TexturePath = "";
    public string SkinName = "goblin";
    public string AnimationName = "walk";
    public float DelaySeconds;
    public SkeletonAnimation? Created;

    public override void Start()
    {
        if (DelaySeconds > 0f)
            LoadAfterDelay().Forget();
        else
            LoadNow();
    }

    public SkeletonAnimation? LoadNow()
    {
        if (string.IsNullOrEmpty(JsonPath) || string.IsNullOrEmpty(AtlasPath) || !File.Exists(JsonPath) || !File.Exists(AtlasPath))
            return null;

        Texture2D? page = null;
        if (!string.IsNullOrEmpty(TexturePath) && File.Exists(TexturePath) && !Graphics.IsHeadless)
            page = Texture2D.FromFile(TexturePath);

        SpineAtlasAsset atlas = SpineAtlasAsset.CreateRuntime(File.ReadAllText(AtlasPath), page);
        SkeletonDataAsset data = SkeletonDataAsset.CreateRuntime(File.ReadAllBytes(JsonPath), atlas);
        Created = GameObject.AddComponent<SkeletonAnimation>();
        Created.SkeletonDataAsset = data;
        Created.Initialize(overwrite: true);
        if (Created.Skeleton == null)
            return Created;

        if (!string.IsNullOrEmpty(SkinName))
            Created.Skeleton.SetSkin(SkinName);
        Created.Skeleton.SetupPoseSlots();
        if (!string.IsNullOrEmpty(AnimationName))
            Created.SetAnimation(0, AnimationName, loop: true);
        return Created;
    }

    private async XTaskVoid LoadAfterDelay()
    {
        await XTask.Delay(TimeSpan.FromSeconds(DelaySeconds));
        if (this.IsValid())
            LoadNow();
    }
}
