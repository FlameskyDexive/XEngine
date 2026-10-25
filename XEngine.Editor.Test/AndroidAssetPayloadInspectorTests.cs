// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO.Compression;

using XEngine.Editor.Build.Android;
using XEngine.Runtime;
using XEngine.Runtime.AssetBundles;
using XEngine.Runtime.Resources;

using AssetBundlePackageManifest = XEngine.Runtime.AssetBundles.PackageManifest;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class AndroidAssetPayloadInspectorTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineAndroidAssetPayloadTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void XEnginePakInspectionAcceptsExactCollectedClosure()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        string content = Path.Combine(_testRoot, "Content");
        Directory.CreateDirectory(content);
        using (ZipArchive archive = ZipFile.Open(Path.Combine(content, "data_0.xenginepak"), ZipArchiveMode.Create))
        {
            WriteEntry(archive, first, [1, 2, 3, 4]);
            WriteEntry(archive, second, [5, 6, 7]);
        }

        AndroidPhysicalAssetPayload inspection = AndroidAssetPayloadInspector.Inspect(
            _testRoot,
            AssetPackagingMode.XEnginePak,
            [second, first]);

        Assert.True(inspection.IsExactClosure);
        Assert.Equal("exact-xenginepak-closure", inspection.Verification);
        Assert.Equal(2, inspection.Assets.Count);
        Assert.Equal(["Content/data_0.xenginepak"], inspection.Containers);
        Assert.Equal(4, inspection.Assets[first].UncompressedBytes);
    }

    [Fact]
    public void LooseFilesInspectionRejectsAssetsOutsideCollectedClosure()
    {
        Guid expected = Guid.NewGuid();
        Guid unexpected = Guid.NewGuid();
        string content = Path.Combine(_testRoot, "Content");
        Directory.CreateDirectory(content);
        File.WriteAllBytes(Path.Combine(content, expected + ".asset"), [1]);
        File.WriteAllBytes(Path.Combine(content, unexpected + ".asset"), [2]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            AndroidAssetPayloadInspector.Inspect(
                _testRoot,
                AssetPackagingMode.LooseFiles,
                [expected]));

        Assert.Contains("Unexpected", error.Message, StringComparison.Ordinal);
        Assert.Contains(unexpected.ToString("N"), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void XEnginePakInspectionRejectsUnindexedNestedPayload()
    {
        Guid expected = Guid.NewGuid();
        string content = Path.Combine(_testRoot, "Content");
        Directory.CreateDirectory(Path.Combine(content, "leak"));
        using (ZipArchive archive = ZipFile.Open(Path.Combine(content, "data_0.xenginepak"), ZipArchiveMode.Create))
            WriteEntry(archive, expected, [1]);
        File.WriteAllBytes(Path.Combine(content, "leak", "unindexed.asset"), [2]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            AndroidAssetPayloadInspector.Inspect(
                _testRoot,
                AssetPackagingMode.XEnginePak,
                [expected]));

        Assert.Contains("unexpected file", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssetBundleInspectionAcceptsExactCollectedClosure()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        CreateAssetBundlePackage("default", [first, second]);

        AndroidPhysicalAssetPayload inspection = AndroidAssetPayloadInspector.Inspect(
            _testRoot,
            AssetPackagingMode.AssetBundle,
            [second, first]);

        Assert.True(inspection.IsExactClosure);
        Assert.Equal("exact-assetbundle-closure", inspection.Verification);
        Assert.Equal(2, inspection.Assets.Count);
        Assert.Equal(["Content/default/data.bundle"], inspection.Containers);
        Assert.Equal(3, inspection.Assets[first].UncompressedBytes);
        Assert.Null(inspection.Assets[first].CompressedBytes);
    }

    [Fact]
    public void AssetBundleInspectionExcludesEmbeddedBuiltInsFromPhysicalClosure()
    {
        Guid scene = Guid.NewGuid();
        Guid standardShader = BuiltInAssets.GuidFor(DefaultShader.Standard);
        CreateAssetBundlePackage("default", [scene]);

        AndroidPhysicalAssetPayload inspection = AndroidAssetPayloadInspector.Inspect(
            _testRoot,
            AssetPackagingMode.AssetBundle,
            [scene, standardShader]);

        Assert.True(inspection.IsExactClosure);
        Assert.Single(inspection.Assets);
        Assert.Contains(scene, inspection.Assets.Keys);
        Assert.DoesNotContain(standardShader, inspection.Assets.Keys);
    }

    [Fact]
    public void AssetBundleInspectionRejectsBundleContentsOutsideManifestIndex()
    {
        Guid manifestGuid = Guid.NewGuid();
        Guid physicalGuid = Guid.NewGuid();
        CreateAssetBundlePackage("default", [physicalGuid], [manifestGuid]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            AndroidAssetPayloadInspector.Inspect(
                _testRoot,
                AssetPackagingMode.AssetBundle,
                [manifestGuid]));

        Assert.Contains("does not match manifest index", error.Message, StringComparison.Ordinal);
        Assert.Contains(manifestGuid.ToString("N"), error.Message, StringComparison.Ordinal);
        Assert.Contains(physicalGuid.ToString("N"), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetBundleInspectionRejectsBundleWithTamperedFileMetadata()
    {
        Guid asset = Guid.NewGuid();
        string package = CreateAssetBundlePackage("default", [asset]);
        string manifestPath = Path.Combine(package, "default_1.0.0.bytes");
        AssetBundlePackageManifest manifest = PackageManifestSerializer.DeserializeFromFile(manifestPath)!;
        manifest.BundleList[0].FileHash = BundleHash.Zero;
        PackageManifestSerializer.SerializeToFile(manifest, manifestPath);
        File.WriteAllText(
            Path.Combine(package, PackageFileNames.HashFile("default", "1.0.0")),
            BundleHash.Compute(BundleHash.Zero.ToByteArray()).ToString());

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            AndroidAssetPayloadInspector.Inspect(
                _testRoot,
                AssetPackagingMode.AssetBundle,
                [asset]));

        Assert.Contains("metadata does not match", error.Message, StringComparison.Ordinal);
    }

    private static void WriteEntry(ZipArchive archive, Guid guid, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(guid + ".asset", CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        stream.Write(bytes);
    }

    private string CreateAssetBundlePackage(
        string packageName,
        IReadOnlyList<Guid> bundleAssets,
        IReadOnlyList<Guid>? manifestAssets = null)
    {
        string package = Path.Combine(_testRoot, "Content", packageName);
        Directory.CreateDirectory(package);
        string bundlePath = Path.Combine(package, "data.bundle");
        var entries = new List<KeyValuePair<AssetBundleEntry, byte[]>>();
        for (int i = 0; i < bundleAssets.Count; i++)
        {
            entries.Add(new KeyValuePair<AssetBundleEntry, byte[]>(
                new AssetBundleEntry(bundleAssets[i], bundleAssets[i].ToString("N"), string.Empty, 0, 3),
                [1, 2, 3]));
        }
        AssetBundleWriter.Write(bundlePath, entries, new AssetBundleBuildOptions
        {
            Platform = AssetBundlePlatform.Android,
            Compression = BundleCompressionMode.Lz4Block,
        });

        BundleHash bundleGuid;
        using (var bundle = AssetBundle.LoadFromFile(bundlePath))
            bundleGuid = bundle.BundleHash;
        var bundleInfo = new BundleInfo
        {
            BundleName = "data",
            FileName = "data.bundle",
            BundleGuid = bundleGuid,
            FileHash = BundleHash.ComputeFile(bundlePath),
            FileCrc = Crc32Algorithm.ComputeFile(bundlePath),
            FileSize = new FileInfo(bundlePath).Length,
        };
        var manifest = new AssetBundlePackageManifest
        {
            PackageName = packageName,
            PackageVersion = "1.0.0",
            BuildTarget = AssetBundlePlatform.Android,
        };
        manifest.BundleList.Add(bundleInfo);
        IReadOnlyList<Guid> indexedAssets = manifestAssets ?? bundleAssets;
        for (int i = 0; i < indexedAssets.Count; i++)
        {
            manifest.AssetList.Add(new ManifestAsset
            {
                AssetGuid = indexedAssets[i],
                BundleId = 0,
            });
        }
        PackageManifestSerializer.SerializeToFile(
            manifest,
            Path.Combine(package, PackageFileNames.ManifestFile(packageName, manifest.PackageVersion)));
        File.WriteAllText(Path.Combine(package, PackageFileNames.VersionFile(packageName)), manifest.PackageVersion);
        File.WriteAllText(
            Path.Combine(package, PackageFileNames.HashFile(packageName, manifest.PackageVersion)),
            BundleHash.Compute(bundleInfo.FileHash.ToByteArray()).ToString());
        return package;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, recursive: true);
        }
        catch { }
    }
}
