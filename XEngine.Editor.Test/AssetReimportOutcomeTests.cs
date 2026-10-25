using System.Reflection;
using XEngine.Echo;
using XEngine.Editor.Importers;
using XEngine.Editor.Inspector;
using XEngine.Runtime;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class ReimportOutcomeProbeAsset : ScriptableObject
{
    [SerializeField] public int Revision;
}

[ImporterFor(".outcomeprobe")]
public sealed class ReimportOutcomeProbeImporter : AssetImporter
{
    public static int Imports;
    public override int Version => 1;
    public override bool Import(ImportContext ctx)
    {
        Imports++;
        string[] source = File.ReadAllText(ctx.AbsolutePath).Split('|');
        if (source[0] == "throw") throw new InvalidOperationException("Probe import exception.");
        if (source[0] == "false") return false;
        if (source[0] == "reported") return ctx.Fail("Probe parser rejected this source.");
        if (source[0] == "source-only") return true;
        ctx.SetMainAsset(new ReimportOutcomeProbeAsset { Revision = int.Parse(source[0]) });
        ctx.AddSubAsset("stable-child", new ReimportOutcomeProbeAsset { Revision = int.Parse(source[0]) });
        if (source.Length > 1) ctx.AddDependency(Guid.Parse(source[1]));
        return true;
    }
}

