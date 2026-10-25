// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Linq;

using XEngine.Editor.AssetBundles.Builder;
using XEngine.Editor.AssetBundles.Collector;
using XEngine.Editor.Projects;
using XEngine.Runtime;
using XEngine.Runtime.AssetBundles;

using Xunit;

// Both XEngine.Runtime.AssetBundles and XEngine.Editor.AssetBundles.Builder define an
// AssetBundleBuildOptions. Alias the editor one (the one AssetBundleBuilder.Build takes).
using EditorBuildOptions = XEngine.Editor.AssetBundles.Builder.AssetBundleBuildOptions;

namespace XEngine.Editor.Test.AssetBundles;

/// <summary>
/// Tests for the editor-side AssetBundleBuilder: configure a collector over real project assets,
/// run the build, and verify it produces bundle files + a loadable manifest. Round-trips with the
/// runtime Offline tests (the manifest the builder writes is the one BuiltinStorage reads).
/// </summary>
public class AssetBundleBuilderTests : EditorTestHarness
{
    [Fact]
    public void Collect_DefaultGroupPacksSeparately_AndGroupModeCombinesCollectors()
    {
        CreateCachedAsset("First/A.tex", "a");
        CreateCachedAsset("Second/B.tex", "b");
        var package = new AssetBundleCollectorPackage();
        var group = new AssetBundleCollectorGroup { GroupName = "Shared" };
        group.Collectors.Add(new AssetBundleCollector { CollectPath = "First" });
        group.Collectors.Add(new AssetBundleCollector { CollectPath = "Second" });
        package.Groups.Add(group);
        Assert.True(package.EnableAddressable);
        Assert.Equal(2, AssetBundleBuilder.CollectBuildCollection(package).BundleNames.Count);
        group.PackRule = CollectorPackRule.PackByGroup;
        Assert.Single(AssetBundleBuilder.CollectBuildCollection(package).BundleNames);
    }

    [Fact]
    public void Collect_RejectsCaseInsensitiveDuplicateAddresses()
    {
        CreateCachedAsset("First/Name.tex", "a");
        CreateCachedAsset("Second/name.tex", "b");
        var package = new AssetBundleCollectorPackage();
        var group = new AssetBundleCollectorGroup();
        group.Collectors.Add(new AssetBundleCollector { CollectPath = "First" });
        group.Collectors.Add(new AssetBundleCollector { CollectPath = "Second" });
        package.Groups.Add(group);
        var collection = AssetBundleBuilder.CollectBuildCollection(package);
        Assert.NotEmpty(collection.AddressErrors);
        Assert.Contains(collection.AddressErrors, message => message.Contains("Duplicate Addressable"));
        package.EnableAddressable = false;
        Assert.Equal(2, AssetBundleBuilder.CollectBuildCollection(package).AssetToBundle.Count);
    }

    private readonly string _outputRoot;

    [Fact]
    public void Collect_LaterExplicitRootGetsAddressAfterDependencyWasAssigned()
    {
        Guid scene = CreateCachedAsset("Scenes/Main.scene", "scene");
        Guid config = CreateCachedAsset("Config/Hero.asset", "config");
        Assets.Dependencies.SetDependencies(scene, new[] { config });
        var package = new AssetBundleCollectorPackage();
        var group = new AssetBundleCollectorGroup();
        group.Collectors.Add(new AssetBundleCollector { CollectPath = "Scenes" });
        group.Collectors.Add(new AssetBundleCollector { CollectPath = "Config", AddressRule = CollectorAddressRule.ByAssetPath });
        package.Groups.Add(group);
        Assert.Equal("Config/Hero", AssetBundleBuilder.CollectBuildCollection(package).AssetAddress[config]);
    }

