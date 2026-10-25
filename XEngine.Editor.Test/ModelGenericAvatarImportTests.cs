using XEngine.Animation.Humanoid;
using XEngine.Echo;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class ModelGenericAvatarImportTests : EditorTestHarness
{
    private const string StaticFbx = """
        ; FBX 7.4.0 project file
        FBXHeaderExtension: { FBXVersion: 7400 }
        Objects: {
            Model: 1, "Model::Rig", "Null" { }
            Model: 2, "Model::Bip001", "Null" { }
            Model: 3, "Model::Weapon", "Null" { }
            Model: 4, "Model::Bip001", "Null" { }
        }
        Connections: {
            C: "OO", 1, 0
            C: "OO", 2, 1
            C: "OO", 3, 0
            C: "OO", 4, 3
        }
        """;

    private ImportContext Import(string source, EchoObject settings, Guid? guid = null)
    {
        string path = AssetAbsolutePath("Generic.fbx");
        File.WriteAllText(path, source);
        return new ImportContext(guid ?? Guid.NewGuid(), path, settings);
    }

    private static EchoObject Settings(string root = "", bool animation = true, int definition = 0)
    {
        var settings = EchoObject.NewCompound();
        settings["animationType"] = new EchoObject(ModelRigBinding.Generic);
        settings["avatarDefinition"] = new EchoObject(definition);
        settings["genericRootPath"] = new EchoObject(root);
        settings["importAnimation"] = new EchoObject(animation);
        return settings;
    }

    private static void Dispose(ImportContext ctx)
    {
        ctx.MainAsset?.Dispose();
        foreach (var asset in ctx.SubAssets) asset.Dispose();
    }

    [Fact]
    public void CreateWithoutClipsPublishesCompleteAvatarWithoutInventingAnimator()
    {
        var ctx = Import(StaticFbx, Settings("Rig/Bip001"));
        try
        {
            Assert.True(new EditorModelImporter().Import(ctx), ctx.Error);
            var avatar = Assert.Single(ctx.SubAssets.OfType<Avatar>());
            Assert.Equal(AvatarRigKind.Generic, avatar.RigKind);
            Assert.Null(avatar.Validate());
            Assert.Equal("Rig/Bip001", avatar.RootMotionBonePath);
            Assert.Equal(new[] { ".", "Rig", "Rig/Bip001", "Weapon", "Weapon/Bip001" },
                avatar.GenericSkeleton.Select(n => n.Path));
            Assert.Empty(ctx.SubAssets.OfType<AnimationClip>());
            using var root = Assert.IsType<PrefabAsset>(ctx.MainAsset).Instantiate();
            Assert.NotNull(root);
            Assert.Null(root.GetComponent<Animator>());
            Assert.Null(root.GetComponent<AnimationComponent>());
        }
        finally { Dispose(ctx); }
    }

    [Theory]
    [InlineData(ModelRigBinding.Generic)]
    [InlineData(ModelRigBinding.Humanoid)]
    [InlineData(ModelRigBinding.Legacy)]
    public void DisablingAnimationRemovesComponentAndEveryUnregisteredClipReference(int type)
    {
        var settings = Settings(animation: false, definition: 2);
        settings["animationType"] = new EchoObject(type);
        var ctx = Import(ModelFileUnitTests.Fbx(1), settings);
        try
        {
            Assert.True(new EditorModelImporter().Import(ctx), ctx.Error);
            Assert.Empty(ctx.SubAssets.OfType<AnimationClip>());
            using var root = Assert.IsType<PrefabAsset>(ctx.MainAsset).Instantiate();
            Assert.NotNull(root);
            Assert.Null(root.GetComponent<Animator>());
            Assert.Null(root.GetComponent<AnimationComponent>());
        }
        finally { Dispose(ctx); }
    }

    [Theory]
    [InlineData("", ClipRootMotionPolicy.Disabled)]
    [InlineData(".", ClipRootMotionPolicy.ExplicitBone)]
    [InlineData("Triangle", ClipRootMotionPolicy.ExplicitBone)]
    public void AnimatedCreateBindsRegisteredAvatarAndExactRigPolicy(string path, ClipRootMotionPolicy policy)
    {
        var settings = Settings(path);
        string originalSettings = settings.WriteToString();
        var ctx = Import(ModelFileUnitTests.Fbx(1), settings);
        try
        {
            Assert.True(new EditorModelImporter().Import(ctx), ctx.Error);
            var avatar = Assert.Single(ctx.SubAssets.OfType<Avatar>());
            var clip = Assert.Single(ctx.SubAssets.OfType<AnimationClip>());
            Assert.Equal(policy, clip.RootMotionPolicy);
            Assert.Equal(path, clip.RootBonePath);
            using var root = Assert.IsType<PrefabAsset>(ctx.MainAsset).Instantiate();
            var animator = Assert.IsType<Animator>(root!.GetComponent<Animator>());
            Assert.Equal(avatar.AssetID, animator.Avatar.AssetID);
            Assert.Equal(clip.AssetID, animator.Clip.AssetID);
            Assert.False(clip.IsHumanoid);
            Assert.Empty(clip.MuscleCurves);
            Assert.Equal(originalSettings, settings.WriteToString());
        }
        finally { Dispose(ctx); }
    }

    [Fact]
    public void CreateWithAnimationDisabledStillPublishesAvatarWithoutClipReferences()
    {
        var ctx = Import(ModelFileUnitTests.Fbx(1), Settings("Triangle", animation: false));
        try
        {
            Assert.True(new EditorModelImporter().Import(ctx), ctx.Error);
            Assert.Equal("Triangle", Assert.Single(ctx.SubAssets.OfType<Avatar>()).RootMotionBonePath);
            Assert.Empty(ctx.SubAssets.OfType<AnimationClip>());
            using var root = Assert.IsType<PrefabAsset>(ctx.MainAsset).Instantiate();
            Assert.Null(root!.GetComponent<Animator>());
        }
        finally { Dispose(ctx); }
    }

    [Theory]
    [InlineData("Bip001")]
    [InlineData("rig/Bip001")]
    [InlineData("Rig\\Bip001")]
    [InlineData("Rig/../Weapon/Bip001")]
    [InlineData("Missing")]
    public void MissingOrAmbiguousLeafNeverFallsBackAndCannotPublishMainAsset(string path)
    {
        var ctx = Import(StaticFbx, Settings(path));
        try
        {
            Assert.False(new EditorModelImporter().Import(ctx));
            Assert.NotEmpty(ctx.Error!);
            Assert.Contains(path, ctx.Error);
            Assert.Null(ctx.MainAsset);
            Assert.Empty(ctx.SubAssets.OfType<Avatar>());
        }
        finally { Dispose(ctx); }
    }

    [Fact]
    public void NonStringRootIsRejectedWithoutNormalizingSettings()
    {
        var settings = Settings(); settings["genericRootPath"] = new EchoObject(17);
        string before = settings.WriteToString(); var ctx = Import(StaticFbx, settings);
        try
        {
            Assert.False(new EditorModelImporter().Import(ctx));
            Assert.Contains("string", ctx.Error);
            Assert.Null(ctx.MainAsset);
            Assert.Equal(before, settings.WriteToString());
        }
        finally { Dispose(ctx); }
    }

    [Theory]
    [InlineData(1)] // unavailable Generic Copy retains its prior ignored semantics until N3e1b.
    [InlineData(2)]
    [InlineData(73)]
    public void OldNonCreateSettingsRetainClipPolicyAndExistingSubAssetIdentities(int definition)
    {
        var guid = Guid.NewGuid();
        var old = Import(ModelFileUnitTests.Fbx(1), Settings(definition: definition), guid);
        var create = Import(ModelFileUnitTests.Fbx(1), Settings("Triangle"), guid);
        try
        {
            Assert.True(new EditorModelImporter().Import(old), old.Error);
            Assert.True(new EditorModelImporter().Import(create), create.Error);
            Assert.Empty(old.SubAssets.OfType<Avatar>());
            Assert.Equal(ClipRootMotionPolicy.LegacyAutomatic,
                Assert.Single(old.SubAssets.OfType<AnimationClip>()).RootMotionPolicy);
            foreach (var asset in old.SubAssets)
                Assert.Contains(create.SubAssets, other => other.GetType() == asset.GetType()
                    && other.Name == asset.Name && other.AssetID == asset.AssetID);
            Assert.Equal(AssetEntry.DeriveSubAssetGuid(guid, EditorModelImporter.GenericAvatarStableId),
                Assert.Single(create.SubAssets.OfType<Avatar>()).AssetID);
        }
        finally { Dispose(old); Dispose(create); }
    }

    [Fact]
    public void AvatarIdentitySurvivesNoneCreateAndDisplayNameChange()
    {
        Guid guid = Guid.NewGuid(); var importer = new EditorModelImporter();
        var first = Import(StaticFbx, Settings("Rig/Bip001"), guid);
        var none = Import(StaticFbx, Settings(definition: 2), guid);
        var renamed = new ImportContext(guid, AssetAbsolutePath("Renamed.fbx"), Settings("Weapon/Bip001"));
        File.WriteAllText(renamed.AbsolutePath, StaticFbx);
        try
        {
            Assert.True(importer.Import(first), first.Error);
            Assert.True(importer.Import(none), none.Error);
            Assert.True(importer.Import(renamed), renamed.Error);
            var a = Assert.Single(first.SubAssets.OfType<Avatar>());
            var b = Assert.Single(renamed.SubAssets.OfType<Avatar>());
            Assert.NotEqual(a.Name, b.Name);
            Assert.Equal(a.AssetID, b.AssetID);
            Assert.Empty(none.SubAssets.OfType<Avatar>());
        }
        finally { Dispose(first); Dispose(none); Dispose(renamed); }
    }

    [Fact]
    public void InvalidRootReimportPreservesLastUsableDiskOutputsAndRestartIdentity()
    {
        string path = AssetAbsolutePath("Atomic.fbx"); File.WriteAllText(path, ModelFileUnitTests.Fbx(1));
        Guid guid = Guid.NewGuid();
        MetaFile.Write(path + ".meta", new MetaFileData { Guid = guid,
            ImporterType = nameof(EditorModelImporter), Settings = Settings("Triangle") });
        Assert.Equal(guid, Assets.ImportFile("Atomic.fbx"));
        var entry = Assets.GetEntry(guid)!;
        var beforeSubs = entry.SubAssets.Select(s => s.Guid).ToArray();
        Assert.Contains(entry.SubAssets, s => s.Type == typeof(Avatar));
        var before = beforeSubs.Append(guid).ToDictionary(id => id,
            id => File.ReadAllBytes(Path.Combine(Project.CachePath, id + ".asset")));
        var meta = MetaFile.Read(path + ".meta"); meta.Settings!["genericRootPath"] = new EchoObject("Missing");
        MetaFile.Write(path + ".meta", meta);
        var result = Assets.ReimportWithResult(guid);
        Assert.False(result.Succeeded);
        Assert.Contains("Missing", result.Error);
        Assert.Equal(beforeSubs, entry.SubAssets.Select(s => s.Guid));
        foreach (var pair in before)
            Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(Project.CachePath, pair.Key + ".asset")));
        meta.Settings["genericRootPath"] = new EchoObject(""); MetaFile.Write(path + ".meta", meta);
        Assert.True(Assets.ReimportWithResult(guid).Succeeded);
        Assert.Equal(beforeSubs, entry.SubAssets.Select(s => s.Guid));
        ReopenDatabase();
        Assert.Equal(beforeSubs, Assets.GetEntry(guid)!.SubAssets.Select(s => s.Guid));
        var clipId = Assert.Single(Assets.GetEntry(guid)!.SubAssets, s => s.Type == typeof(AnimationClip)).Guid;
        Assert.Equal(ClipRootMotionPolicy.Disabled, Assert.IsType<AnimationClip>(Assets.Get(clipId)).RootMotionPolicy);
    }
}
