// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Editor.Inspector;
using XEngine.Editor.Projects;
using XEngine.Editor.Importers;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Tests for <see cref="ScriptableObjectImporter"/>: the importer that turns <c>.asset</c> files
/// (Echo-serialized <see cref="ScriptableObject"/> instances) into loadable runtime assets. Covers
/// the core gap this module fills - before <see cref="ScriptableObjectImporter"/>, <c>.asset</c>
/// files fell through to <see cref="DefaultImporter"/> and never produced a main asset.
/// </summary>
public class ScriptableObjectImporterTests : EditorTestHarness
{
    /// <summary>A concrete ScriptableObject subclass with a mix of serialized field kinds.</summary>
    public class TestDataAsset : ScriptableObject
    {
        public int IntValue = 0;
        public float FloatValue = 0f;
        public string TextValue = "";
        public List<string> Items = new();
        public AssetRef<TestDataAsset> Reference;

        // Runtime-only: never persist, so the value we read after import reflects THIS instance's
        // OnEnable, not whatever the source instance had when it was written to disk.
        [SerializeIgnore] public int EnableCount;
        [SerializeIgnore] public int DisableCount;
        [SerializeIgnore] public int ValidateCount;

        protected override void OnEnable() => EnableCount++;
        protected override void OnDisable() => DisableCount++;
        public override void OnValidate() => ValidateCount++;
    }

    private sealed class ThrowingValidateAsset : ScriptableObject
    {
        protected override void OnEnable() { }
        public override void OnValidate() => throw new InvalidOperationException("validation failed");
    }

    private sealed class ThrowingSerializationAsset : ScriptableObject, ISerializationCallbackReceiver
    {
        public void OnBeforeSerialize() => throw new InvalidOperationException("serialization failed");
        public void OnAfterDeserialize() { }
    }

    [CreateAssetMenu("Transient Test")]
    private sealed class TransientMenuAsset : ScriptableObject
    {
        public static int EnableCount;
        public static int DisableCount;

        protected override void OnEnable() => EnableCount++;
        protected override void OnDisable() => DisableCount++;
    }

    /// <summary>
    /// Write a ScriptableObject to a <c>.asset</c> file exactly the way <c>AssetCreateMenu.CreateAsset</c>
    /// does: serialize with <c>typeof(object)</c> to force a <c>$type</c> envelope, then Echo-text to disk.
    /// Returns the GUID after import.
    /// </summary>
    private Guid WriteAndImportAsset(ScriptableObject instance, string relativePath)
    {
        string abs = AssetAbsolutePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);

        var savedId = instance.AssetID;
        EchoObject echo;
        try
        {
            instance.AssetID = Guid.Empty;
            echo = Serializer.Serialize(typeof(object), instance);
        }
        finally
        {
            instance.AssetID = savedId;
        }

