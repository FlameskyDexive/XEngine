using System.Reflection;
using XEngine.Echo;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Inspector;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Scribe;
using Xunit;
using Surface = XEngine.Editor.Test.InspectorComponentHeaderTests.Fixture;

namespace XEngine.Editor.Test;

public sealed class ModelImporterCapabilityTests : EditorTestHarness
{
    [Theory]
    [InlineData(280f)] [InlineData(335f)] [InlineData(520f)]
    public void GenericCreateCaptionPreservesMalformedRootWithoutThrowingOrDirtying(float width)
    {
        var entry = Prepare();
        string path = AssetAbsolutePath(entry.Path) + ".meta";
        var meta = MetaFile.Read(path);
        meta.Settings!["avatarDefinition"] = new EchoObject(0);
        meta.Settings["genericRootPath"] = EchoObject.NewCompound();
        meta.Settings["genericRootPath"]["future"] = new EchoObject(73);
        MetaFile.Write(path, meta);
        byte[] bytes = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        var editor = new ModelAssetEditor(); using var f = new Surface(width);
        Frame(f, editor, entry, width); Tab(editor, ModelImportTab.Rig); Frame(f, editor, entry, width);
        var note = Assert.Single(f.Elements(), e => e.Paragraph?.StartsWith("Avatar Definition:") == true);
        Assert.Contains("Stored Root: Invalid (Compound)", note.Paragraph);
        Assert.True(note.IsNotInteractable); Assert.Null(note.OnClick);
        Assert.InRange(note.X + note.LayoutWidth, 1, width);
        Assert.False(Get<bool>(editor, "_settingsDirty"));
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Get<T>(ModelAssetEditor editor, string field)
        => (T)typeof(ModelAssetEditor).GetField(field, Private)!.GetValue(editor)!;
    private static void Tab(ModelAssetEditor editor, ModelImportTab tab)
        => typeof(ModelAssetEditor).GetProperty(nameof(ModelAssetEditor.ActiveTab))!.SetValue(editor, tab);

    private AssetEntry Prepare(string name = "Model.fbx")
    {
        var entry = new AssetEntry { Guid = Guid.NewGuid(), Path = name, ImporterType = "EditorModelImporter" };
        File.WriteAllText(AssetAbsolutePath(name), "presentation only; not imported");
        var settings = EchoObject.NewCompound();
        settings["globalScale"] = new EchoObject(true);
        settings["skinBoneCount"] = new EchoObject(100); // keep exact stored value, not the UI clamp.
        settings["stripBones"] = new EchoObject(false);
        settings["resampleCurves"] = new EchoObject(false);
        settings["avatarDefinition"] = new EchoObject(1);
        settings["avatarSourceGuid"] = new EchoObject(Guid.NewGuid().ToString());
        settings["future"] = EchoObject.NewCompound(); settings["future"]["keep"] = new EchoObject(73);
        MetaFile.Write(AssetAbsolutePath(name) + ".meta", new MetaFileData
        { Guid = entry.Guid, ImporterType = entry.ImporterType, ImporterVersion = 19, Settings = settings });
        return entry;
    }

    private static void Frame(Surface f, ModelAssetEditor editor, AssetEntry entry, float width, Action? input = null)
    {
        var paper = f.Paper;
        paper.BeginFrame(1f / 60); Origami.BeginFrame(paper, 1f / 60); input?.Invoke();
        using (paper.Column("model_host").Size(width, 900).Enter()) editor.OnGUI(paper, "model", entry, null);
        Origami.EndFrame(paper); paper.EndFrame();
    }

    private static void Click(Surface f, ModelAssetEditor editor, AssetEntry entry, float width, float x, float y)
    {
        f.Paper.SetPointerPosition(x, y); Frame(f, editor, entry, width);
        Frame(f, editor, entry, width, () => f.Paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false));
        Frame(f, editor, entry, width, () => f.Paper.SetPointerState(PaperMouseBtn.Left, x, y, false, false));
        Frame(f, editor, entry, width);
    }

