using Spine;

using Xunit;

namespace XEngine.Runtime.Test.Spine;

public sealed class SpineCSharpRuntimeTests
{
    private const string MinimalWalkJson = """
        {
          "skeleton": { "hash": "m1", "spine": "4.2.37", "width": 64, "height": 64 },
          "bones": [
            { "name": "root" },
            { "name": "body", "parent": "root", "y": 20 }
          ],
          "slots": [
            { "name": "body", "bone": "body" }
          ],
          "animations": {
            "walk": {
              "bones": {
                "body": {
                  "translate": [
                    { "x": 0, "y": 0 },
                    { "time": 0.5, "x": 10, "y": 0 }
                  ]
                }
              }
            }
          }
        }
        """;

    [Fact]
    public void SkeletonJson_LoadsBonesAndWalkAnimation()
    {
        SkeletonData data = ReadMinimal();
        Assert.Equal("4.2.37", data.Version);
        Assert.NotNull(data.FindBone("root"));
        Assert.NotNull(data.FindBone("body"));
        Assert.NotNull(data.FindAnimation("walk"));
        Assert.True(data.FindAnimation("walk").Duration >= 0.5f);
    }

    [Fact]
    public void AnimationState_WalkMovesBodyBone()
    {
        SkeletonData data = ReadMinimal();
        var skeleton = new Skeleton(data);
        var state = new AnimationState(new AnimationStateData(data));
        state.SetAnimation(0, "walk", loop: false);

        Bone body = skeleton.FindBone("body")!;
        skeleton.UpdateWorldTransform(global::Spine.Physics.Update);
        float startX = body.AppliedPose.WorldX;

        state.Update(0.5f);
        state.Apply(skeleton);
        skeleton.UpdateWorldTransform(global::Spine.Physics.Update);

        Assert.True(body.AppliedPose.WorldX > startX + 5f, $"Expected body WorldX to move, start={startX} now={body.AppliedPose.WorldX}");
    }

    private static SkeletonData ReadMinimal()
    {
        var json = new SkeletonJson(new NullAttachmentLoader());
        using var reader = new StringReader(MinimalWalkJson);
        return json.ReadSkeletonData(reader);
    }

    private sealed class NullAttachmentLoader : AttachmentLoader
    {
        public RegionAttachment NewRegionAttachment(Skin skin, string placeholder, string name, string path, Sequence sequence) => null!;
        public MeshAttachment NewMeshAttachment(Skin skin, string placeholder, string name, string path, Sequence sequence) => null!;
        public BoundingBoxAttachment NewBoundingBoxAttachment(Skin skin, string placeholder, string name) => null!;
        public PathAttachment NewPathAttachment(Skin skin, string placeholder, string name) => null!;
        public PointAttachment NewPointAttachment(Skin skin, string placeholder, string name) => null!;
        public ClippingAttachment NewClippingAttachment(Skin skin, string placeholder, string name) => null!;
    }
}