        File.WriteAllText(abs, echo.WriteToString());
        return Assets.ImportFile(relativePath);
    }

    // ── Registration ────────────────────────────────────────────

    [Fact]
    public void ScriptableObjectImporter_IsRegisteredForDotAsset()
    {
        var importer = EditorRegistries.GetImporter(".asset");
        Assert.NotNull(importer);
        Assert.IsType<ScriptableObjectImporter>(importer);
    }

    // ── Core import path ────────────────────────────────────────

    [Fact]
    public void Import_ProducesCorrectSubtype()
    {
        var guid = WriteAndImportAsset(new TestDataAsset { IntValue = 42, TextValue = "hello" }, "Data.asset");

        Assert.NotEqual(Guid.Empty, guid);

        var entry = Assets.GetEntry(guid);
        Assert.NotNull(entry);
        Assert.Equal(typeof(TestDataAsset), entry!.MainAssetType);

        var loaded = AssetDatabase.Get(guid) as TestDataAsset;
        Assert.NotNull(loaded);
        Assert.Equal(42, loaded!.IntValue);
        Assert.Equal("hello", loaded.TextValue);
    }

    [Fact]
    public void Import_FiresOnEnableOnce()
    {
        var guid = WriteAndImportAsset(new TestDataAsset(), "Lifecycle.asset");

        var loaded = AssetDatabase.Get(guid) as TestDataAsset;
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.EnableCount);
        Assert.Equal(1, loaded.ValidateCount);
        Assert.True(loaded.IsEnabled);
    }

    [Fact]
    public void Import_PreservesCollections()
    {
        var guid = WriteAndImportAsset(
            new TestDataAsset { Items = new List<string> { "a", "b", "c" } },
            "Collection.asset");

        var loaded = AssetDatabase.Get(guid) as TestDataAsset;
        Assert.NotNull(loaded);
        Assert.Equal(new[] { "a", "b", "c" }, loaded!.Items);
    }

    [Fact]
    public void Import_SurvivesReopen()
    {
        var guid = WriteAndImportAsset(new TestDataAsset { IntValue = 99 }, "Persist.asset");
        var original = Assert.IsType<TestDataAsset>(AssetDatabase.Get(guid));

        // Close + reopen the database (simulates editor restart: re-scan, read .meta, read cache).
        ReopenDatabase();

        Assert.True(original.IsDisposed);
        Assert.Equal(1, original.DisableCount);

        var entry = Assets.GetEntry(guid);
        Assert.NotNull(entry);
        Assert.Equal(typeof(TestDataAsset), entry!.MainAssetType);

        var loaded = AssetDatabase.Get(guid) as TestDataAsset;
        Assert.NotNull(loaded);
        Assert.Equal(99, loaded!.IntValue);
    }

    [Fact]
    public void Import_TracksAssetRefDependency()
    {
        var dependencyGuid = WriteAndImportAsset(new TestDataAsset(), "Dependency.asset");
        var holderGuid = WriteAndImportAsset(
            new TestDataAsset { Reference = new AssetRef<TestDataAsset>(dependencyGuid) },
            "Holder.asset");

        var entry = Assets.GetEntry(holderGuid);
        Assert.NotNull(entry);
        Assert.Contains(dependencyGuid, entry!.Dependencies);
    }

    [Fact]
    public void Reimport_DisablesOldInstanceAndEnablesReplacement()
    {
        var guid = WriteAndImportAsset(new TestDataAsset { IntValue = 1 }, "Reloaded.asset");
        var original = Assert.IsType<TestDataAsset>(AssetDatabase.Get(guid));

        WriteAssetFile(new TestDataAsset { IntValue = 2 }, "Reloaded.asset");
        Assets.Reimport(guid);

        var replacement = Assert.IsType<TestDataAsset>(AssetDatabase.Get(guid));
        Assert.NotSame(original, replacement);
        Assert.True(original.IsDisposed);
        Assert.Equal(1, original.DisableCount);
        Assert.Equal(1, replacement.EnableCount);
        Assert.Equal(1, replacement.ValidateCount);
        Assert.Equal(2, replacement.IntValue);
    }

    // ── Auto-migration from DefaultImporter ─────────────────────

    [Fact]
    public void StaleDefaultImporter_AutoMigratesToScriptableObjectImporter()
    {
        // Hand-craft an .asset file plus a legacy .meta that names DefaultImporter. A fresh database
        // scan must choose ScriptableObjectImporter from the extension and preserve the old GUID.
        var instance = new TestDataAsset { IntValue = 7 };
        string abs = AssetAbsolutePath("Migrated.asset");
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);

        instance.AssetID = Guid.Empty;
        var echo = Serializer.Serialize(typeof(object), instance);
        File.WriteAllText(abs, echo!.WriteToString());

        // Write a .meta naming DefaultImporter explicitly, so we simulate a legacy entry.
        Guid legacyGuid = Guid.NewGuid();
        string metaPath = abs + ".meta";
        MetaFile.Write(metaPath, new MetaFileData
        {
            Guid = legacyGuid,
            ImporterType = nameof(DefaultImporter),
            ImporterVersion = 1
        });

        ReopenDatabase();

        Guid migratedGuid = Assets.PathToGuid("Migrated.asset");
        Assert.Equal(legacyGuid, migratedGuid);

        var migratedEntry = Assets.GetEntry(migratedGuid);
        Assert.NotNull(migratedEntry);
        Assert.Equal(nameof(ScriptableObjectImporter), migratedEntry!.ImporterType);
        Assert.Equal(typeof(TestDataAsset), migratedEntry!.MainAssetType);

        var loaded = AssetDatabase.Get(migratedGuid) as TestDataAsset;
        Assert.NotNull(loaded);
        Assert.Equal(7, loaded!.IntValue);
    }

    // ── Error handling ──────────────────────────────────────────

    [Fact]
    public void Import_NoTypeTag_IsRejected()
    {
        // A compound with no $type envelope cannot identify a concrete asset class.
        string abs = AssetAbsolutePath("NoType.asset");
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        var empty = EchoObject.NewCompound();
        empty["Name"] = new EchoObject("NoType");
        empty["AssetPath"] = new EchoObject("");
        File.WriteAllText(abs, empty.WriteToString());

        Guid guid = Assets.ImportFile("NoType.asset");
        Assert.NotEqual(Guid.Empty, guid);

        var entry = Assets.GetEntry(guid);
        Assert.NotNull(entry);
        // No main asset type was recorded because the import failed.
        Assert.Null(entry!.MainAssetType);
        Assert.Null(AssetDatabase.Get(guid));
    }

    [Fact]
    public void Import_UnresolvableType_LogsAndReturnsFalse()
    {
        string abs = AssetAbsolutePath("BadType.asset");
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        var echo = EchoObject.NewCompound();
        echo["$type"] = new EchoObject("Fully.Qualified.Type.That.Does.Not.Exist, NoSuchAssembly");
        File.WriteAllText(abs, echo.WriteToString());

        Guid guid = Assets.ImportFile("BadType.asset");
        Assert.NotEqual(Guid.Empty, guid);

        var entry = Assets.GetEntry(guid);
        Assert.NotNull(entry);
        Assert.Null(entry!.MainAssetType);
    }

    [Fact]
    public void Import_NonEngineObjectType_IsRejected()
    {
        string abs = AssetAbsolutePath("WrongBase.asset");
        var echo = EchoObject.NewCompound();
        echo["$type"] = new EchoObject(TypeNameRegistry.GetFullTypeName(typeof(string)));
        echo["$value"] = new EchoObject("not an asset");
        File.WriteAllText(abs, echo.WriteToString());

        Guid guid = Assets.ImportFile("WrongBase.asset");

        Assert.NotEqual(Guid.Empty, guid);
        Assert.Null(Assets.GetEntry(guid)!.MainAssetType);
        Assert.Null(AssetDatabase.Get(guid));
    }

    [Fact]
    public void Import_OnValidateException_DoesNotDiscardAsset()
    {
        var guid = WriteAndImportAsset(new ThrowingValidateAsset(), "InvalidButLoadable.asset");

        var loaded = AssetDatabase.Get(guid);
        Assert.IsType<ThrowingValidateAsset>(loaded);
        Assert.True(((ThrowingValidateAsset)loaded!).IsEnabled);
    }

    [Fact]
    public void SerializeFullAsset_RestoresAssetIdWhenSerializationThrows()
    {
        var asset = new ThrowingSerializationAsset { AssetID = Guid.NewGuid() };
        Guid expected = asset.AssetID;

        Assert.Throws<InvalidOperationException>(() => ScriptableObjectAssetEditor.SerializeFullAsset(asset));

        Assert.Equal(expected, asset.AssetID);
    }

    [Fact]
    public void AssetCreateMenu_UsesScriptableObjectLifecycleForTransientInstance()
    {
        TransientMenuAsset.EnableCount = 0;
        TransientMenuAsset.DisableCount = 0;
        var entry = new AssetMenuEntry
        {
            Type = typeof(TransientMenuAsset),
            Name = "Transient Test",
            Extension = ".asset"
        };

        string? path = AssetCreateMenu.CreateAsset(entry, "");

        Assert.NotNull(path);
        Assert.EndsWith(".asset", path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(AssetAbsolutePath(path!)));
        Assert.Equal(1, TransientMenuAsset.EnableCount);
        Assert.Equal(1, TransientMenuAsset.DisableCount);
    }

    // ── Version bump triggers reimport ──────────────────────────

    [Fact]
    public void VersionBump_TriggersReimport()
    {
        var guid = WriteAndImportAsset(new TestDataAsset { IntValue = 5 }, "Versioned.asset");
        var loaded = AssetDatabase.Get(guid) as TestDataAsset;
        Assert.NotNull(loaded);
        Assert.Equal(5, loaded!.IntValue);

        // Edit the on-disk file (simulating a user change) then reimport. The cache should be
        // refreshed and the new value visible.
        string abs = AssetAbsolutePath("Versioned.asset");
        var updated = new TestDataAsset { IntValue = 999 };
        updated.AssetID = Guid.Empty;
        File.WriteAllText(abs, Serializer.Serialize(typeof(object), updated)!.WriteToString());

        Assets.Reimport(guid);

        var reloaded = AssetDatabase.Get(guid) as TestDataAsset;
        Assert.NotNull(reloaded);
        Assert.Equal(999, reloaded!.IntValue);
    }

    private void WriteAssetFile(ScriptableObject instance, string relativePath)
    {
        string abs = AssetAbsolutePath(relativePath);
        var echo = Serializer.Serialize(typeof(object), instance);
        File.WriteAllText(abs, echo.WriteToString());
    }
}