    [Theory]
    [InlineData(280f, ModelImportTab.Model, "File units:")]
    [InlineData(335f, ModelImportTab.Model, "File units:")]
    [InlineData(520f, ModelImportTab.Model, "File units:")]
    [InlineData(280f, ModelImportTab.Rig, "Skin Weights:")]
    [InlineData(335f, ModelImportTab.Rig, "Skin Weights:")]
    [InlineData(520f, ModelImportTab.Rig, "Skin Weights:")]
    [InlineData(280f, ModelImportTab.Animation, "Resample Curves:")]
    [InlineData(335f, ModelImportTab.Animation, "Resample Curves:")]
    [InlineData(520f, ModelImportTab.Animation, "Resample Curves:")]
    public void UnsupportedOptionsAreWrappedReadOnlyAndCannotDirtyOrWrite(float width, ModelImportTab tab, string prefix)
    {
        var entry = Prepare(); var editor = new ModelAssetEditor(); using var f = new Surface(width);
        Frame(f, editor, entry, width); Tab(editor, tab); Frame(f, editor, entry, width);
        var metaPath = AssetAbsolutePath(entry.Path) + ".meta";
        var before = File.ReadAllBytes(metaPath); var stamp = File.GetLastWriteTimeUtc(metaPath);
        var note = Assert.Single(f.Elements().Where(e => e.Paragraph?.StartsWith(prefix) == true));
        Assert.True(note.IsNotInteractable); Assert.Null(note.OnClick);
        Assert.Equal(TextWrapMode.Wrap, note.WrapMode);
        Assert.InRange(note.X + note.LayoutWidth, 1, width);
        Assert.InRange(note.LayoutHeight, 12, 200);
        if (width < 520) Assert.True(note.LayoutHeight > 12); // narrow text actually wraps.
        var retry = f.Elements().Single(e => e.Paragraph?.Contains("Reimport") == true);
        Assert.True(retry.Y >= note.Y + note.LayoutHeight);
        Assert.InRange(retry.Y + retry.LayoutHeight, 1, 900);
        Click(f, editor, entry, width, note.X + 10, note.Y + 10);
        Assert.False(Get<bool>(editor, "_settingsDirty"));
        Assert.Equal(before, File.ReadAllBytes(metaPath)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(metaPath));
        Assert.DoesNotContain(f.Elements(), e => e.Paragraph == "Apply");
        if (tab == ModelImportTab.Rig)
        {
            Assert.Contains("100", note.Paragraph);
            Assert.Contains(f.Elements(), e => e.Paragraph?.StartsWith("Strip Bones:") == true && e.IsNotInteractable);
            Assert.Contains(f.Elements(), e => e.Paragraph?.Contains("Generic Avatar") == true);
        }
        if (tab == ModelImportTab.Animation)
            Assert.Contains(f.Elements(), e => e.Paragraph?.Contains("Range and loop editing") == true);
    }

    [Theory]
    [InlineData(".fbx", false)]
    [InlineData(".OBJ", false)]
    [InlineData(".glTF", true)]
    [InlineData(".glb", true)]
    public void FlipUvIsEditableOnlyForFormatsWhoseImporterConsumesIt(string extension, bool editable)
    {
        var entry = Prepare("Model" + extension); var editor = new ModelAssetEditor(); using var f = new Surface(335);
        Frame(f, editor, entry, 335); Frame(f, editor, entry, 335);
        Assert.Equal(editable, f.Elements().Any(e => e.Paragraph == "Flip UVs"));
        Assert.Equal(!editable, f.Elements().Any(e => e.Paragraph?.StartsWith("Flip UVs:") == true && e.IsNotInteractable));
    }

