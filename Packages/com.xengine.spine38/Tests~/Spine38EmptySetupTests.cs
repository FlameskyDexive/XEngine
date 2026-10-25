using System.Reflection;
using Spine;
using XEngine.Runtime;
using Xunit;

namespace XEngine.Spine38.Tests;

public sealed class Spine38EmptySetupTests
{
    [Fact]
    public void EmptySetupPose_CanRevealAndHideAttachmentWithoutRecreatingSkeleton()
    {
        SkeletonData data = Spine38MeshTests.CreateQuadSkeleton().Data;
        data.Slots.Items[0].AttachmentName = null;
        var timeline = new AttachmentTimeline(2) { SlotIndex = 0 };
        timeline.SetFrame(0, 0.25f, "body");
        timeline.SetFrame(1, 0.75f, null);
        var timelines = new ExposedList<Timeline>();
        timelines.Add(timeline);
        data.Animations.Add(new global::Spine.Animation("reveal", timelines, 1f));

        var asset = ScriptableObject.CreateInstance<SkeletonDataAsset>();
        // Model an already imported atlas-backed skeleton without needing GPU resources.
        asset.AtlasAssets.Add(default);
        typeof(SkeletonDataAsset).GetField("_cachedData", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(asset, data);
        var animation = new SkeletonAnimation { SkeletonDataAsset = asset };
        animation.Initialize(true);
        Skeleton skeleton = Assert.IsType<Skeleton>(animation.Skeleton);
        Assert.Null(skeleton.Slots.Items[0].Attachment);
        Assert.NotNull(animation.SetAnimation(0, "reveal", false));
        animation.UpdateAnimation(0.5f);
        Assert.IsType<RegionAttachment>(skeleton.Slots.Items[0].Attachment);
        animation.UpdateAnimation(0.3f);
        Assert.Null(skeleton.Slots.Items[0].Attachment);
        Assert.Same(skeleton, animation.Skeleton);
        Assert.Same(data, asset.GetSkeletonData());
    }
}