public sealed class AssetReimportOutcomeTests : EditorTestHarness
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(ModelAssetEditor editor, string field, object value)
        => typeof(ModelAssetEditor).GetField(field, Private)!.SetValue(editor, value);
    private static T Get<T>(ModelAssetEditor editor, string field)
        => (T)typeof(ModelAssetEditor).GetField(field, Private)!.GetValue(editor)!;
    private static object? Invoke(ModelAssetEditor editor, string method, params object[] args)
        => typeof(ModelAssetEditor).GetMethod(method, Private)!.Invoke(editor, args);

    private AssetEntry Prepare(string path = "Model.outcomeprobe", string source = "1")
    {
        File.WriteAllText(AssetAbsolutePath(path), source);
        Guid guid = Assets.ImportFile(path);
        return Assets.GetEntry(guid)!;
    }

    private ModelAssetEditor Load(AssetEntry entry)
    {
        var editor = new ModelAssetEditor();
        Set(editor, "_currentGuid", entry.Guid);
        Invoke(editor, "LoadSettingsFromMeta", entry);
        Set(editor, "_settingsLoaded", true);
        return editor;
    }

    [Theory]
    [InlineData("false")]
    [InlineData("throw")]
    [InlineData("reported")]
    public void InspectorKeepsFailureAfterSettingsAreSavedAndRetryDoesNotRewriteMeta(string failure)
    {
        var entry = Prepare();
        var editor = Load(entry);
        var session = Get<object>(editor, "_settingsSession");
        var childIds = entry.SubAssets.Select(x => x.Guid).ToArray();
        File.WriteAllText(AssetAbsolutePath(entry.Path), failure);
        Set(editor, "_unitScale", 2f);
        Assert.True((bool)Invoke(editor, "SaveSettingsToMeta", entry)!);
        Set(editor, "_settingsDirty", false); // the Apply callback has saved its draft.
        var metaPath = AssetAbsolutePath(entry.Path) + ".meta";
        byte[] saved = File.ReadAllBytes(metaPath);
        DateTime savedAt = File.GetLastWriteTimeUtc(metaPath);
        var notifications = new List<string>();
        Assets.OnAssetsImported += paths => notifications.AddRange(paths);

        Invoke(editor, "Reimport", entry, session);
        Assert.Contains("reimport", Get<string>(editor, "_settingsError"), StringComparison.OrdinalIgnoreCase);
        Assert.True(Get<bool>(editor, "_settingsLoaded")); // the failed request keeps its session.
        Assert.False(Get<bool>(editor, "_settingsDirty"));
        Assert.Empty(notifications);
        Assert.Equal(childIds, entry.SubAssets.Select(x => x.Guid).ToArray());

        File.WriteAllText(AssetAbsolutePath(entry.Path), "2");
        Invoke(editor, "Reimport", entry, session);
        Assert.Null(Get<string?>(editor, "_settingsError"));
        Assert.Equal([entry.Path], notifications);
        Assert.Equal(2, Assert.IsType<ReimportOutcomeProbeAsset>(Assets.Get(entry.Guid)).Revision);
        Assert.Equal(saved, File.ReadAllBytes(metaPath));
        Assert.Equal(savedAt, File.GetLastWriteTimeUtc(metaPath));
        Assert.Equal(childIds, entry.SubAssets.Select(x => x.Guid).ToArray());
    }

    [Fact]
    public void MissingTargetAndSourceAreExplicitAndDoNotDisposeLastGoodInstance()
    {
        var missing = Assets.ReimportWithResult(Guid.NewGuid());
        Assert.Equal(AssetReimportStatus.NotFound, missing.Status);
        Assert.False(missing.Succeeded);
        Assert.NotEmpty(missing.Error!);
        var entry = Prepare();
        var old = Assets.GetLoadedAsset(entry.Guid)!;
        File.Delete(AssetAbsolutePath(entry.Path));
        var result = Assets.ReimportWithResult(entry.Guid);
        Assert.Equal(AssetReimportStatus.MissingSource, result.Status);
        Assert.False(result.TargetSucceeded);
        Assert.Equal(entry.Path, result.Path);
        Assert.Contains(entry.Path, result.Error!);
        Assert.False(old.IsDisposed);
        File.WriteAllText(AssetAbsolutePath(entry.Path), "2");
        Assert.True(Assets.ReimportWithResult(entry.Guid).Succeeded);
        Assert.True(old.IsDisposed);
    }

    [Theory]
    [InlineData("false", "reported an import failure")]
    [InlineData("throw", "Probe import exception")]
    [InlineData("reported", "Probe parser rejected")]
    public void FailureHasActualReasonPreservesIdentityAndCannotReloadOldCacheAsSuccess(string mode, string reason)
    {
        var entry = Prepare();
        var child = Assert.Single(entry.SubAssets).Guid;
        var old = Assets.GetLoadedAsset(entry.Guid)!;
        var notifications = new List<string>();
        Assets.OnAssetsImported += paths => notifications.AddRange(paths);
        File.WriteAllText(AssetAbsolutePath(entry.Path), mode);
        int before = ReimportOutcomeProbeImporter.Imports;
        var result = Assets.ReimportWithResult(entry.Guid);
        Assert.Equal(AssetReimportStatus.Failed, result.Status);
        Assert.False(result.Succeeded);
        Assert.Contains(reason, result.Error!);
        Assert.Equal(before + 1, ReimportOutcomeProbeImporter.Imports);
        Assert.Same(old, Assets.GetLoadedAsset(entry.Guid));
        Assert.False(old.IsDisposed); // a2i2a: failure retains last-good output without reporting success.
        Assert.Empty(notifications);
        Assert.Equal(child, Assert.Single(entry.SubAssets).Guid);
        Assert.True(entry.NeedsReimport);
    }

    [Fact]
    public void SourceOnlyIncludeStillInvalidatesItsDirectDependentWithoutFalseRuntimeNotification()
    {
        var include = Prepare("Test.unknowninclude", "source text");
        Assert.Equal(nameof(DefaultImporter), include.ImporterType);
        var dependent = Prepare("User.outcomeprobe", "1|" + include.Guid);
        var oldDependent = Assets.GetLoadedAsset(dependent.Guid)!;
        var notifications = new List<string>();
        Assets.OnAssetsImported += paths => notifications.AddRange(paths);
        File.WriteAllText(AssetAbsolutePath(include.Path), "changed include");
        File.WriteAllText(AssetAbsolutePath(dependent.Path), "2|" + include.Guid);
        var result = Assets.ReimportWithResult(include.Guid);
        Assert.Equal(AssetReimportStatus.SourceOnly, result.Status);
        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.Empty(result.DependentFailures);
        Assert.True(oldDependent.IsDisposed);
        Assert.Equal(2, Assert.IsType<ReimportOutcomeProbeAsset>(Assets.Get(dependent.Guid)).Revision);
        Assert.DoesNotContain(include.Path, notifications);
    }

    [Fact]
    public void SelectedSuccessAndDependentFailureAreSeparateAndInspectorRetainsRetryState()
    {
        var entry = Prepare();
        var dependent = Prepare("User.outcomeprobe", "1|" + entry.Guid);
        var editor = Load(entry);
        var notifications = new List<string>();
        Assets.OnAssetsImported += paths => notifications.AddRange(paths);
        File.WriteAllText(AssetAbsolutePath(entry.Path), "2");
        File.WriteAllText(AssetAbsolutePath(dependent.Path), "false");
        var result = Assets.ReimportWithResult(entry.Guid);
        Assert.Equal(AssetReimportStatus.Imported, result.Status);
        Assert.True(result.TargetSucceeded);
        Assert.False(result.Succeeded);
        Assert.Null(result.Error);
        var failed = Assert.Single(result.DependentFailures);
        Assert.Equal(dependent.Guid, failed.Guid);
        Assert.Equal(dependent.Path, failed.Path);
        Assert.Equal([entry.Path], notifications);
        Assert.Equal(2, Assert.IsType<ReimportOutcomeProbeAsset>(Assets.Get(entry.Guid)).Revision);
        Invoke(editor, "Reimport", entry, Get<object>(editor, "_settingsSession"));
        Assert.Contains(dependent.Path, Get<string>(editor, "_settingsError"));
        Assert.True(Get<bool>(editor, "_settingsLoaded"));
        File.WriteAllText(AssetAbsolutePath(dependent.Path), "3|" + entry.Guid);
        Invoke(editor, "Reimport", entry, Get<object>(editor, "_settingsSession"));
        Assert.Null(Get<string?>(editor, "_settingsError"));
        Assert.False(Get<bool>(editor, "_settingsLoaded"));
        Assert.Equal(3, Assert.IsType<ReimportOutcomeProbeAsset>(Assets.Get(dependent.Guid)).Revision);
    }

    [Fact]
    public void StaleSettingsSessionCannotReimportReplacementSelection()
    {
        var entry = Prepare();
        var editor = Load(entry);
        var stale = Get<object>(editor, "_settingsSession");
        Invoke(editor, "LoadSettingsFromMeta", entry);
        int before = ReimportOutcomeProbeImporter.Imports;
        Invoke(editor, "Reimport", entry, stale);
        Assert.Equal(before, ReimportOutcomeProbeImporter.Imports);
        Assert.Null(Get<string?>(editor, "_settingsError"));
    }

    [Fact]
    public void LegacyVoidApiStillImportsAndDoesNotSendFailureAsSuccess()
    {
        var entry = Prepare();
        var notifications = new List<string>();
        Assets.OnAssetsImported += paths => notifications.AddRange(paths);
        File.WriteAllText(AssetAbsolutePath(entry.Path), "false");
        Assets.Reimport(entry.Guid);
        Assert.Empty(notifications);
        File.WriteAllText(AssetAbsolutePath(entry.Path), "3");
        Assets.Reimport(entry.Guid);
        Assert.Equal([entry.Path], notifications);
        Assert.Equal(3, Assert.IsType<ReimportOutcomeProbeAsset>(Assets.Get(entry.Guid)).Revision);
    }

    [Fact]
    public void SourceOnlyOutcomeIsNotAValidModelPreviewImport()
    {
        var entry = Prepare();
        var editor = Load(entry);
        File.WriteAllText(AssetAbsolutePath(entry.Path), "source-only");
        Invoke(editor, "Reimport", entry, Get<object>(editor, "_settingsSession"));
        Assert.Contains("no runtime model", Get<string>(editor, "_settingsError"));
    }

    [Fact]
    public void WatcherPayloadIncludesOnlySuccessfulSelectedAndDependentImports()
    {
        var entry = Prepare();
        var dependent = Prepare("User.outcomeprobe", "1|" + entry.Guid);
        var applied = new List<string>();
        var changed = typeof(EditorAssetBackend).GetMethod("ImportFileChange", Private)!;
        File.WriteAllText(AssetAbsolutePath(entry.Path), "false");
        File.WriteAllText(AssetAbsolutePath(dependent.Path), "false");
        changed.Invoke(Assets, [AssetAbsolutePath(entry.Path), entry.Path, applied]);
        Assert.Empty(applied);
        File.WriteAllText(AssetAbsolutePath(entry.Path), "2");
        changed.Invoke(Assets, [AssetAbsolutePath(entry.Path), entry.Path, applied]);
        Assert.Equal([entry.Path], applied);
    }
}
