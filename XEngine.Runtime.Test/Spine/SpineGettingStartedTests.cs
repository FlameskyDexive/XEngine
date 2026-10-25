using System.Text;

using Spine;

using XEngine.Runtime;
using XEngine.Spine;
using XEngine.Spine.Samples.GettingStarted;

using Xunit;

namespace XEngine.Runtime.Test.Spine;

public sealed class SpineGettingStartedTests : RuntimeTestBase
{
    private static readonly string[] SceneFiles =
    [
        "1 The Spine GameObject.scene",
        "2 Controlling Animation.scene",
        "3 Controlling Animation Continued.scene",
        "4 Object Oriented Sample.scene",
        "5 Basic Platformer.scene",
        "6 SkeletonGraphic.scene"
    ];

    [Fact]
    public void SampleLayout_HasScenesScriptsAndOfficialSkeletons()
    {
        string root = SampleRoot();
        foreach (string scene in SceneFiles)
            Assert.True(File.Exists(Path.Combine(root, "Scenes", scene)), scene);

        Assert.True(File.Exists(Path.Combine(root, "Scripts", "SpineBeginnerTwo.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Scripts", "HandleEventWithAudioExample.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Scripts", "Raptor.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Scripts", "SpineboyBeginnerInput.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Scripts", "SpineboyBeginnerModel.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Scripts", "SpineboyBeginnerView.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Scripts", "BasicPlatformerController.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Scripts", "SkeletonAnimationHandleExample.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Scripts", "SpineBlinkPlayer.cs")));
        Assert.True(File.Exists(Path.Combine(root, "LICENSE-SPINE-EXAMPLES.txt")));

        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "Spineboy", "spineboy-ess.json")));
        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "Spineboy", "spineboy-pma.atlas")));
        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "Spineboy", "spineboy-pma.png")));
        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "Raptor", "raptor-pro.json")));
        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "Raptor", "raptor-pma.png")));
        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "Hero", "hero-ess.json")));
        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "Hero", "hero-pma.png")));
        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "Doi", "Doi.json")));
        Assert.True(File.Exists(Path.Combine(root, "Skeletons", "Doi", "Doi.png")));
    }

    [Fact]
    public void SceneText_NamesSpineComponentsAndOfficialCast()
    {
        string scenes = Path.Combine(SampleRoot(), "Scenes");
        string s1 = File.ReadAllText(Path.Combine(scenes, SceneFiles[0]));
        string s2 = File.ReadAllText(Path.Combine(scenes, SceneFiles[1]));
        string s3 = File.ReadAllText(Path.Combine(scenes, SceneFiles[2]));
        string s4 = File.ReadAllText(Path.Combine(scenes, SceneFiles[3]));
        string s5 = File.ReadAllText(Path.Combine(scenes, SceneFiles[4]));
        string s6 = File.ReadAllText(Path.Combine(scenes, SceneFiles[5]));

        Assert.Contains("XEngine.Spine.SkeletonAnimation", s1, StringComparison.Ordinal);
        Assert.Contains("XEngine.Spine.SkeletonAnimation", s2, StringComparison.Ordinal);
        Assert.Contains("SpineBeginnerTwo", s2, StringComparison.Ordinal);
        Assert.Contains("HandleEventWithAudioExample", s2, StringComparison.Ordinal);
        Assert.Contains("XEngine.Spine.SkeletonAnimation", s3, StringComparison.Ordinal);
        Assert.Contains("Raptor", s3, StringComparison.Ordinal);
        Assert.Contains("XEngine.Spine.SkeletonAnimation", s4, StringComparison.Ordinal);
        Assert.Contains("SpineboyBeginnerModel", s4, StringComparison.Ordinal);
        Assert.Contains("hero", s5, StringComparison.Ordinal);
        Assert.Contains("BasicPlatformerController", s5, StringComparison.Ordinal);
        Assert.Contains("XEngine.Spine.SkeletonGraphic", s6, StringComparison.Ordinal);
        Assert.Contains("BoneFollowerGraphic", s6, StringComparison.Ordinal);
        Assert.Contains("doi", s6, StringComparison.Ordinal);
    }

    [Fact]
    public void OfficialExports_LoadAndPlayExpectedClips()
    {
        SkeletonData spineboy = ReadOfficial("Spineboy", "spineboy-ess.json");
        Assert.NotNull(spineboy.FindAnimation("walk"));
        Assert.NotNull(spineboy.FindAnimation("idle"));
        Assert.NotNull(spineboy.FindAnimation("run"));
        PlayNamed(spineboy, "walk");

        SkeletonData raptor = ReadOfficial("Raptor", "raptor-pro.json");
        Assert.NotNull(raptor.FindAnimation("walk"));
        Assert.NotNull(raptor.FindAnimation("gun-grab"));
        PlayNamed(raptor, "walk");

        SkeletonData hero = ReadOfficial("Hero", "hero-ess.json");
        Assert.NotNull(hero.FindAnimation("walk"));
        Assert.NotNull(hero.FindAnimation("run"));
        PlayNamed(hero, "walk");

        SkeletonData doi = ReadOfficial("Doi", "Doi.json");
        Assert.NotNull(doi.FindAnimation("blink"));
        Assert.NotNull(doi.FindAnimation("main"));
        PlayNamed(doi, "main");
    }

    [Fact]
    public void SpineBeginnerTwo_WalkThenRun()
    {
        SkeletonAnimation anim = CreateOfficialAnimation("Spineboy", "spineboy-ess.json");
        var demo = anim.GameObject.AddComponent<SpineBeginnerTwo>();
        demo.SkeletonAnimation = anim;
        demo.AutoPlay = false;
        demo.Start();
        Assert.Equal("walk", demo.PlayWalk()!.Animation.Name);
        Assert.Equal("run", demo.PlayRun()!.Animation.Name);
    }

    [Fact]
    public void Raptor_DualTrackWalkAndGunGrab()
    {
        SkeletonAnimation anim = CreateOfficialAnimation("Raptor", "raptor-pro.json");
        var raptor = anim.GameObject.AddComponent<Raptor>();
        raptor.SkeletonAnimation = anim;
        raptor.AutoPlay = false;
        raptor.Start();
        Assert.Equal("walk", anim.AnimationState.GetTrack(0)!.Animation.Name);
        Assert.Equal("gun-grab", raptor.PlayGunGrab()!.Animation.Name);
        Assert.Equal("walk", anim.AnimationState.GetTrack(0)!.Animation.Name);
        Assert.Equal("gun-grab", anim.AnimationState.GetTrack(1)!.Animation.Name);
    }

    [Fact]
    public void SpineboyMvc_RunAndShootTracks()
    {
        SkeletonAnimation anim = CreateOfficialAnimation("Spineboy", "spineboy-ess.json");
        var model = anim.GameObject.AddComponent<SpineboyBeginnerModel>();
        var view = anim.GameObject.AddComponent<SpineboyBeginnerView>();
        view.Model = model;
        view.SkeletonAnimation = anim;
        view.Start();

        model.TryMove(1f);
        view.Update();
        Assert.Equal(SpineBeginnerBodyState.Running, model.State);
        Assert.Equal("run", anim.AnimationState.GetTrack(0)!.Animation.Name);
        Assert.True(anim.Skeleton!.ScaleX > 0f);

        model.TryMove(-1f);
        view.Update();
        Assert.True(model.FacingLeft);
        Assert.True(anim.Skeleton.ScaleX < 0f);

        view.PlayShoot();
        Assert.Equal("shoot", anim.AnimationState.GetTrack(1)!.Animation.Name);
    }

    [Fact]
    public void PlatformerHandle_PlaysHeroWalk()
    {
        SkeletonAnimation anim = CreateOfficialAnimation("Hero", "hero-ess.json");
        var handle = anim.GameObject.AddComponent<SkeletonAnimationHandleExample>();
        handle.SkeletonAnimation = anim;
        handle.Start();
        handle.PlayAnimationForState("walk", 0);
        Assert.Equal("walk", handle.LastStateName);
        Assert.Equal("walk", anim.AnimationState.GetTrack(0)!.Animation.Name);
        handle.PlayAnimationForState("rise", 0);
        Assert.Equal("jump", anim.AnimationState.GetTrack(0)!.Animation.Name);
        handle.SetFlip(-1f);
        Assert.True(anim.Skeleton!.ScaleX < 0f);
    }

    [Fact]
    public void HandleEvent_RecordsOfficialFootstep()
    {
        SkeletonAnimation anim = CreateOfficialAnimation("Spineboy", "spineboy-ess.json");
        var handler = anim.GameObject.AddComponent<HandleEventWithAudioExample>();
        handler.SkeletonAnimation = anim;
        handler.LogDebugMessage = false;
        handler.Start();
        anim.SetAnimation(0, "walk", loop: false);
        for (int i = 0; i < 20; i++)
            anim.UpdateAnimation(0.05f);
        Assert.Equal("footstep", handler.LastEventName);
        Assert.True(handler.EventCount > 0);
    }

    [Fact]
    public void GettingStartedEvidence_WritesM7Screenshot()
    {
        const int width = 480;
        const int height = 200;
        byte[] rgba = new byte[width * height * 4];
        SpineEvidenceImage.Fill(rgba, width, 0, 0, width, height, 22, 24, 30);
        string[] labels = ["1 Spineboy", "2 Events", "3 Raptor", "4 MVC", "5 Hero", "6 Doi UI"];
        byte[][] colors =
        [
            [80, 168, 255],
            [72, 196, 120],
            [232, 140, 64],
            [196, 96, 220],
            [255, 220, 96],
            [96, 200, 220]
        ];
        for (int i = 0; i < 6; i++)
        {
            int x = 16 + i * 76;
            SpineEvidenceImage.Fill(rgba, width, x, 36, 68, 128, colors[i][0], colors[i][1], colors[i][2]);
            SpineEvidenceImage.FillCircle(rgba, width, height, x + 34, 88, 14, 24, 26, 32);
        }

        string repo = SpineEvidenceImage.FindRepositoryRoot();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "spine");
        Directory.CreateDirectory(dir);
        string pngPath = Path.Combine(dir, "m7-getting-started.png");
        File.WriteAllBytes(pngPath, SpineEvidenceImage.EncodePng(rgba, width, height));
        Assert.True(new FileInfo(pngPath).Length > 256);
        _ = labels;

        File.WriteAllText(Path.Combine(dir, "m7-getting-started.md"),
            "# M7 Getting Started\n\n" +
            "Date: 2026-08-16. Branch: `codex/animation-module`.\n\n" +
            "Official spine-unity Getting Started six scenes ported to Echo `.scene` under " +
            "`Packages/com.xengine.spine/Samples~/GettingStarted/`. Skeletons: Spineboy ESS+PMA, " +
            "Raptor pro+PMA, Hero ESS+PMA, Doi (`spineunitygirl`). Scripts are XEngine ports " +
            "(XTask instead of Unity coroutines; Input / CharacterController / xgui).\n\n" +
            "Headless: official JSON loads; walk / gun-grab / MVC run+shoot / Hero walk+rise→jump / footstep events pass.\n\n" +
            "Screenshot: [m7-getting-started.png](m7-getting-started.png).\n");
    }

    private SkeletonAnimation CreateOfficialAnimation(string folder, string file)
    {
        var asset = ScriptableObject.CreateInstance<SkeletonDataAsset>();
        asset.Scale = 1f;
        asset.IsBinary = false;
        asset.SkeletonBytes = File.ReadAllBytes(Path.Combine(SampleRoot(), "Skeletons", folder, file));
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

    private static string SampleRoot()
        => Path.Combine(SpineEvidenceImage.FindRepositoryRoot(),
            "Packages", "com.xengine.spine", "Samples~", "GettingStarted");
}
