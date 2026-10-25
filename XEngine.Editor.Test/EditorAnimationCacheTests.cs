using XEngine.Runtime;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorAnimationCacheTests : EditorTestHarness
{
    [Fact]
    public void BundleSwitch_ReleasesUnpinnedClipsWithoutInvalidatingExternalReferences()
    {
        var clip = new AnimationClip();
        Assets.CreateAsset(clip, "Memory/Unpinned.anim");
        var pinned = new AnimationClip();
        Assets.CreateAsset(pinned, "Memory/Pinned.anim");
        clip = (AnimationClip)Assets.Get(clip.AssetID)!;
        pinned = (AnimationClip)Assets.Get(pinned.AssetID)!;
        var owner = new Scene();
        AssetDatabase.LockToScene(pinned.AssetID, owner);
        Assert.Equal(1, Assets.ReleaseUnpinnedSceneCache());
        Assert.Null(Assets.GetLoadedAsset(clip.AssetID));
        Assert.False(clip.IsDisposed);
        Assert.Same(pinned, Assets.GetLoadedAsset(pinned.AssetID));
        owner.Dispose();
        Assert.Equal(1, Assets.ReleaseUnpinnedSceneCache());
        Assert.Equal(0, Assets.ReleaseUnpinnedSceneCache());
        Assert.NotNull(Assets.Get(clip.AssetID));
    }

    [Fact]
    public void BundleSwitch_DropsMeshOwnershipAndPreservesOtherScenePins()
    {
        using var mesh = new Mesh();
        using var pinned = new Mesh();
        mesh.AssetID = Guid.NewGuid(); pinned.AssetID = Guid.NewGuid();
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<Guid, EngineObject>)
            typeof(AssetBackendBase).GetField("_loadedAssets", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!.GetValue(Assets)!;
        cache[mesh.AssetID] = mesh; cache[pinned.AssetID] = pinned;
        using var owner = new Scene();
        AssetDatabase.LockToScene(pinned.AssetID, owner);
        Assert.Equal(1, Assets.ReleaseUnpinnedSceneCache());
        Assert.Null(Assets.GetLoadedAsset(mesh.AssetID));
        Assert.False(mesh.IsDisposed);
        Assert.Same(pinned, Assets.GetLoadedAsset(pinned.AssetID));
    }
}