    public AssetBundleBuilderTests()
    {
        _outputRoot = Path.Combine(Path.GetTempPath(), "xengine-ab-builder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outputRoot);
    }

    public override void Dispose()
    {
        base.Dispose();
        try { Directory.Delete(_outputRoot, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Create a simple EngineObject asset (cached Echo blob) at a relative path and return
    /// its GUID. We write the cache blob directly so we don't depend on a specific importer.</summary>
    private Guid CreateCachedAsset(string relativePath, string marker)
    {
        // Build a minimal Echo blob with a $type so the runtime can deserialize it.
        var tag = XEngine.Echo.EchoObject.NewCompound();
        tag["marker"] = new XEngine.Echo.EchoObject(marker);
        tag["$type"] = new XEngine.Echo.EchoObject(typeof(BuilderStubAsset).FullName ?? "BuilderStubAsset");
        using var ms = new MemoryStream();
        using (var bw = new System.IO.BinaryWriter(ms)) tag.WriteToBinary(bw);
        byte[] blob = ms.ToArray();

        string abs = AssetAbsolutePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllBytes(abs, blob); // the "asset" file (its bytes happen to be an Echo blob)
        Guid guid = Assets.ImportFile(relativePath);

        // Also place the same bytes in Library/cache/{guid}.asset, which is what AssetBundleBuilder
        // reads as the bundle payload.
        string cachePath = Path.Combine(Project.CachePath, $"{guid}.asset");
        File.WriteAllBytes(cachePath, blob);
        return guid;
    }

    /// <summary>Stub asset type the test payloads claim to be (never actually instantiated; the
    /// runtime deserializer just needs a resolvable $type name).</summary>
    public sealed class BuilderStubAsset : XEngine.Runtime.EngineObject
    {
        public string Marker = string.Empty;
    }

    [Fact]
    public void Build_PackTogether_ProducesOneBundlePerCollector()
    {
        CreateCachedAsset("Textures/Grass.tex", "grass");
        CreateCachedAsset("Textures/Stone.tex", "stone");

        var package = new AssetBundleCollectorPackage { PackageName = "testpkg", EnableAddressable = true };
        var group = new AssetBundleCollectorGroup { GroupName = "textures" };
        group.Collectors.Add(new AssetBundleCollector
        {
            CollectPath = "Textures",
            PackRule = CollectorPackRule.PackTogether,
            AddressRule = CollectorAddressRule.ByFileName,
            FilterRule = CollectorFilterRule.MainAssets,
        });
        package.Groups.Add(group);

        var result = AssetBundleBuilder.Build(package, new EditorBuildOptions
        {
            Version = "1.0.0",
            Platform = AssetBundlePlatform.Any,
            Compression = BundleCompressionMode.Lz4Block,
            OutputRoot = _outputRoot,
        });

        Assert.Equal(1, result.BundleCount); // PackTogether -> 1 bundle for both textures
        string pkgDir = Path.Combine(_outputRoot, "testpkg");
        Assert.Equal(1, Directory.GetFiles(pkgDir, "*.bundle").Length);
        Assert.True(File.Exists(Path.Combine(pkgDir, PackageFileNames.ManifestFile("testpkg", "1.0.0"))));
        Assert.True(File.Exists(Path.Combine(pkgDir, PackageFileNames.VersionFile("testpkg"))));
    }

    [Fact]
    public void Build_PackSeparately_ProducesOneBundlePerAsset()
    {
        var g1 = CreateCachedAsset("Tex/A.tex", "a");
        var g2 = CreateCachedAsset("Tex/B.tex", "b");
        var g3 = CreateCachedAsset("Tex/C.tex", "c");

        var package = new AssetBundleCollectorPackage { PackageName = "sep", EnableAddressable = true };
        var group = new AssetBundleCollectorGroup { GroupName = "g" };
        group.Collectors.Add(new AssetBundleCollector
        {
            CollectPath = "Tex",
            PackRule = CollectorPackRule.PackSeparately,
            AddressRule = CollectorAddressRule.ByFileName,
        });
        package.Groups.Add(group);

        var result = AssetBundleBuilder.Build(package, new EditorBuildOptions
        {
            Version = "2.0.0",
            OutputRoot = _outputRoot,
            WriteReport = true,
        });

        Assert.Equal(3, result.BundleCount);
        Assert.Equal(3, result.AssetCount);
        string pkgDir = Path.Combine(_outputRoot, "sep");
        Assert.Equal(3, Directory.GetFiles(pkgDir, "*.bundle").Length);
        Assert.True(File.Exists(Path.Combine(pkgDir, PackageFileNames.ReportFile("sep", "2.0.0"))));
    }

    [Fact]
    public void Build_ManifestIsLoadableByRuntimeSerializer()
    {
        CreateCachedAsset("Data/X.tex", "x");

        var package = new AssetBundleCollectorPackage { PackageName = "loadpkg", EnableAddressable = true };
        var group = new AssetBundleCollectorGroup { GroupName = "g" };
        group.Collectors.Add(new AssetBundleCollector { CollectPath = "Data", PackRule = CollectorPackRule.PackTogether });
        package.Groups.Add(group);

        AssetBundleBuilder.Build(package, new EditorBuildOptions { Version = "3.0.0", OutputRoot = _outputRoot });

        string manifestPath = Path.Combine(_outputRoot, "loadpkg", PackageFileNames.ManifestFile("loadpkg", "3.0.0"));
        var manifest = PackageManifestSerializer.DeserializeFromFile(manifestPath);

        Assert.NotNull(manifest);
        Assert.Equal("loadpkg", manifest!.PackageName);
        Assert.Equal("3.0.0", manifest.PackageVersion);
        Assert.Single(manifest.BundleList);
        Assert.Single(manifest.AssetList);

        // The asset's address (ByPath default) and bundle assignment round-trip.
        var asset = manifest.AssetList[0];
        Assert.Equal(manifest.BundleList[0].BundleGuid, manifest.BundleList[asset.BundleId].BundleGuid);
    }

    [Fact]
    public void Build_CollectorIncludesTransitiveDependencies()
    {
        Guid root = CreateCachedAsset("Scenes/Root.scene", "root");
        Guid dependency = CreateCachedAsset("Textures/Probe.tex", "dependency");

        // Model the importer output used by a scene/material reference. The collector must use the
        // dependency graph, not only the collector path, or the dependency silently disappears from
        // the physical bundle even though the regular player AssetCollector retains it.
        Assets.GetEntry(root)!.Dependencies = new[] { dependency };
        Assets.Dependencies.SetDependencies(root, new[] { dependency });

        var package = new AssetBundleCollectorPackage { PackageName = "closure", EnableAddressable = true };
        var group = new AssetBundleCollectorGroup { GroupName = "scene" };
        group.Collectors.Add(new AssetBundleCollector
        {
            CollectPath = "Scenes/Root.scene",
            PackRule = CollectorPackRule.PackTogether,
            AddressRule = CollectorAddressRule.ByPath,
        });
        package.Groups.Add(group);

        var result = AssetBundleBuilder.Build(package, new EditorBuildOptions
        {
            Platform = AssetBundlePlatform.Android,
            OutputRoot = _outputRoot,
        });

        Assert.Equal(1, result.BundleCount);
        Assert.Equal(2, result.AssetCount);
        string bundlePath = Directory.GetFiles(Path.Combine(_outputRoot, "closure"), "*.bundle")[0];
        using var bundle = AssetBundle.LoadFromFile(bundlePath);
        Assert.True(bundle.Contains(root));
        Assert.True(bundle.Contains(dependency));
    }

    [Fact]
    public void Build_CollectorIncludesSubAssetDependencyWithoutParentOrSiblings()
    {
        // The .streamtest importer creates two independently cached AnimationClip sub-assets.
        // The collector root references only the first clip; resolving the dependency must not
        // silently drop it or pull the parent container and its sibling clip into the bundle.
        File.WriteAllText(AssetAbsolutePath("Hero.streamtest"), "test");
        Guid parent = Assets.ImportFile("Hero.streamtest");
        var subAssets = Assets.GetSubAssets(parent);
        Assert.Equal(2, subAssets.Length);
        Guid idle = subAssets[0].Guid;
        Guid attack = subAssets[1].Guid;

        Guid root = CreateCachedAsset("Scenes/SubAssetRoot.scene", "root");
        Assets.GetEntry(root)!.Dependencies = new[] { idle };
        Assets.Dependencies.SetDependencies(root, new[] { idle });

        var package = new AssetBundleCollectorPackage { PackageName = "subasset", EnableAddressable = true };
        var group = new AssetBundleCollectorGroup { GroupName = "scene" };
        group.Collectors.Add(new AssetBundleCollector
        {
            CollectPath = "Scenes/SubAssetRoot.scene",
            PackRule = CollectorPackRule.PackTogether,
            AddressRule = CollectorAddressRule.ByPath,
        });
        package.Groups.Add(group);

        var result = AssetBundleBuilder.Build(package, new EditorBuildOptions { OutputRoot = _outputRoot });

        Assert.Equal(2, result.AssetCount);
        Assert.Contains(root, result.AssetGuids);
        Assert.Contains(idle, result.AssetGuids);
        Assert.DoesNotContain(parent, result.AssetGuids);
        Assert.DoesNotContain(attack, result.AssetGuids);

        string manifestPath = Path.Combine(_outputRoot, "subasset", PackageFileNames.ManifestFile("subasset", "1.0.0"));
        var manifest = PackageManifestSerializer.DeserializeFromFile(manifestPath);
        Assert.NotNull(manifest);
        var clipAsset = Assert.Single(manifest!.AssetList.Where(a => a.AssetGuid == idle));
        Assert.Equal("Hero.streamtest#Idle", clipAsset.AssetPath);

        string bundlePath = Directory.GetFiles(Path.Combine(_outputRoot, "subasset"), "*.bundle").Single();
        using var bundle = AssetBundle.LoadFromFile(bundlePath);
        Assert.True(bundle.Contains(idle));
        Assert.NotNull(bundle.LoadAsset<AnimationClip>(idle));
    }

    [Fact]
    public void Build_FailsBeforeWritingManifestWhenSelectedCacheIsMissing()
    {
        Guid root = CreateCachedAsset("Scenes/MissingCache.scene", "root");
        File.Delete(Path.Combine(Project.CachePath, $"{root}.asset"));

        var package = new AssetBundleCollectorPackage { PackageName = "missing-cache", EnableAddressable = true };
        var group = new AssetBundleCollectorGroup { GroupName = "scene" };
        group.Collectors.Add(new AssetBundleCollector
        {
            CollectPath = "Scenes/MissingCache.scene",
            PackRule = CollectorPackRule.PackTogether,
        });
        package.Groups.Add(group);

        Assert.Throws<InvalidOperationException>(() => AssetBundleBuilder.Build(
            package, new EditorBuildOptions { OutputRoot = _outputRoot }));
        Assert.False(Directory.Exists(Path.Combine(_outputRoot, "missing-cache")));
    }

    [Fact]
    public void CollectAssetSelection_UsesOnlyCollectorRootsAndTheirDependencies()
    {
        Guid root = CreateCachedAsset("Scenes/Root.scene", "root");
        Guid dependency = CreateCachedAsset("Textures/Needed.tex", "dependency");
        Guid unrelated = CreateCachedAsset("Animations/Uncollected.anim", "unrelated");

        Assets.GetEntry(root)!.Dependencies = new[] { dependency };
        Assets.Dependencies.SetDependencies(root, new[] { dependency });

        var package = new AssetBundleCollectorPackage { PackageName = "selection", EnableAddressable = true };
        var group = new AssetBundleCollectorGroup { GroupName = "startup" };
        group.Collectors.Add(new AssetBundleCollector
        {
            CollectPath = "Scenes/Root.scene",
            PackRule = CollectorPackRule.PackTogether,
            AddressRule = CollectorAddressRule.ByPath,
        });
        package.Groups.Add(group);

        var selection = AssetBundleBuilder.CollectAssetSelection(package);

        Assert.Contains(root, selection.RootAssetGuids);
        Assert.Contains(root, selection.AssetGuids);
        Assert.Contains(dependency, selection.AssetGuids);
        Assert.DoesNotContain(dependency, selection.RootAssetGuids);
        Assert.DoesNotContain(unrelated, selection.RootAssetGuids);
        Assert.DoesNotContain(unrelated, selection.AssetGuids);
    }

    [Fact]
    public void CollectAssetSelection_AcceptsVisibleAssetsPrefixForProjectCollectors()
    {
        Guid root = CreateCachedAsset("Scenes/VisibleRoot.scene", "root");

        var package = new AssetBundleCollectorPackage { PackageName = "visible-prefix", EnableAddressable = true };
        var group = new AssetBundleCollectorGroup { GroupName = "startup" };
        group.Collectors.Add(new AssetBundleCollector
        {
            // The editor database stores this as Scenes/VisibleRoot.scene, but project-tree YAML
            // commonly serializes the same location as Assets/Scenes/VisibleRoot.scene.
            CollectPath = "Assets/Scenes/VisibleRoot.scene",
            PackRule = CollectorPackRule.PackTogether,
            AddressRule = CollectorAddressRule.ByPath,
        });
        package.Groups.Add(group);

        var selection = AssetBundleBuilder.CollectAssetSelection(package);

        Assert.Contains(root, selection.RootAssetGuids);
        Assert.Contains(root, selection.AssetGuids);
    }

    [Fact]
    public void Build_EmptyPackage_ProducesWarningAndNoBundles()
    {
        var package = new AssetBundleCollectorPackage { PackageName = "empty", EnableAddressable = true };
        // No groups/collectors.
        var result = AssetBundleBuilder.Build(package, new EditorBuildOptions { Version = "1.0.0", OutputRoot = _outputRoot });

        Assert.Equal(0, result.BundleCount);
        Assert.NotEmpty(result.Warnings);
    }
}