    [Fact]
    public void FunctionalEditApplyAndRevertPreserveUnsupportedAndUnknownStoredValues()
    {
        const float width = 335;
        var entry = Prepare(); var editor = new ModelAssetEditor(); using var f = new Surface(width);
        Frame(f, editor, entry, width); Frame(f, editor, entry, width);
        var path = AssetAbsolutePath(entry.Path) + ".meta"; var bytes = File.ReadAllBytes(path);
        var original = MetaFile.Read(path).Settings!;
        var label = f.Text("Generate Normals");
        var toggle = f.Elements().Single(e => e.OnClick != null && e.X > label.X
            && e.LayoutWidth <= 40 && e.Y >= label.Y && e.Y < label.Y + label.LayoutHeight);
        Click(f, editor, entry, width, toggle.X + 8, toggle.Y + 8);
        Assert.True(Get<bool>(editor, "_settingsDirty")); Assert.Equal(bytes, File.ReadAllBytes(path));
        var revert = f.Text("Revert"); Click(f, editor, entry, width, revert.X + 10, revert.Y + 10);
        Assert.False(Get<bool>(editor, "_settingsDirty")); Assert.Equal(bytes, File.ReadAllBytes(path));
        label = f.Text("Generate Normals");
        toggle = f.Elements().Single(e => e.OnClick != null && e.X > label.X
            && e.LayoutWidth <= 40 && e.Y >= label.Y && e.Y < label.Y + label.LayoutHeight);
        Click(f, editor, entry, width, toggle.X + 8, toggle.Y + 8);
        // Exercise the real settings Apply path without importing this presentation-only source.
        Assert.True((bool)typeof(ModelAssetEditor).GetMethod("SaveSettingsToMeta", Private)!.Invoke(editor, [entry])!);
        var saved = MetaFile.Read(path).Settings!;
        Assert.False(saved["generateNormals"].BoolValue);
        Assert.False(saved.TryGet("convertFileUnits", out _)); // supported edits do not materialize new defaults.
        foreach (var key in new[] { "globalScale", "skinBoneCount", "stripBones", "resampleCurves", "avatarDefinition", "avatarSourceGuid", "future" })
            Assert.Equal(original[key], saved[key]);
    }

    [Fact]
    public void CapturedFunctionalCallbackCannotEditReplacementModelOrReloadedSession()
    {
        var first = Prepare("First.fbx"); var next = Prepare("Next.glb");
        var editor = new ModelAssetEditor(); using var f = new Surface(335);
        Frame(f, editor, first, 335); Frame(f, editor, first, 335);
        var label = f.Text("Generate Normals");
        var callback = f.Elements().Single(e => e.OnClick != null && e.X > label.X
            && e.LayoutWidth <= 40 && e.Y >= label.Y && e.Y < label.Y + label.LayoutHeight).OnClick;
        Frame(f, editor, next, 335); Frame(f, editor, next, 335);
        callback(default);
        Assert.True(Get<bool>(editor, "_generateNormals")); Assert.False(Get<bool>(editor, "_settingsDirty"));
        Assert.Contains(f.Elements(), e => e.Paragraph == "Flip UVs");
        Frame(f, editor, first, 335); Frame(f, editor, first, 335);
        callback(default);
        Assert.True(Get<bool>(editor, "_generateNormals")); Assert.False(Get<bool>(editor, "_settingsDirty"));
        Assert.Contains(f.Elements(), e => e.Paragraph?.StartsWith("Flip UVs:") == true);
    }

    [Fact]
    public void FailedReloadRetainsExactStoredCapabilityCaptionAndSourceReference()
    {
        var entry = Prepare(); var editor = new ModelAssetEditor(); using var f = new Surface(280);
        Frame(f, editor, entry, 280); Tab(editor, ModelImportTab.Rig); Frame(f, editor, entry, 280);
        string avatar = Get<string>(editor, "_avatarCapability"), skin = Get<string>(editor, "_skinCapability");
        Assert.Contains(MetaFile.Read(AssetAbsolutePath(entry.Path) + ".meta").Settings!["avatarSourceGuid"].StringValue, avatar);
        File.WriteAllText(AssetAbsolutePath(entry.Path) + ".meta", "invalid external meta");
        typeof(ModelAssetEditor).GetMethod("LoadSettingsFromMeta", Private)!.Invoke(editor, [entry]);
        Assert.NotEmpty(Get<string>(editor, "_settingsError"));
        Assert.Equal(avatar, Get<string>(editor, "_avatarCapability")); Assert.Equal(skin, Get<string>(editor, "_skinCapability"));
        Assert.Equal("invalid external meta", File.ReadAllText(AssetAbsolutePath(entry.Path) + ".meta"));
    }

