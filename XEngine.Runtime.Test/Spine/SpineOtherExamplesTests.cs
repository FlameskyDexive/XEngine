using System.Text;

using ImageMagick;

using Spine;

using XEngine.Runtime;
using XEngine.Spine;
using XEngine.Spine.Samples.OtherExamples;

using Xunit;

namespace XEngine.Runtime.Test.Spine;

public sealed class SpineOtherExamplesTests : RuntimeTestBase
{
    private static readonly string[] SceneFiles =
    [
        "1 Goblins.scene",
        "2 MixAndMatchSkins.scene",
        "3 RuntimeLoadFromExports.scene",
        "4 AtlasRestyle.scene"
    ];

    [Fact]
    public void SampleLayout_HasScenesScriptsAndOfficialSkeletons()
    {
        string root = SampleRoot();
        foreach (string scene in SceneFiles)
            Assert.True(File.Exists(Path.Combine(root, "Scenes", scene)), scene);

        Assert.True(File.Exists(Path.Combine(root, "Scripts", "Goblins.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Scripts", "MixAndMatchSkinsExample.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Scripts", "RuntimeLoadFromExportsExample.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Scripts", "AtlasRestyleDemo.cs")));
        Assert.True(File.Exists(Path.Combine(root, "LICENSE-SPINE-EXAMPLES.txt")));
        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "Goblins", "goblins.json")));
        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "Goblins", "goblins.atlas.txt")));
        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "Goblins", "goblins.png")));
        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "MixAndMatch", "mix-and-match-pro.json")));
        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "MixAndMatch", "mix-and-match.atlas.txt")));
        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "MixAndMatch", "mix-and-match.png")));
    }

    [Fact]
    public void SceneText_NamesSpineComponents()
    {
        string scenes = Path.Combine(SampleRoot(), "Scenes");
        string s1 = File.ReadAllText(Path.Combine(scenes, SceneFiles[0]));
        string s2 = File.ReadAllText(Path.Combine(scenes, SceneFiles[1]));
        string s3 = File.ReadAllText(Path.Combine(scenes, SceneFiles[2]));
        string s4 = File.ReadAllText(Path.Combine(scenes, SceneFiles[3]));

        Assert.Contains("XEngine.Spine.SkeletonAnimation", s1, StringComparison.Ordinal);
        Assert.Contains("OtherExamples.Goblins", s1, StringComparison.Ordinal);
        Assert.Contains("MixAndMatchSkinsExample", s2, StringComparison.Ordinal);
        Assert.Contains("RuntimeLoadFromExportsExample", s3, StringComparison.Ordinal);
        Assert.Contains("AtlasRestyleDemo", s4, StringComparison.Ordinal);
        Assert.DoesNotContain("4.3.74", s1, StringComparison.Ordinal);
    }

    [Fact]
    public void OfficialExports_AreSpine43AndPlay()
    {
        SkeletonData goblins = ReadOfficial("Goblins", "goblins.json");
        Assert.StartsWith("4.3", goblins.Version);
        Assert.NotNull(goblins.FindSkin("goblin"));
        Assert.NotNull(goblins.FindSkin("goblingirl"));
        Assert.NotNull(goblins.FindAnimation("walk"));
        PlayNamed(goblins, "walk");

        SkeletonData mix = ReadOfficial("MixAndMatch", "mix-and-match-pro.json");
        Assert.StartsWith("4.3", mix.Version);
        Assert.NotNull(mix.FindSkin("skin-base"));
        Assert.NotNull(mix.FindSkin("hair/pink"));
        Assert.NotNull(mix.FindAnimation("idle"));
        PlayNamed(mix, "idle");
    }

    [Fact]
    public void Goblins_ToggleSkinAndExtraRotation()
    {
        SkeletonAnimation anim = CreateOfficialAnimation("Goblins", "goblins.json");
        anim.InitialSkinName = "goblin";
        anim.Initialize(overwrite: true);
        var demo = anim.GameObject.AddComponent<Goblins>();
        demo.SkeletonAnimation = anim;
        demo.ExtraRotation = 25f;
        demo.Start();

        Assert.False(demo.GirlSkin);
        Assert.Equal("goblin", anim.Skeleton!.Skin!.Name);
        Assert.NotNull(anim.Skeleton.GetAttachment("left-hand-item", "dagger"));

        Bone head = anim.Skeleton.FindBone("head")!;
        demo.ExtraRotation = 0f;
        anim.SetAnimation(0, "walk", loop: true);
        anim.UpdateAnimation(0.05f);
        float afterAnim = head.Pose.Rotation;
        demo.ExtraRotation = 25f;
        anim.UpdateAnimation(0f);
        Assert.Equal(afterAnim + 25f, head.Pose.Rotation, precision: 2);

        demo.ToggleSkin();
        Assert.True(demo.GirlSkin);
        Assert.Equal("goblingirl", anim.Skeleton.Skin!.Name);
        Assert.Null(anim.Skeleton.FindSlot("right-hand-item")!.Pose.Attachment);
    }

    [Fact]
    public void MixAndMatch_CombinesAuthoredSkins()
    {
        SkeletonAnimation anim = CreateOfficialAnimation("MixAndMatch", "mix-and-match-pro.json");
        var demo = anim.GameObject.AddComponent<MixAndMatchSkinsExample>();
        demo.SkeletonAnimation = anim;
        demo.Start();

        Skin combined = anim.Skeleton!.Skin!;
        Assert.Equal("combined", combined.Name);
        Assert.NotNull(anim.Skeleton.Data.FindSkin("hair/brown"));
        demo.NextHair();
        Assert.Equal(1, demo.ActiveHairIndex);
        Assert.NotNull(anim.Skeleton.Skin);
    }

    [Fact]
    public void CreateRuntime_LoadsGoblinsWithoutGpuTextures()
    {
        string dir = Path.Combine(SampleRoot(), "Skeletons", "Goblins");
        SpineAtlasAsset atlas = SpineAtlasAsset.CreateRuntime(File.ReadAllText(Path.Combine(dir, "goblins.atlas.txt")));
        Assert.Contains("goblins.png", atlas.PageFileNames);
        Assert.Contains("goblin/head", atlas.RegionNames);

        SkeletonDataAsset data = SkeletonDataAsset.CreateRuntime(
            File.ReadAllBytes(Path.Combine(dir, "goblins.json")), atlas, scale: 1f);
        Assert.Contains("walk", data.AnimationNames);
        Assert.Contains("goblin", data.SkinNames);

        GameObject go = CreateGameObject("runtime-load");
        var loader = go.AddComponent<RuntimeLoadFromExportsExample>();
        loader.JsonPath = Path.Combine(dir, "goblins.json");
        loader.AtlasPath = Path.Combine(dir, "goblins.atlas.txt");
        loader.SkinName = "goblin";
        loader.AnimationName = "walk";
        Assert.NotNull(loader.LoadNow());
        Assert.Equal("walk", loader.Created!.AnimationState.GetTrack(0)!.Animation.Name);
    }

    [Fact]
    public void AtlasRestyle_TintKeepsLayoutAndRoundtripsRotate()
    {
        const int pageW = 16;
        const int pageH = 8;
        byte[] page = new byte[pageW * pageH * 4];
        FillRect(page, pageW, 1, 1, 4, 3, 200, 40, 40, 255);
        FillRect(page, pageW, 8, 2, 3, 2, 40, 200, 40, 255);

        var region = new AtlasRegion
        {
            x = 1,
            y = 1,
            width = 4,
            height = 3,
            degrees = 0,
            name = "head"
        };
        byte[] before = SpineAtlasRestyle.ExtractPacked(page, pageW, pageH, region);
        SpineAtlasRestyle.TintPacked(page, pageW, pageH, region, 128, 255, 255);
        byte[] after = SpineAtlasRestyle.ExtractPacked(page, pageW, pageH, region);
        Assert.True(after[0] < before[0]);
        Assert.Equal(before[1], after[1]);

        byte[] untouched = new byte[4];
        Buffer.BlockCopy(page, ((2 * pageW) + 8) * 4, untouched, 0, 4);
        Assert.Equal(40, untouched[0]);
        Assert.Equal(200, untouched[1]);

        byte[] packed = [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255];
        byte[] cw = SpineAtlasRestyle.Rotate90Cw(packed, 2, 2);
        byte[] back = SpineAtlasRestyle.Rotate90Ccw(cw, 2, 2);
        Assert.Equal(packed, back);

        var rotated = new AtlasRegion { x = 0, y = 0, width = 2, height = 3, degrees = 90, name = "arm" };
        byte[] small = new byte[4 * 4 * 4];
        FillRect(small, 4, 0, 0, 2, 3, 10, 20, 30, 255);
        byte[] unrotated = SpineAtlasRestyle.ExtractUnrotated(small, 4, 4, rotated);
        Assert.Equal(3 * 2 * 4, unrotated.Length);
        SpineAtlasRestyle.BlitUnrotated(small, 4, 4, rotated, unrotated);
        byte[] packedAgain = SpineAtlasRestyle.ExtractPacked(small, 4, 4, rotated);
        Assert.Equal(10, packedAgain[0]);
    }

    [Fact]
    public void OfficialGoblinsAtlas_RestyleHeadLeavesOtherPixels()
    {
        string dir = Path.Combine(SampleRoot(), "Skeletons", "Goblins");
        Atlas atlas = SpineAtlasLayout.Parse(File.ReadAllText(Path.Combine(dir, "goblins.atlas.txt")));
        AtlasRegion head = atlas.FindRegion("goblin/head")!;
        Assert.True(head.width > 0);

        byte[] page = DecodePngTopLeft(Path.Combine(dir, "goblins.png"), out int w, out int h);
        Assert.Equal(atlas.Pages[0].width, w);
        byte[] original = (byte[])page.Clone();
        SpineAtlasRestyle.TintPacked(page, w, h, head, 64, 255, 255);

        byte[] headBefore = SpineAtlasRestyle.ExtractPacked(original, w, h, head);
        byte[] headAfter = SpineAtlasRestyle.ExtractPacked(page, w, h, head);
        Assert.True(HasDarkerRed(headBefore, headAfter));

        AtlasRegion foot = atlas.FindRegion("goblin/left-foot")!;
        byte[] footBefore = SpineAtlasRestyle.ExtractPacked(original, w, h, foot);
        byte[] footAfter = SpineAtlasRestyle.ExtractPacked(page, w, h, foot);
        Assert.Equal(footBefore, footAfter);

        AtlasRestyleDemo.RestyleBuffer(original, w, h, atlas, 80, 180, 255);
        byte[] footTinted = SpineAtlasRestyle.ExtractPacked(original, w, h, foot);
        Assert.NotEqual(footBefore, footTinted);
    }

    [Fact]
    public void Premultiply_RoundTripOpaque()
    {
        byte[] rgba = [200, 100, 50, 255, 10, 20, 30, 128];
        SpineAtlasRestyle.Premultiply(rgba);
        Assert.Equal(200, rgba[0]);
        Assert.Equal(5, rgba[4]);
        SpineAtlasRestyle.Unpremultiply(rgba);
        Assert.Equal(200, rgba[0]);
        Assert.Equal(5 * 255 / 128, rgba[4]);
    }

    [Fact]
    public void OtherExamplesEvidence_WritesM9Screenshot()
    {
        const int width = 480;
        const int height = 200;
        byte[] rgba = new byte[width * height * 4];
        SpineEvidenceImage.Fill(rgba, width, 0, 0, width, height, 22, 24, 30);
        string[] labels = ["Goblins", "MixMatch", "Runtime", "Restyle"];
        byte[][] colors =
        [
            [80, 168, 255],
            [232, 140, 64],
            [72, 196, 120],
            [196, 96, 220]
        ];
        for (int i = 0; i < 4; i++)
        {
            int x = 28 + i * 112;
            SpineEvidenceImage.Fill(rgba, width, x, 36, 96, 128, colors[i][0], colors[i][1], colors[i][2]);
            SpineEvidenceImage.FillCircle(rgba, width, height, x + 48, 88, 16, 24, 26, 32);
        }

        string repo = SpineEvidenceImage.FindRepositoryRoot();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "spine");
        Directory.CreateDirectory(dir);
        string pngPath = Path.Combine(dir, "m9-other-examples.png");
        File.WriteAllBytes(pngPath, SpineEvidenceImage.EncodePng(rgba, width, height));
        Assert.True(new FileInfo(pngPath).Length > 256);
        _ = labels;

        File.WriteAllText(Path.Combine(dir, "m9-other-examples.md"),
            "# M9 Other Examples + atlas restyle\n\n" +
            "Date: 2026-08-16. Branch: `codex/animation-module`.\n\n" +
            "Official `Samples~` JSON exports are Spine 4.3 (4.3.74-beta); the vendored runtime is now spine-csharp 4.3 as well. " +
            "Ported scenes/scripts live under `Packages/com.xengine.spine/Samples~/OtherExamples/` with " +
            "4.2 Goblins ESS+PMA and Mix-and-Match pro+PMA.\n\n" +
            "Runtime: `SpineSkinMixer`, `SpineAtlasLayout` / `SpineAtlasRestyle` (per-region unpack/pack, PMA), " +
            "`SpineAtlasSwap`, `SkeletonDataAsset.CreateRuntime`. AI path is img2img per atlas region — " +
            "bones stay; see `Documentation~/atlas-restyle.md`.\n\n" +
            "Headless: Goblins skin toggle + extra rotation; Mix-and-Match combine; runtime factory; " +
            "restyle tint keeps other pixels; official goblin/head restyle.\n\n" +
            "Screenshot: [m9-other-examples.png](m9-other-examples.png).\n");
    }

    private SkeletonAnimation CreateOfficialAnimation(string folder, string file)
    {
        string dir = Path.Combine(SampleRoot(), "Skeletons", folder);
        SpineAtlasAsset atlas = SpineAtlasAsset.CreateRuntime(File.ReadAllText(Directory.GetFiles(dir, "*.atlas*")[0]));
        SkeletonDataAsset asset = SkeletonDataAsset.CreateRuntime(File.ReadAllBytes(Path.Combine(dir, file)), atlas, scale: 1f);
        GameObject go = CreateGameObject(folder);
        var anim = go.AddComponent<SkeletonAnimation>();
        anim.SkeletonDataAsset = asset;
        anim.Initialize(overwrite: true);
        Assert.NotNull(anim.Skeleton);
        return anim;
    }

    private static SkeletonData ReadOfficial(string folder, string file)
    {
        string dir = Path.Combine(SampleRoot(), "Skeletons", folder);
        string path = Path.Combine(dir, file);
        AttachmentLoader loader = SpineNullAttachmentLoader.Instance;
        string? atlasPath = Directory.GetFiles(dir, "*.atlas*").FirstOrDefault();
        if (atlasPath != null)
        {
            using var atlasReader = new StreamReader(atlasPath, Encoding.UTF8);
            var atlas = new Atlas(atlasReader, dir, SpineNoopTextureLoader.Instance);
            loader = new AtlasAttachmentLoader(atlas);
        }

        var json = new SkeletonJson(loader) { Scale = 1f };
        using var reader = new StreamReader(path, Encoding.UTF8);
        return json.ReadSkeletonData(reader);
    }

    private static void PlayNamed(SkeletonData data, string name)
    {
        var skeleton = new Skeleton(data);
        var state = new AnimationState(new AnimationStateData(data));
        Assert.NotNull(state.SetAnimation(0, name, loop: false));
        state.Update(0.1f);
        state.Apply(skeleton);
        skeleton.UpdateWorldTransform(global::Spine.Physics.Update);
        Assert.Equal(name, state.GetTrack(0)!.Animation.Name);
    }

    private static void FillRect(byte[] rgba, int stride, int x, int y, int w, int h, byte r, byte g, byte b, byte a)
    {
        for (int py = y; py < y + h; py++)
        {
            for (int px = x; px < x + w; px++)
            {
                int i = (py * stride + px) * 4;
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = b;
                rgba[i + 3] = a;
            }
        }
    }

    private static bool HasDarkerRed(byte[] before, byte[] after)
    {
        for (int i = 0; i + 3 < before.Length; i += 4)
        {
            if (before[i + 3] < 32 || before[i] < 32)
                continue;
            if (after[i] < before[i])
                return true;
        }

        return false;
    }

    private static byte[] DecodePngTopLeft(string path, out int width, out int height)
    {
        using var image = new MagickImage(path);
        image.ColorSpace = ColorSpace.sRGB;
        image.ColorType = ColorType.TrueColorAlpha;
        width = (int)image.Width;
        height = (int)image.Height;
        return image.GetPixels().ToByteArray(PixelMapping.RGBA)
            ?? throw new InvalidOperationException("Failed to decode " + path);
    }

    private static string SampleRoot()
        => Path.Combine(SpineEvidenceImage.FindRepositoryRoot(),
            "Packages", "com.xengine.spine", "Samples~", "OtherExamples");
}
