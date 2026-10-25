using System.Reflection;
using XEngine.Runtime;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class AssetReimportLiveInstanceTests : EditorTestHarness
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private AssetEntry Prepare(string path = "Live.outcomeprobe", string source = "1")
    {
        File.WriteAllText(AssetAbsolutePath(path), source);
        return Assets.GetEntry(Assets.ImportFile(path))!;
    }

    [Theory]
    [InlineData("explicit", "false")]
    [InlineData("explicit", "throw")]
    [InlineData("explicit", "reported")]
    [InlineData("import-file", "false")]
    [InlineData("watcher", "reported")]
    [InlineData("background", "throw")]
    public void RejectedImportKeepsLastGoodMainChildAndAssetRefs(string route, string failure)
    {
        var entry = Prepare();
        Guid childId = Assert.Single(entry.SubAssets).Guid;
        var oldMain = Assets.Get(entry.Guid)!;
        var oldChild = Assets.Get(childId)!;
        var mainRef = new AssetRef<ReimportOutcomeProbeAsset>((ReimportOutcomeProbeAsset)oldMain);
        var childRef = new AssetRef<ReimportOutcomeProbeAsset>((ReimportOutcomeProbeAsset)oldChild);
        int mainLoads = Assets.GetReloadCount(entry.Guid), childLoads = Assets.GetReloadCount(childId);
        var disk = new[] { entry.Guid, childId }.ToDictionary(id => id,
            id => File.ReadAllBytes(Path.Combine(Project.CachePath, id + ".asset")));
        var notifications = new List<string>();
        Assets.OnAssetsImported += paths => notifications.AddRange(paths);
        File.WriteAllText(AssetAbsolutePath(entry.Path), failure);

        Run(route, entry);

        Assert.False(oldMain.IsDisposed); Assert.False(oldChild.IsDisposed);
        Assert.Same(oldMain, Assets.GetLoadedAsset(entry.Guid));
        Assert.Same(oldChild, Assets.GetLoadedAsset(childId));
        Assert.Same(oldMain, mainRef.Res); Assert.Same(oldChild, childRef.Res);
        Assert.Equal(mainLoads, Assets.GetReloadCount(entry.Guid));
        Assert.Equal(childLoads, Assets.GetReloadCount(childId));
        Assert.Equal(childId, Assert.Single(entry.SubAssets).Guid);
        Assert.True(entry.NeedsReimport); Assert.Empty(notifications);
        foreach (var pair in disk)
            Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(Project.CachePath, pair.Key + ".asset")));

        File.WriteAllText(AssetAbsolutePath(entry.Path), "2");
        Run(route, entry);
        Assert.True(oldMain.IsDisposed); Assert.True(oldChild.IsDisposed);
        // Bulk apply deliberately idle-sweeps new results. Populate the cache before asserting
        // AssetRef.Res, whose asynchronous contract allows a transient null after eviction.
        Assert.NotNull(Assets.Get(entry.Guid)); Assert.NotNull(Assets.Get(childId));
        Assert.Equal(2, mainRef.Res!.Revision); Assert.Equal(2, childRef.Res!.Revision);
        Assert.NotSame(oldMain, mainRef.Res); Assert.NotSame(oldChild, childRef.Res);
        Assert.Equal(childId, Assert.Single(entry.SubAssets).Guid);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DependentRejectionPreservesItsLiveFamilyUntilItsOwnRepair(bool background)
    {
        var source = Prepare();
        var dependent = Prepare("Dependent.outcomeprobe", "1|" + source.Guid);
        var main = Assets.Get(dependent.Guid)!;
        var child = Assets.Get(Assert.Single(dependent.SubAssets).Guid)!;
        File.WriteAllText(AssetAbsolutePath(dependent.Path), "false");
        if (background)
        {
            typeof(EditorAssetBackend).GetMethod("ReenqueueDirectDependents", Private)!
                .Invoke(Assets, [source.Guid]);
            Assets.FlushBackgroundImports(10_000);
            Assert.False(Assets.HasPendingBackgroundImports);
        }
        else
        {
            var result = Assets.ReimportWithResult(source.Guid);
            Assert.True(result.TargetSucceeded); Assert.False(result.Succeeded);
            Assert.Equal(dependent.Guid, Assert.Single(result.DependentFailures).Guid);
        }
        Assert.False(main.IsDisposed); Assert.False(child.IsDisposed);
        Assert.Same(main, Assets.GetLoadedAsset(dependent.Guid));
        File.WriteAllText(AssetAbsolutePath(dependent.Path), "2|" + source.Guid);
        Assert.True(Assets.ReimportWithResult(dependent.Guid).Succeeded);
        Assert.True(main.IsDisposed); Assert.True(child.IsDisposed);
    }

    [Fact]
    public void ImportCoreDoesNotRetireOldFamilyUntilMainThreadPublication()
    {
        var entry = Prepare(); var main = Assets.Get(entry.Guid)!;
        var child = Assets.Get(Assert.Single(entry.SubAssets).Guid)!;
        File.WriteAllText(AssetAbsolutePath(entry.Path), "2");
        var core = typeof(EditorAssetBackend).GetMethod("ImportCore", Private)!
            .Invoke(Assets, [entry, AssetAbsolutePath(entry.Path)]);
        Assert.False(main.IsDisposed); Assert.False(child.IsDisposed);
        Assert.Same(main, Assets.GetLoadedAsset(entry.Guid));
        Assert.True((bool)typeof(EditorAssetBackend).GetMethod("ApplyImportResult", Private)!
            .Invoke(Assets, [core])!);
        Assert.True(main.IsDisposed); Assert.True(child.IsDisposed);
        Assert.Equal(2, Assert.IsType<ReimportOutcomeProbeAsset>(Assets.Get(entry.Guid)).Revision);
    }

    [Fact]
    public void SaveAssetParserFailureKeepsEditedInstanceAndLastGoodCacheUntilRepair()
    {
        var entry = Prepare();
        var main = Assert.IsType<ReimportOutcomeProbeAsset>(Assets.Get(entry.Guid));
        var child = Assets.Get(Assert.Single(entry.SubAssets).Guid)!;
        string cachePath = Path.Combine(Project.CachePath, entry.Guid + ".asset");
        byte[] cache = File.ReadAllBytes(cachePath);
        main.Revision = 2;
        Assets.SaveAsset(main); // Echo source is invalid for the deliberately numeric probe parser.
        Assert.False(main.IsDisposed); Assert.False(child.IsDisposed);
        Assert.Same(main, Assets.GetLoadedAsset(entry.Guid));
        Assert.Equal(2, main.Revision); Assert.Equal(cache, File.ReadAllBytes(cachePath));
        Assert.True(entry.NeedsReimport);
        File.WriteAllText(AssetAbsolutePath(entry.Path), "3");
        Assert.True(Assets.ReimportWithResult(entry.Guid).Succeeded);
        Assert.True(main.IsDisposed); Assert.True(child.IsDisposed);
        Assert.Equal(3, Assert.IsType<ReimportOutcomeProbeAsset>(Assets.Get(entry.Guid)).Revision);
    }

    [Fact]
    public void SourceOnlyOutcomeDoesNotRetirePreviouslyUsableRuntimeOutput()
    {
        var entry = Prepare(); var main = Assets.Get(entry.Guid)!;
        var childId = Assert.Single(entry.SubAssets).Guid; var child = Assets.Get(childId)!;
        File.WriteAllText(AssetAbsolutePath(entry.Path), "source-only");
        Assert.Equal(AssetReimportStatus.SourceOnly, Assets.ReimportWithResult(entry.Guid).Status);
        Assert.False(main.IsDisposed); Assert.False(child.IsDisposed);
        Assert.Equal(childId, Assert.Single(entry.SubAssets).Guid);
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
