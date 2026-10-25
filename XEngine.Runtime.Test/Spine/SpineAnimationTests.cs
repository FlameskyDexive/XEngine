using System.Text;

using Spine;

using XEngine.Runtime;
using XEngine.Spine;

using Xunit;

namespace XEngine.Runtime.Test.Spine;

public sealed class SpineAnimationTests : RuntimeTestBase
{
    private const string DualTrackJson = """
        {
          "skeleton": { "hash": "m4", "spine": "4.2.37", "width": 64, "height": 64 },
          "bones": [
            { "name": "root" },
            { "name": "body", "parent": "root", "y": 20 },
            { "name": "head", "parent": "body", "y": 10 }
          ],
          "slots": [
            { "name": "body", "bone": "body" },
            { "name": "head", "bone": "head" }
          ],
          "events": {
            "footstep": { "int": 1, "string": "left" }
          },
          "skins": [
            { "name": "default" }
          ],
          "animations": {
            "idle": {
              "bones": {
                "body": {
                  "translate": [
                    { "x": 0, "y": 0 },
                    { "time": 0.2, "x": 0, "y": 0 }
                  ]
                }
              }
            },
            "walk": {
              "bones": {
                "body": {
                  "translate": [
                    { "x": 0, "y": 0 },
                    { "time": 0.5, "x": 10, "y": 0 }
                  ]
                }
              },
              "events": [
                { "name": "footstep", "time": 0.25, "int": 2 }
              ]
            },
            "look": {
              "bones": {
                "head": {
                  "translate": [
                    { "x": 0, "y": 0 },
                    { "time": 0.4, "x": 0, "y": 8 }
                  ]
                }
              }
            }
          }
        }
        """;

    [Fact]
    public void SetAnimation_WalkMovesBodyBone()
    {
        SkeletonAnimation anim = CreateAnimation();
        Bone body = anim.Skeleton!.FindBone("body")!;
        anim.Skeleton.UpdateWorldTransform(global::Spine.Physics.Update);
        float startX = body.AppliedPose.WorldX;

        Assert.NotNull(anim.SetAnimation(0, "walk", loop: false));
        anim.UpdateAnimation(0.5f);
        Assert.True(body.AppliedPose.WorldX > startX + 5f, $"Expected walk to move body, start={startX} now={body.AppliedPose.WorldX}");
        Assert.Equal("walk", anim.AnimationState.GetTrack(0)!.Animation.Name);
    }

    [Fact]
    public void AddAnimation_QueuesWalkAfterIdle()
    {
        SkeletonAnimation anim = CreateAnimation();
        anim.AnimationState.Data.DefaultMix = 0f;
        Assert.NotNull(anim.SetAnimation(0, "idle", loop: false));
        Assert.NotNull(anim.AddAnimation(0, "walk", loop: false, delay: 0f));

        anim.UpdateAnimation(0.1f);
        Assert.Equal("idle", anim.AnimationState.GetTrack(0)!.Animation.Name);

        // AnimationState promotes the queued clip when trackLast (previous apply) passes delay.
        anim.UpdateAnimation(0.15f);
        anim.UpdateAnimation(0.01f);
        Assert.Equal("walk", anim.AnimationState.GetTrack(0)!.Animation.Name);
    }

    [Fact]
    public void MultiTrack_WalkAndLookApplyIndependently()
    {
        SkeletonAnimation anim = CreateAnimation();
        Bone body = anim.Skeleton!.FindBone("body")!;
        Bone head = anim.Skeleton.FindBone("head")!;
        anim.Skeleton.UpdateWorldTransform(global::Spine.Physics.Update);
        float startBodyX = body.AppliedPose.WorldX;
        float startHeadY = head.AppliedPose.WorldY;

        Assert.NotNull(anim.SetAnimation(0, "walk", loop: false));
        Assert.NotNull(anim.SetAnimation(1, "look", loop: false));
        anim.UpdateAnimation(0.5f);

        Assert.True(body.AppliedPose.WorldX > startBodyX + 5f);
        Assert.True(head.AppliedPose.WorldY > startHeadY + 4f);
        Assert.Equal("walk", anim.AnimationState.GetTrack(0)!.Animation.Name);
        Assert.Equal("look", anim.AnimationState.GetTrack(1)!.Animation.Name);
    }

    [Fact]
    public void AnimationEvent_FiresFootstep()
    {
        SkeletonAnimation anim = CreateAnimation();
        string? fired = null;
        int payload = 0;
        anim.AnimationEvent += (_, e) =>
        {
            fired = e.Data.Name;
            payload = e.Int;
        };

        Assert.NotNull(anim.SetAnimation(0, "walk", loop: false));
        anim.UpdateAnimation(0.3f);
        Assert.Equal("footstep", fired);
        Assert.Equal(2, payload);
    }

    [Fact]
    public void AnimationReferenceAsset_ResolvesAndPlaysWalk()
    {
        SkeletonDataAsset data = CreateDataAsset();
        var reference = ScriptableObject.CreateInstance<AnimationReferenceAsset>();
        reference.SkeletonDataAsset = data;
        reference.AnimationName = "walk";
        Assert.Equal("walk", reference.GetAnimation()!.Name);

        SkeletonAnimation anim = CreateAnimation(data);
        Assert.NotNull(anim.SetAnimation(0, reference, loop: false));
        Bone body = anim.Skeleton!.FindBone("body")!;
        float startX = body.AppliedPose.WorldX;
        anim.UpdateAnimation(0.5f);
        Assert.True(body.AppliedPose.WorldX > startX + 5f);
    }

