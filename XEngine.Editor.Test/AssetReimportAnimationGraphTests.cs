using System.Globalization;
using System.Reflection;
using XEngine.Animation;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using Xunit;

namespace XEngine.Editor.Test;

[ImporterFor(".graphprobe")]
public sealed class AnimationGraphProbeImporter : AssetImporter
{
    public override int Version => 1;
    public override bool Import(ImportContext ctx)
    {
        string source = File.ReadAllText(ctx.AbsolutePath);
        if (source == "fail") return ctx.Fail("Intentional graph probe rejection.");
        ctx.SetMainAsset(new ReimportOutcomeProbeAsset());
        if (source == "none") return true;
        float value = float.Parse(source, CultureInfo.InvariantCulture);
        var clip = new AnimationClip { Name = "Probe", Duration = 4,
            RootMotionPolicy = ClipRootMotionPolicy.Disabled };
        clip.AddBone(new AnimationClip.AnimBone { BoneName = value < 3 ? "Bone" : "Other",
            PosX = new AnimationCurve([new KeyFrame(0, value)]),
            PosY = new AnimationCurve([new KeyFrame(0, 0)]),
            PosZ = new AnimationCurve([new KeyFrame(0, 0)]) });
        ctx.AddSubAsset("stable-clip", clip);
        return true;
    }
}

public sealed class AssetReimportAnimationGraphTests : EditorTestHarness
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("explicit")]
    [InlineData("import-file")]
    [InlineData("watcher")]
    [InlineData("background")]
    public void PublicationRefreshesSamePlayableAndMapsWithoutChangingItsPausedClock(string route)
    {
        File.WriteAllText(AssetAbsolutePath("Probe.graphprobe"), "1");
        var entry = Assets.GetEntry(Assets.ImportFile("Probe.graphprobe"))!;
        Guid id = Assert.Single(entry.SubAssets).Guid;
        var old = Assert.IsType<AnimationClip>(Assets.Get(id));
        using var root = new GameObject("Root");
        var bone = new GameObject("Bone"); bone.SetParent(root);
        var other = new GameObject("Other"); other.SetParent(root);
        var animator = new Animator { PlayAutomatically = false }; root.AddComponent(animator);
        var state = animator.Play(old);
        state.SetTime(1.25); state.Speed = -0.5f; state.Wrap = AnimationWrapMode.PingPong;
        state.Pause(); animator.EvaluateGraph(0);
        Assert.Equal(1, bone.Transform.LocalPosition.X);
        File.WriteAllText(AssetAbsolutePath(entry.Path), "3");
        Run(route, entry);

        Assert.True(old.IsDisposed);
        Assert.NotSame(old, state.Clip);
        Assert.False(state.Clip!.IsDisposed);
        Assert.Equal(id, state.Clip!.AssetID);
        Assert.Same(state.Clip, animator.CurrentClip);
        Assert.Equal(1.25, state.Time);
        Assert.False(state.IsPlaying);
        Assert.Equal(-0.5f, state.Speed);
        Assert.Equal(AnimationWrapMode.PingPong, state.Wrap);
        animator.EvaluateGraph(0);
        Assert.Equal(3, other.Transform.LocalPosition.X);
        Assert.Same(state, animator.Play(state.Clip)); // Dictionary key also follows the new instance.
    }

    [Fact]
    public void FailureDoesNotRefreshGraphButRemovingAndRestoringClipDoes()
    {
        File.WriteAllText(AssetAbsolutePath("Probe.graphprobe"), "1");
        var entry = Assets.GetEntry(Assets.ImportFile("Probe.graphprobe"))!;
        Guid id = Assert.Single(entry.SubAssets).Guid;
        var old = Assert.IsType<AnimationClip>(Assets.Get(id));
        using var root = new GameObject("Root");
        new GameObject("Bone").SetParent(root);
        var animator = new Animator { PlayAutomatically = false }; root.AddComponent(animator);
        var state = animator.Play(old); state.SetTime(1.25); state.Pause();
        File.WriteAllText(AssetAbsolutePath(entry.Path), "fail");
        Assert.False(Assets.ReimportWithResult(entry.Guid).Succeeded);
        Assert.Same(old, state.Clip); Assert.False(old.IsDisposed);
        File.WriteAllText(AssetAbsolutePath(entry.Path), "none");
        Assert.True(Assets.ReimportWithResult(entry.Guid).Succeeded);
        Assert.Null(state.Clip); Assert.Null(animator.CurrentClip);
        animator.EvaluateGraph(0); // No stale curve/event indexing after deletion.
        Assert.Equal(1.25, state.Time);
        File.WriteAllText(AssetAbsolutePath(entry.Path), "2");
        Assert.True(Assets.ReimportWithResult(entry.Guid).Succeeded);
        Assert.Equal(id, state.Clip!.AssetID);
        Assert.Equal(1.25, state.Time); Assert.False(state.IsPlaying);
        Assert.Same(state.Clip, animator.CurrentClip);
    }

    [Fact]
    public void ExplicitDeleteClearsActiveClipWithoutDestroyingOwner()
    {
        File.WriteAllText(AssetAbsolutePath("Probe.graphprobe"), "1");
        var entry = Assets.GetEntry(Assets.ImportFile("Probe.graphprobe"))!;
        var clip = Assert.IsType<AnimationClip>(Assets.Get(Assert.Single(entry.SubAssets).Guid));
        using var root = new GameObject("Root");
        new GameObject("Bone").SetParent(root);
        var animator = new Animator { PlayAutomatically = false }; root.AddComponent(animator);
        var state = animator.Play(clip);
        Assets.DeleteAsset(entry.Path);
        Assert.Null(state.Clip); Assert.Null(animator.CurrentClip);
        animator.EvaluateGraph(0.1f);
        Assert.False(animator.IsDisposed);
    }

    private void Run(string route, AssetEntry entry)
    {
        if (route == "explicit") Assets.ReimportWithResult(entry.Guid);
        else if (route == "import-file") Assets.ImportFile(entry.Path);
        else if (route == "watcher")
            typeof(EditorAssetBackend).GetMethod("ImportFileChange", Private)!
                .Invoke(Assets, [AssetAbsolutePath(entry.Path), entry.Path, new List<string>()]);
        else
        {
            entry.NeedsReimport = true;
            typeof(EditorAssetBackend).GetMethod("EnqueueBackgroundImport", Private)!.Invoke(Assets, [entry]);
            Assets.FlushBackgroundImports(10_000);
            Assert.False(Assets.HasPendingBackgroundImports);
        }
    }
}
