using XEngine.Animation;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Editor.Test;

[ImporterFor(".streamtest")]
public sealed class StreamingTestImporter : AssetImporter
{
    public override int Version => 1;
    public override bool Import(ImportContext ctx)
    {
        ctx.SetMainAsset(new Scene());
        ctx.AddSubAsset("Idle", new AnimationClip { Name = "Idle" });
        ctx.AddSubAsset("Attack", new AnimationClip { Name = "Attack" });
        return true;
    }
}

public sealed class SubAssetStreamingTests : EditorTestHarness
{
    private (Guid parent, Guid idle, Guid attack) Prepare()
    {
        File.WriteAllText(AssetAbsolutePath("Hero.streamtest"), "test");
        Guid parent = Assets.ImportFile("Hero.streamtest");
        Assert.NotEqual(Guid.Empty, parent);
        var subs = Assets.GetSubAssets(parent);
        Assert.Equal(2, subs.Length);
        Guid idle = subs[0].Guid, attack = subs[1].Guid;
        AssetDatabase.ForceIdle(parent);
        Assets.ForceIdleSweep();
        Assert.Null(Assets.GetLoadedAsset(parent));
        Assert.Null(Assets.GetLoadedAsset(idle));
        Assert.Null(Assets.GetLoadedAsset(attack));
        return (parent, idle, attack);
    }

    [Fact]
    public void LoadingOneClip_DoesNotLoadParentOrSibling()
    {
        var (parent, idle, attack) = Prepare();
        var clip = Assert.IsType<AnimationClip>(Assets.Get(idle));
        Assert.Equal(idle, clip.AssetID);
        Assert.Equal("Hero.streamtest#Idle", clip.AssetPath);
        Assert.Null(Assets.GetLoadedAsset(parent));
        Assert.Null(Assets.GetLoadedAsset(attack));
        Assert.Same(clip, Assets.Get(idle));
        Assert.Equal(2, Assets.GetReloadCount(idle)); // import + direct cache load
        Assert.Equal(1, Assets.GetReloadCount(parent));
        Assert.Equal(1, Assets.GetReloadCount(attack));
    }

    [Fact]
    public void LoadingParent_DoesNotEagerlyLoadClips()
    {
        var (parent, idle, attack) = Prepare();
        Assert.IsType<Scene>(Assets.Get(parent));
        Assert.Null(Assets.GetLoadedAsset(idle));
        Assert.Null(Assets.GetLoadedAsset(attack));
        Assert.IsType<AnimationClip>(Assets.Get(attack));
        Assert.Null(Assets.GetLoadedAsset(idle));
    }

    [Fact]
    public void IndependentlyLoadedClip_StillHonorsFamilySceneLock()
    {
        var (parent, idle, _) = Prepare();
        using var scene = new Scene();
        var clip = Assert.IsType<AnimationClip>(Assets.Get(idle));
        AssetDatabase.LockToScene(idle, scene);
        AssetDatabase.ForceIdle(parent);
        Assets.ForceIdleSweep();
        Assert.False(clip.IsDisposed);
        scene.Dispose();
        AssetDatabase.ForceIdle(parent);
        Assets.ForceIdleSweep();
        Assert.True(clip.IsDisposed);
    }
}