    [Fact]
    public void FileUnitToggleUsesIndependentDefaultAndOwnedDraftApplyRevert()
    {
        const float width = 335;
        var entry = Prepare(); var next = Prepare("Next.fbx");
        var editor = new ModelAssetEditor(); using var f = new Surface(width);
        string path = AssetAbsolutePath(entry.Path) + ".meta";
        var bytes = File.ReadAllBytes(path); var stamp = File.GetLastWriteTimeUtc(path);
        Frame(f, editor, entry, width); Frame(f, editor, entry, width);
        Assert.True(Get<bool>(editor, "_convertFileUnits"));
        var label = f.Text("Convert File Units");
        var toggle = f.Elements().Single(e => e.OnClick != null && e.X > label.X
            && e.LayoutWidth <= 40 && e.Y >= label.Y && e.Y < label.Y + label.LayoutHeight);
        var oldCallback = toggle.OnClick;
        Click(f, editor, entry, width, toggle.X + 8, toggle.Y + 8);
        Assert.False(Get<bool>(editor, "_convertFileUnits")); Assert.True(Get<bool>(editor, "_settingsDirty"));
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        var revert = f.Text("Revert"); Click(f, editor, entry, width, revert.X + 10, revert.Y + 10);
        Assert.True(Get<bool>(editor, "_convertFileUnits")); Assert.False(Get<bool>(editor, "_settingsDirty"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        label = f.Text("Convert File Units");
        toggle = f.Elements().Single(e => e.OnClick != null && e.X > label.X
            && e.LayoutWidth <= 40 && e.Y >= label.Y && e.Y < label.Y + label.LayoutHeight);
        Click(f, editor, entry, width, toggle.X + 8, toggle.Y + 8);
        Assert.True((bool)typeof(ModelAssetEditor).GetMethod("SaveSettingsToMeta", Private)!.Invoke(editor, [entry])!);
        var saved = MetaFile.Read(path).Settings!;
        Assert.False(saved["convertFileUnits"].BoolValue); Assert.True(saved["globalScale"].BoolValue);
        Assert.Equal(100, saved["skinBoneCount"].IntValue); Assert.Equal(73, saved["future"]["keep"].IntValue);
        Frame(f, editor, next, width); Frame(f, editor, next, width); oldCallback(default);
        Assert.True(Get<bool>(editor, "_convertFileUnits")); Assert.False(Get<bool>(editor, "_settingsDirty"));
    }

    [Fact]
    public void SkinInfluenceDropdownEditsOnlySupportedOwnedDraftAndPreservesLegacyUntilChanged()
    {
        const float width = 335;
        var entry = Prepare(); var next = Prepare("Next.fbx");
        var editor = new ModelAssetEditor(); using var f = new Surface(width);
        string path = AssetAbsolutePath(entry.Path) + ".meta"; var bytes = File.ReadAllBytes(path);
        Frame(f, editor, entry, width); Tab(editor, ModelImportTab.Rig); Frame(f, editor, entry, width);
        Assert.Equal(4, Get<int>(editor, "_skinBoneCount"));
        Assert.Contains("100 (unsupported; using 4)", Get<string>(editor, "_skinCapability"));
        var trigger = f.Text("4 Bones"); Click(f, editor, entry, width, trigger.X + 8, trigger.Y + 8);
        var one = f.Text("1 Bone"); var two = f.Text("2 Bones");
        var stale = f.Elements().Where(e => e.OnClick != null && e.X <= two.X && e.Y <= two.Y
            && e.X + e.LayoutWidth >= two.X + two.LayoutWidth && e.Y + e.LayoutHeight >= two.Y + two.LayoutHeight)
            .OrderBy(e => e.LayoutWidth * e.LayoutHeight).First().OnClick;
        Click(f, editor, entry, width, one.X + 8, one.Y + 8);
        Assert.Equal(1, Get<int>(editor, "_skinBoneCount")); Assert.True(Get<bool>(editor, "_settingsDirty"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        var revert = f.Text("Revert"); Click(f, editor, entry, width, revert.X + 10, revert.Y + 10);
        Assert.Equal(4, Get<int>(editor, "_skinBoneCount")); Assert.False(Get<bool>(editor, "_settingsDirty"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        trigger = f.Text("4 Bones"); Click(f, editor, entry, width, trigger.X + 8, trigger.Y + 8);
        two = f.Text("2 Bones"); Click(f, editor, entry, width, two.X + 8, two.Y + 8);
        Assert.Equal(2, Get<int>(editor, "_skinBoneCount"));
        Assert.True((bool)typeof(ModelAssetEditor).GetMethod("SaveSettingsToMeta", Private)!.Invoke(editor, [entry])!);
        var saved = MetaFile.Read(path).Settings!;
        Assert.Equal(2, saved["skinBoneCount"].IntValue); Assert.Equal(73, saved["future"]["keep"].IntValue);
        Assert.True(saved["globalScale"].BoolValue); Assert.False(saved.TryGet("convertFileUnits", out _));
        Frame(f, editor, next, width); Frame(f, editor, next, width); stale(default);
        Assert.Equal(4, Get<int>(editor, "_skinBoneCount")); Assert.False(Get<bool>(editor, "_settingsDirty"));
        Frame(f, editor, entry, width); Frame(f, editor, entry, width); stale(default);
        Assert.Equal(2, Get<int>(editor, "_skinBoneCount")); Assert.False(Get<bool>(editor, "_settingsDirty"));
    }

    [Fact]
    public void StripToggleOwnsDraftRevertSaveAndStaleCallbacksWithoutMaterializingDefaults()
    {
        const float width = 280;
        var entry = Prepare(); var next = Prepare("Next.fbx");
        var editor = new ModelAssetEditor(); using var f = new Surface(width);
        Frame(f, editor, entry, width); Tab(editor, ModelImportTab.Rig); Frame(f, editor, entry, width);
        var path = AssetAbsolutePath(entry.Path) + ".meta"; var original = File.ReadAllBytes(path);
        var label = f.Text("Strip Bones");
        var toggle = f.Elements().Single(e => e.OnClick != null && e.X > label.X
            && e.LayoutWidth <= 40 && e.Y >= label.Y && e.Y < label.Y + label.LayoutHeight);
        var stale = toggle.OnClick;
        Click(f, editor, entry, width, toggle.X + 8, toggle.Y + 8);
        Assert.True(Get<bool>(editor, "_stripBones")); Assert.True(Get<bool>(editor, "_settingsDirty"));
        Assert.Equal(original, File.ReadAllBytes(path));
        var revert = f.Text("Revert"); Click(f, editor, entry, width, revert.X + 8, revert.Y + 8);
        Assert.False(Get<bool>(editor, "_stripBones")); Assert.False(Get<bool>(editor, "_settingsDirty"));
        Assert.Equal(original, File.ReadAllBytes(path));
        label = f.Text("Strip Bones");
        toggle = f.Elements().Single(e => e.OnClick != null && e.X > label.X
            && e.LayoutWidth <= 40 && e.Y >= label.Y && e.Y < label.Y + label.LayoutHeight);
        Click(f, editor, entry, width, toggle.X + 8, toggle.Y + 8);
        Assert.True((bool)typeof(ModelAssetEditor).GetMethod("SaveSettingsToMeta", Private)!.Invoke(editor, [entry])!);
        var settings = MetaFile.Read(path).Settings!;
        Assert.True(settings["stripBones"].BoolValue); Assert.Equal(100, settings["skinBoneCount"].IntValue);
        Assert.Equal(73, settings["future"]["keep"].IntValue); Assert.False(settings.TryGet("convertFileUnits", out _));
        Frame(f, editor, next, width); Frame(f, editor, next, width); stale(default);
        Assert.False(Get<bool>(editor, "_stripBones")); Assert.False(Get<bool>(editor, "_settingsDirty"));
        Frame(f, editor, entry, width); Frame(f, editor, entry, width); stale(default);
        Assert.True(Get<bool>(editor, "_stripBones")); Assert.False(Get<bool>(editor, "_settingsDirty"));
    }
}