    [Fact]
    public void LateUpdate_AdvancesWithDeltaTime()
    {
        SkeletonAnimation anim = CreateAnimation();
        Bone body = anim.Skeleton!.FindBone("body")!;
        anim.Skeleton.UpdateWorldTransform(global::Spine.Physics.Update);
        float startX = body.AppliedPose.WorldX;
        Assert.NotNull(anim.SetAnimation(0, "walk", loop: false));

        int frames = (int)Math.Ceiling(0.5f / Time.DeltaTime);
        for (int i = 0; i < frames; i++)
            anim.LateUpdate();

        Assert.True(body.AppliedPose.WorldX > startX + 5f);
    }

    [Fact]
    public void AnimationEvidence_WritesM4Screenshot()
    {
        SkeletonAnimation anim = CreateAnimation();
        Bone body = anim.Skeleton!.FindBone("body")!;
        Bone head = anim.Skeleton.FindBone("head")!;
        anim.SetAnimation(0, "walk", loop: false);
        anim.SetAnimation(1, "look", loop: false);
        anim.UpdateAnimation(0f);
        float x0 = body.AppliedPose.WorldX;
        float y0 = head.AppliedPose.WorldY;
        anim.UpdateAnimation(0.5f);
        float x1 = body.AppliedPose.WorldX;
        float y1 = head.AppliedPose.WorldY;

        const int width = 320;
        const int height = 140;
        byte[] rgba = new byte[width * height * 4];
        SpineEvidenceImage.Fill(rgba, width, 0, 0, width, height, 24, 26, 32);
        SpineEvidenceImage.Fill(rgba, width, 0, 0, width / 2, height, 28, 32, 40);
        DrawPose(rgba, width, height, 0, x0, y0, 80, 168, 255);
        DrawPose(rgba, width, height, width / 2, x1, y1, 72, 196, 120);

        string repo = SpineEvidenceImage.FindRepositoryRoot();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "spine");
        Directory.CreateDirectory(dir);
        string pngPath = Path.Combine(dir, "m4-skeleton-animation.png");
        File.WriteAllBytes(pngPath, SpineEvidenceImage.EncodePng(rgba, width, height));
        Assert.True(new FileInfo(pngPath).Length > 256);
        Assert.True(x1 > x0 + 5f);
        Assert.True(y1 > y0 + 4f);

        File.WriteAllText(Path.Combine(dir, "m4-skeleton-animation.md"),
            "# M4 SkeletonAnimation\n\n" +
            "Date: 2026-08-16. Branch: `codex/animation-module`.\n\n" +
            "`SkeletonAnimation.SetAnimation` / `AddAnimation` drive `AnimationState` in LateUpdate. " +
            "Left: t=0 setup pose. Right: t=0.5 after track 0 `walk` (body +X) and track 1 `look` (head +Y). " +
            "Events fire (`footstep`); `AnimationReferenceAsset.GetAnimation` resolves named clips.\n\n" +
            "Screenshot: [m4-skeleton-animation.png](m4-skeleton-animation.png).\n");
    }

    private SkeletonAnimation CreateAnimation(SkeletonDataAsset? data = null)
    {
        data ??= CreateDataAsset();
        GameObject go = CreateGameObject("SpineAnim");
        var anim = go.AddComponent<SkeletonAnimation>();
        anim.SkeletonDataAsset = data;
        anim.Initialize(overwrite: true);
        Assert.NotNull(anim.Skeleton);
        Assert.NotNull(anim.AnimationState);
        return anim;
    }

    private static SkeletonDataAsset CreateDataAsset()
    {
        var asset = ScriptableObject.CreateInstance<SkeletonDataAsset>();
        asset.Scale = 1f;
        asset.IsBinary = false;
        asset.SkeletonBytes = Encoding.UTF8.GetBytes(DualTrackJson);
        asset.AnimationNames.Add("idle");
        asset.AnimationNames.Add("walk");
        asset.AnimationNames.Add("look");
        asset.EventNames.Add("footstep");
        return asset;
    }

    private static void DrawPose(byte[] rgba, int width, int height, int originX, float bodyX, float headY, byte r, byte g, byte b)
    {
        int baseX = originX + 36;
        int baseY = height - 32;
        int cx = baseX + (int)Math.Round(bodyX * 10f);
        int cy = baseY;
        int hx = cx;
        int hy = baseY - 36 - (int)Math.Round((headY - 30f) * 4f);
        SpineEvidenceImage.Fill(rgba, width, originX + 24, baseY + 12, 110, 3, 48, 52, 60);
        DrawStem(rgba, width, height, hx, hy, cx, cy, r, g, b);
        SpineEvidenceImage.FillCircle(rgba, width, height, cx, cy, 11, r, g, b);
        SpineEvidenceImage.FillCircle(rgba, width, height, hx, hy, 8, 255, 220, 96);
    }

    private static void DrawStem(byte[] rgba, int width, int height, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        if (steps <= 0)
            return;
        for (int i = 0; i <= steps; i++)
        {
            int x = x0 + (x1 - x0) * i / steps;
            int y = y0 + (y1 - y0) * i / steps;
            SpineEvidenceImage.FillCircle(rgba, width, height, x, y, 2, r, g, b);
        }
    }
}
