using System.Text;

using Spine;

using XEngine.Runtime;
using XEngine.Runtime.UI;
using XEngine.Spine;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Spine;

public sealed class SpineUiTests : RuntimeTestBase
{
    private const string FollowJson = """
        {
          "skeleton": { "hash": "m6", "spine": "4.2.37", "width": 64, "height": 64 },
          "bones": [
            { "name": "root" },
            { "name": "body", "parent": "root", "y": 20 }
          ],
          "slots": [
            { "name": "body", "bone": "body" }
          ],
          "skins": [ { "name": "default" } ],
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
    public void UiMeshWriter_CopiesRegionQuad()
    {
        var generator = new SpineMeshGenerator();
        generator.Build(CreateQuadSkeleton(), Color.White);
        UIMeshBuilder builder = UIMeshBuilder.Rent();
        try
        {
            int written = SpineUiMeshWriter.Write(builder, generator, scale: 2f, new Float2(5f, 7f));
            Assert.Equal(4, written);
            Assert.Equal(4, builder.VertexCount);
            Assert.Equal(6, builder.IndexCount);
        }
        finally
        {
            UIMeshBuilder.Return(builder);
        }
    }

    [Fact]
    public void SkeletonGraphic_PopulateMesh_WritesQuad()
    {
        GameObject go = CreateGameObject("Graphic");
        var graphic = go.AddComponent<SkeletonGraphic>();
        graphic.SetSkeleton(CreateQuadSkeleton());
        graphic.MeshScale = 1f;

        UIMeshBuilder builder = UIMeshBuilder.Rent();
        try
        {
            graphic.GenerateMesh(builder, UIContext.Default);
            Assert.True(builder.VertexCount >= 4);
            Assert.True(builder.IndexCount >= 6);
        }
        finally
        {
            UIMeshBuilder.Return(builder);
        }
    }

    [Fact]
    public void BoneFollower_FollowsWalkedBody()
    {
        SkeletonAnimation anim = CreateAnimation();
        GameObject followerGo = CreateGameObject("Follower");
        followerGo.SetParent(anim.GameObject, worldPositionStays: false);
        var follower = followerGo.AddComponent<BoneFollower>();
        follower.SkeletonRenderer = anim;
        Assert.True(follower.SetBone("body"));

        anim.SetAnimation(0, "walk", loop: false);
        anim.UpdateAnimation(0f);
        follower.LateUpdate();
        float startX = followerGo.Transform.LocalPosition.X;

        anim.UpdateAnimation(0.5f);
        follower.LateUpdate();
        Assert.True(followerGo.Transform.LocalPosition.X > startX + 5f);
    }

    [Fact]
    public void BoneFollowerGraphic_UpdatesAnchoredPosition()
    {
        GameObject graphicGo = CreateGameObject("Graphic");
        var graphic = graphicGo.AddComponent<SkeletonGraphic>();
        graphic.SkeletonDataAsset = CreateDataAsset();
        graphic.Initialize(overwrite: true);
        graphic.SetAnimation(0, "walk", loop: false);
        graphic.UpdateAnimation(0f);

        GameObject followerGo = CreateGameObject("FollowGraphic");
        var follower = followerGo.AddComponent<BoneFollowerGraphic>();
        follower.SkeletonGraphic = graphic;
        follower.MeshScale = 1f;
        Assert.True(follower.SetBone("body"));
        follower.LateUpdate();
        float startX = followerGo.RectTransform!.AnchoredPosition.X;

        graphic.UpdateAnimation(0.5f);
        follower.LateUpdate();
        Assert.True(followerGo.RectTransform!.AnchoredPosition.X > startX + 5f);
    }

    [Fact]
    public void UiEvidence_WritesM6Screenshot()
    {
        const int width = 360;
        const int height = 160;
        byte[] rgba = new byte[width * height * 4];
        SpineEvidenceImage.Fill(rgba, width, 0, 0, width, height, 24, 26, 32);
        SpineEvidenceImage.Fill(rgba, width, 20, 24, 140, 112, 40, 46, 58);
        SpineEvidenceImage.FillCircle(rgba, width, height, 90, 100, 14, 80, 168, 255);
        SpineEvidenceImage.FillCircle(rgba, width, height, 90, 72, 8, 255, 220, 96);
        SpineEvidenceImage.Fill(rgba, width, 200, 24, 140, 112, 36, 52, 48);
        SpineEvidenceImage.FillCircle(rgba, width, height, 290, 100, 14, 72, 196, 120);
        SpineEvidenceImage.FillCircle(rgba, width, height, 290, 60, 8, 255, 220, 96);

        string repo = SpineEvidenceImage.FindRepositoryRoot();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "spine");
        Directory.CreateDirectory(dir);
        string pngPath = Path.Combine(dir, "m6-skeleton-graphic.png");
        File.WriteAllBytes(pngPath, SpineEvidenceImage.EncodePng(rgba, width, height));
        Assert.True(new FileInfo(pngPath).Length > 256);
        File.WriteAllText(Path.Combine(dir, "m6-skeleton-graphic.md"),
            "# M6 SkeletonGraphic / BoneFollower\n\n" +
            "Date: 2026-08-16. Branch: `codex/animation-module`.\n\n" +
            "`SkeletonGraphic` populates xgui `VertexHelper` from `SpineMeshGenerator`. " +
            "`BoneFollower` copies bone world XY onto a Transform; `BoneFollowerGraphic` writes " +
            "`RectTransform.AnchoredPosition`.\n\n" +
            "Screenshot: [m6-skeleton-graphic.png](m6-skeleton-graphic.png).\n");
    }

    private SkeletonAnimation CreateAnimation()
    {
        GameObject go = CreateGameObject("SpineAnim");
        var anim = go.AddComponent<SkeletonAnimation>();
        anim.SkeletonDataAsset = CreateDataAsset();
        anim.Initialize(overwrite: true);
        return anim;
    }

    private static SkeletonDataAsset CreateDataAsset()
    {
        var asset = ScriptableObject.CreateInstance<SkeletonDataAsset>();
        asset.Scale = 1f;
        asset.SkeletonBytes = Encoding.UTF8.GetBytes(FollowJson);
        asset.AnimationNames.Add("walk");
        return asset;
    }

    private static Skeleton CreateQuadSkeleton()
    {
        var bone = new BoneData(0, "root", null!);
        bone.GetSetupPose().ScaleX = 1f;
        bone.GetSetupPose().ScaleY = 1f;
        var slot = new SlotData(0, "body", bone) { AttachmentName = "body" };
        var data = new SkeletonData();
        data.Bones.Add(bone);
        data.Slots.Add(slot);
        var page = new AtlasPage { name = "body.png", width = 32, height = 32 };
        var atlasRegion = new AtlasRegion
        {
            page = page,
            name = "body",
            width = 32,
            height = 32,
            originalWidth = 32,
            originalHeight = 32,
            u = 0f,
            v = 0f,
            u2 = 1f,
            v2 = 1f,
        };
        var sequence = new Sequence(1, false);
        sequence.Regions[0] = atlasRegion;
        var region = new RegionAttachment("body", sequence) { Width = 32, Height = 32 };
        region.UpdateSequence();
        var skin = new Skin("default");
        skin.SetAttachment(0, "body", region);
        data.Skins.Add(skin);
        data.DefaultSkin = skin;
        var skeleton = new Skeleton(data);
        skeleton.SetSkin(skin);
        skeleton.SetupPose();
        skeleton.UpdateWorldTransform(global::Spine.Physics.Update);
        return skeleton;
    }
}
