using System.Reflection;
using XEngine.Echo;
using XEngine.Editor.Inspector;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.MeshFeatures.Generation;
using Xunit;

namespace XEngine.Editor.Test;

public class ModelImporterSettingsTests : EditorTestHarness
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private AssetEntry CreateModel(string name, EchoObject? settings)
    {
        var entry = new AssetEntry { Guid = Guid.NewGuid(), Path = name, ImporterType = "EditorModelImporter" };
        File.WriteAllText(AssetAbsolutePath(name), "test source, never imported");
        MetaFile.Write(AssetAbsolutePath(name) + ".meta", new MetaFileData
        { Guid = entry.Guid, ImporterType = entry.ImporterType, ImporterVersion = 19, Settings = settings });
        return entry;
    }

    private static void Set(ModelAssetEditor editor, string field, object value)
        => typeof(ModelAssetEditor).GetField(field, Private)!.SetValue(editor, value);
    private static T Get<T>(ModelAssetEditor editor, string field)
        => (T)typeof(ModelAssetEditor).GetField(field, Private)!.GetValue(editor)!;
    private static void Load(ModelAssetEditor editor, AssetEntry entry)
    {
        Set(editor, "_currentGuid", entry.Guid);
        typeof(ModelAssetEditor).GetMethod("LoadSettingsFromMeta", Private)!.Invoke(editor, [entry]);
    }
    private static void Save(ModelAssetEditor editor, AssetEntry entry)
        => typeof(ModelAssetEditor).GetMethod("SaveSettingsToMeta", Private)!.Invoke(editor, [entry]);

    private static bool Map(ModelAssetEditor editor, object session, Guid source, AssetRef<Material> target)
        => (bool)typeof(ModelAssetEditor).GetMethod("SetMaterialRemap", Private)!.Invoke(editor, [session, source, target])!;

    [Fact]
    public void DeletedExternalMaterialIsRejectedEvenWhileCached()
    {
        using var material = new Material { Name = "External" };
        Assets.CreateAsset(material, "External.mat");
        Assert.True(ModelMaterialRemapping.IsExternalMaterial(material.AssetID));
        Assert.NotNull(Assets.GetEntry(material.AssetID));
        Assert.IsType<Material>(AssetDatabase.Get(material.AssetID));
        File.Delete(AssetAbsolutePath("External.mat"));
        Assert.False(ModelMaterialRemapping.IsExternalMaterial(material.AssetID));
    }

    [Fact]
    public void MaterialDraftApplyRevertPreservesUnknownMappingsAndIdentity()
    {
        var source = new Material { AssetID = Guid.NewGuid(), Name = "Body" };
        var external = new Material { Name = "External" };
        try
        {
            Assets.CreateAsset(external, "External.mat");
            Assert.True(ModelMaterialRemapping.IsExternalMaterial(external.AssetID),
                $"Importer={Assets.GetEntry(external.AssetID)?.ImporterType}, resolved={AssetDatabase.Get(external.AssetID)?.GetType().FullName}");
            var settings = EchoObject.NewCompound();
            settings[ModelMaterialRemapping.SettingsKey] = EchoObject.NewCompound();
            settings[ModelMaterialRemapping.SettingsKey]["future"] = new EchoObject("preserved");
            var entry = CreateModel("Model.fbx", settings);
            var editor = new ModelAssetEditor(); Load(editor, entry);
            Get<List<Material>>(editor, "_materials").Add(source);
            var path = AssetAbsolutePath(entry.Path) + ".meta";
            var bytes = File.ReadAllBytes(path);
            Assert.True(Map(editor, Get<object>(editor, "_settingsSession"), source.AssetID, external));
            Assert.True(Get<bool>(editor, "_settingsDirty"));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Load(editor, entry); // Revert never writes.
            Assert.False(Get<bool>(editor, "_settingsDirty"));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.True(Map(editor, Get<object>(editor, "_settingsSession"), source.AssetID, external));
            Save(editor, entry);
            var saved = MetaFile.Read(path);
            Assert.Equal(entry.Guid, saved.Guid);
            Assert.Equal("preserved", saved.Settings![ModelMaterialRemapping.SettingsKey]["future"].StringValue);
            Assert.Equal(external.AssetID.ToString(), saved.Settings[ModelMaterialRemapping.SettingsKey][source.AssetID.ToString()].StringValue);
            Load(editor, entry);
            Assert.True(Map(editor, Get<object>(editor, "_settingsSession"), source.AssetID, new AssetRef<Material>(Guid.Empty)));
            Save(editor, entry);
            Assert.Equal(Guid.Empty.ToString(), MetaFile.Read(path).Settings![ModelMaterialRemapping.SettingsKey][source.AssetID.ToString()].StringValue);
        }
        finally { source.Dispose(); external.Dispose(); }
    }

    [Fact]
    public void MaterialCallbacksRejectStaleSessionUnknownSourceAndInvalidTargetsAtomically()
    {
        using var source = new Material { AssetID = Guid.NewGuid() };
        using var runtime = new Material();
        var entry = CreateModel("Model.fbx", null);
        var editor = new ModelAssetEditor(); Load(editor, entry);
        Get<List<Material>>(editor, "_materials").Add(source);
        var stale = Get<object>(editor, "_settingsSession"); Load(editor, entry);
        var current = Get<object>(editor, "_settingsSession");
        Assert.False(Map(editor, stale, source.AssetID, new AssetRef<Material>(Guid.Empty)));
        Assert.False(Map(editor, current, Guid.NewGuid(), new AssetRef<Material>(Guid.Empty)));
        Assert.False(Map(editor, current, source.AssetID, runtime));
        Assert.False(Map(editor, current, source.AssetID, new AssetRef<Material>(source.AssetID)));
        Assert.False(Map(editor, current, source.AssetID, new AssetRef<Material>(Guid.NewGuid())));
        Assert.Empty(Get<EchoObject>(editor, "_materialRemaps").Tags);
        Assert.False(Get<bool>(editor, "_settingsDirty"));
        Assert.NotEmpty(Get<string>(editor, "_settingsError"));
        Assert.True(Map(editor, current, source.AssetID, new AssetRef<Material>(Guid.Empty)));
        Assert.Null(Get<string?>(editor, "_settingsError"));
        var bytes = File.ReadAllBytes(AssetAbsolutePath(entry.Path) + ".meta"); Save(editor, entry);
        Assert.Equal(bytes, File.ReadAllBytes(AssetAbsolutePath(entry.Path) + ".meta"));
    }

    [Fact]
    public void ApplyPreservesUnknownNestedSettingsAndAvatarIdentity()
    {
        var settings = EchoObject.NewCompound();
        settings["avatarDefinition"] = new EchoObject(1);
        settings["avatarSourceGuid"] = new EchoObject(Guid.NewGuid().ToString());
        settings["recalculateNormals"] = new EchoObject(true);
        settings["virtualGeometry"] = EchoObject.NewCompound();
        settings["virtualGeometry"]["future"] = new EchoObject("保留");
        settings[SDFFeatureSpec.KeyRoot] = EchoObject.NewCompound();
        settings[SDFFeatureSpec.KeyRoot]["future"] = new EchoObject(73);
        var entry = CreateModel("Model.fbx", settings);
        var editor = new ModelAssetEditor();
        Load(editor, entry);
        Set(editor, "_unitScale", 2f);
        Save(editor, entry);
        var saved = MetaFile.Read(AssetAbsolutePath(entry.Path) + ".meta");
        Assert.Equal(entry.Guid, saved.Guid);
        Assert.Equal("EditorModelImporter", saved.ImporterType);
        Assert.Equal(19, saved.ImporterVersion);
        Assert.Equal(1, saved.Settings!["avatarDefinition"].IntValue);
        Assert.Equal(settings["avatarSourceGuid"], saved.Settings["avatarSourceGuid"]);
        Assert.Equal(settings["virtualGeometry"], saved.Settings["virtualGeometry"]);
        Assert.True(saved.Settings["recalculateNormals"].BoolValue);
        Assert.Equal(73, saved.Settings[SDFFeatureSpec.KeyRoot]["future"].IntValue);
        Assert.Equal(2f, saved.Settings["unitScale"].FloatValue);
        Assert.False(saved.Settings.TryGet("globalScale", out _));
    }

    [Fact]
    public void LoadingSparseSettingsResetsPreviousModelValues()
    {
        var settings = EchoObject.NewCompound();
        settings["unitScale"] = new EchoObject(100f);
        settings["stripBones"] = new EchoObject(true);
        settings[SDFFeatureSpec.KeyRoot] = EchoObject.NewCompound();
        settings[SDFFeatureSpec.KeyRoot][SDFFeatureSpec.Key_Enabled] = new EchoObject(true);
        var first = CreateModel("First.fbx", settings);
        var second = CreateModel("Second.fbx", null);
        var editor = new ModelAssetEditor();
        Load(editor, first);
        Load(editor, second);
        Assert.Equal(1f, Get<float>(editor, "_unitScale"));
        Assert.False(Get<bool>(editor, "_generateSDF"));
        Assert.True(Get<bool>(editor, "_generateSmoothNormals"));
        Assert.True(Get<bool>(editor, "_flipUVs"));
        Assert.False(Get<bool>(editor, "_stripBones"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrCorruptMetaIsNeverRecreatedByApply(bool corrupt)
    {
        var entry = CreateModel("Model.fbx", null);
        var editor = new ModelAssetEditor();
        Load(editor, entry);
        var path = AssetAbsolutePath(entry.Path) + ".meta";
        if (corrupt) File.WriteAllText(path, "not a meta document");
        else File.Delete(path);
        Set(editor, "_unitScale", 2f);
        Save(editor, entry);
        if (corrupt) Assert.Equal("not a meta document", File.ReadAllText(path));
        else Assert.False(File.Exists(path));
    }

    [Fact]
    public void ExternalMetaChangeIsNotOverwritten()
    {
        var entry = CreateModel("Model.fbx", EchoObject.NewCompound());
        var editor = new ModelAssetEditor();
        Load(editor, entry);
        var path = AssetAbsolutePath(entry.Path) + ".meta";
        var external = MetaFile.Read(path);
        external.Settings!["unitScale"] = new EchoObject(5f);
        MetaFile.Write(path, external);
        var bytes = File.ReadAllBytes(path);
        Set(editor, "_unitScale", 2f);
        Save(editor, entry);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void NoOpKeepsSparseDocumentBytesAndTimestamp()
    {
        var entry = CreateModel("Model.fbx", EchoObject.NewCompound());
        var editor = new ModelAssetEditor();
        Load(editor, entry);
        var path = AssetAbsolutePath(entry.Path) + ".meta";
        File.SetLastWriteTimeUtc(path, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var bytes = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        Save(editor, entry);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void ApplyPreservesUnknownDocumentFieldsAndUneditedNormalizedValues()
    {
        var settings = EchoObject.NewCompound();
        settings["skinBoneCount"] = new EchoObject(100);
        var entry = CreateModel("Model.fbx", settings);
        var path = AssetAbsolutePath(entry.Path) + ".meta";
        var document = EchoObject.ReadFromString(File.ReadAllText(path));
        document["futureDocumentData"] = new EchoObject("keep this");
        File.WriteAllText(path, document.WriteToString());
        var editor = new ModelAssetEditor();
        Load(editor, entry);
        Assert.Equal(4, Get<int>(editor, "_skinBoneCount")); // unsupported stored value uses the legacy effective limit.
        Set(editor, "_unitScale", 3f);
        Save(editor, entry);
        var saved = EchoObject.ReadFromString(File.ReadAllText(path));
        Assert.Equal("keep this", saved["futureDocumentData"].StringValue);
        Assert.Equal(100, saved["settings"]["skinBoneCount"].IntValue);
        Assert.False(saved["settings"].TryGet("avatarDefinition", out _));
    }

    [Fact]
    public void EditingOneSdfFieldPreservesNestedUnknownsAndAbsentDefaults()
    {
        var settings = EchoObject.NewCompound();
        var sdf = EchoObject.NewCompound();
        sdf["future"] = new EchoObject(13);
        sdf[SDFFeatureSpec.Key_Padding] = new EchoObject(0.2f);
        settings[SDFFeatureSpec.KeyRoot] = sdf;
        var entry = CreateModel("Model.fbx", settings);
        var editor = new ModelAssetEditor();
        Load(editor, entry);
        Set(editor, "_sdfPadding", 0.3f);
        Save(editor, entry);
        var saved = MetaFile.Read(AssetAbsolutePath(entry.Path) + ".meta").Settings![SDFFeatureSpec.KeyRoot];
        Assert.Equal(13, saved["future"].IntValue);
        Assert.Equal(0.3f, saved[SDFFeatureSpec.Key_Padding].FloatValue);
        Assert.False(saved.TryGet(SDFFeatureSpec.Key_Enabled, out _));
        Assert.False(saved.TryGet(SDFFeatureSpec.Key_Resolution, out _));
    }

    [Fact]
    public void ChangeBackToInitialValueIsNoOp()
    {
        var entry = CreateModel("Model.fbx", null);
        var editor = new ModelAssetEditor();
        Load(editor, entry);
        var path = AssetAbsolutePath(entry.Path) + ".meta";
        var bytes = File.ReadAllBytes(path);
        Set(editor, "_unitScale", 2f);
        Set(editor, "_unitScale", 1f);
        Save(editor, entry);
        Assert.False(Get<bool>(editor, "_lastSettingsWrite"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void RevertReloadsExternalChangesWithoutWriting()
    {
        var entry = CreateModel("Model.fbx", null);
        var editor = new ModelAssetEditor();
        Load(editor, entry);
        Set(editor, "_unitScale", 2f);
        Set(editor, "_settingsDirty", true);
        var path = AssetAbsolutePath(entry.Path) + ".meta";
        var meta = MetaFile.Read(path);
        meta.Settings = EchoObject.NewCompound();
        meta.Settings["unitScale"] = new EchoObject(5f);
        MetaFile.Write(path, meta);
        var bytes = File.ReadAllBytes(path);
        Load(editor, entry);
        Assert.Equal(5f, Get<float>(editor, "_unitScale"));
        Assert.False(Get<bool>(editor, "_settingsDirty"));
        Assert.Null(Get<string?>(editor, "_settingsError"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void FailedRevertKeepsDraftAndShowsError()
    {
        var entry = CreateModel("Model.fbx", null);
        var editor = new ModelAssetEditor();
        Load(editor, entry);
        Set(editor, "_unitScale", 2f);
        Set(editor, "_settingsDirty", true);
        File.WriteAllText(AssetAbsolutePath(entry.Path) + ".meta", "broken");
        Load(editor, entry);
        Assert.Equal(2f, Get<float>(editor, "_unitScale"));
        Assert.True(Get<bool>(editor, "_settingsDirty"));
        Assert.NotEmpty(Get<string>(editor, "_settingsError"));
    }

    [Theory]
    [InlineData("guid")]
    [InlineData("importer")]
    [InlineData("settings")]
    public void InvalidIdentityOrSettingsCannotLoadOrOverwrite(string field)
    {
        var entry = CreateModel("Model.fbx", null);
        var path = AssetAbsolutePath(entry.Path) + ".meta";
        var document = EchoObject.ReadFromString(File.ReadAllText(path));
        document[field] = new EchoObject(field == "guid" ? Guid.NewGuid().ToString() : "invalid");
        File.WriteAllText(path, document.WriteToString());
        var bytes = File.ReadAllBytes(path);
        var editor = new ModelAssetEditor();
        Load(editor, entry);
        Set(editor, "_unitScale", 2f);
        Save(editor, entry);
        Assert.NotEmpty(Get<string>(editor, "_settingsError"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void RemovedSourceAndForeignTargetCannotApply()
    {
        var first = CreateModel("First.fbx", null);
        var second = CreateModel("Second.fbx", null);
        var editor = new ModelAssetEditor();
        Load(editor, first);
        var secondPath = AssetAbsolutePath(second.Path) + ".meta";
        var secondBytes = File.ReadAllBytes(secondPath);
        Set(editor, "_unitScale", 2f);
        Save(editor, second);
        Assert.Equal(secondBytes, File.ReadAllBytes(secondPath));
        File.Delete(AssetAbsolutePath(first.Path));
        Save(editor, first);
        Assert.False(Get<bool>(editor, "_lastSettingsWrite"));
        Assert.NotEmpty(Get<string>(editor, "_settingsError"));
        Assert.Null(MetaFile.Read(AssetAbsolutePath(first.Path) + ".meta").Settings);
    }

    [Fact]
    public void PreviousSessionCannotOwnCallbacksAfterReloadOrSelectionChange()
    {
        var first = CreateModel("First.fbx", null);
        var second = CreateModel("Second.fbx", null);
        var editor = new ModelAssetEditor();
        Load(editor, first);
        var session = Get<object>(editor, "_settingsSession");
        var owns = typeof(ModelAssetEditor).GetMethod("Owns", Private)!;
        Assert.True((bool)owns.Invoke(editor, [session])!);
        Load(editor, first);
        Assert.False((bool)owns.Invoke(editor, [session])!);
        var replacement = Get<object>(editor, "_settingsSession");
        Load(editor, second);
        Assert.False((bool)owns.Invoke(editor, [replacement])!);
        Set(editor, "_currentGuid", Guid.NewGuid());
        Assert.False((bool)owns.Invoke(editor, [Get<object>(editor, "_settingsSession")])!);
    }

    [ModelSettingsWindowsFact]
    public void WriteFailureKeepsPendingStateAndOriginalFile()
    {
        var entry = CreateModel("Model.fbx", null);
        var editor = new ModelAssetEditor();
        Load(editor, entry);
        Set(editor, "_unitScale", 2f);
        Set(editor, "_settingsDirty", true);
        var path = AssetAbsolutePath(entry.Path) + ".meta";
        var bytes = File.ReadAllBytes(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Save(editor, entry);
            Assert.True(Get<bool>(editor, "_settingsDirty"));
            Assert.NotEmpty(Get<string>(editor, "_settingsError"));
            Assert.False(Get<bool>(editor, "_lastSettingsWrite"));
        }
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Project.AssetsPath, "*.model-settings-*.tmp"));
    }
}

internal sealed class ModelSettingsWindowsFactAttribute : FactAttribute
{
    public ModelSettingsWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Exercises Windows deny-delete file sharing during atomic replacement.";
    }
}
