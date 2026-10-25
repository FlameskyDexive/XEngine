// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

using XEngine.Editor.Build.HarmonyOS;
using XEngine.Runtime;
using XEngine.Runtime.AssetBundles;
using XEngine.Runtime.Resources;

using AssetBundlePackageManifest = XEngine.Runtime.AssetBundles.PackageManifest;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSAssetPayloadInspectorTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineHarmonyOSAssetPayloadTests",
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
        WriteHashManifest();

        HarmonyOSPhysicalAssetPayload inspection = HarmonyOSAssetPayloadInspector.Inspect(
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
        WriteHashManifest();

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            HarmonyOSAssetPayloadInspector.Inspect(
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
        WriteHashManifest();

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            HarmonyOSAssetPayloadInspector.Inspect(
                _testRoot,
                AssetPackagingMode.XEnginePak,
                [expected]));

        Assert.Contains("unexpected file", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssetBundleInspectionAcceptsExactHarmonyOSClosure()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        CreateAssetBundlePackage("default", [first, second]);

        HarmonyOSPhysicalAssetPayload inspection = HarmonyOSAssetPayloadInspector.Inspect(
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

        HarmonyOSPhysicalAssetPayload inspection = HarmonyOSAssetPayloadInspector.Inspect(
            _testRoot,
            AssetPackagingMode.AssetBundle,
            [scene, standardShader]);

        Assert.True(inspection.IsExactClosure);
        Assert.Single(inspection.Assets);
        Assert.Contains(scene, inspection.Assets.Keys);
        Assert.DoesNotContain(standardShader, inspection.Assets.Keys);
    }

    [Fact]
    public void AssetBundleInspectionRejectsAndroidTarget()
    {
        Guid asset = Guid.NewGuid();
        CreateAssetBundlePackage("default", [asset], AssetBundlePlatform.Android);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            HarmonyOSAssetPayloadInspector.Inspect(
                _testRoot,
                AssetPackagingMode.AssetBundle,
                [asset]));

        Assert.Contains("not HarmonyOS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssetBundleInspectionRejectsBundleContentsOutsideManifestIndex()
    {
        Guid manifestGuid = Guid.NewGuid();
        Guid physicalGuid = Guid.NewGuid();
        CreateAssetBundlePackage("default", [physicalGuid], AssetBundlePlatform.HarmonyOS, [manifestGuid]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            HarmonyOSAssetPayloadInspector.Inspect(
                _testRoot,
                AssetPackagingMode.AssetBundle,
                [manifestGuid]));

        Assert.Contains("does not match manifest index", error.Message, StringComparison.Ordinal);
        Assert.Contains(manifestGuid.ToString("N"), error.Message, StringComparison.Ordinal);
        Assert.Contains(physicalGuid.ToString("N"), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectionRejectsSourceShaderAndUnexpectedNativeLibrary()
    {
        Guid expected = Guid.NewGuid();
        string content = Path.Combine(_testRoot, "Content");
        Directory.CreateDirectory(content);
        using (ZipArchive archive = ZipFile.Open(Path.Combine(content, "data_0.xenginepak"), ZipArchiveMode.Create))
            WriteEntry(archive, expected, [1]);
        string shaders = Path.Combine(_testRoot, "Shaders");
        Directory.CreateDirectory(shaders);
        File.WriteAllText(Path.Combine(shaders, "bad.hlsl"), "float4 main() : SV_Target { return 1; }");
        string libraries = Path.Combine(_testRoot, "libs", "arm64-v8a");
        Directory.CreateDirectory(libraries);
        File.WriteAllBytes(Path.Combine(libraries, "libforeign.so"), [0]);
        WriteHashManifest();

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            HarmonyOSAssetPayloadInspector.Inspect(
                _testRoot,
                AssetPackagingMode.XEnginePak,
                [expected],
                []));

        Assert.Contains("source-only shader", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectionRejectsHashManifestThatIsNotOrdinallySorted()
    {
        Guid expected = Guid.NewGuid();
        string content = Path.Combine(_testRoot, "Content");
        Directory.CreateDirectory(content);
        using (ZipArchive archive = ZipFile.Open(Path.Combine(content, "data_0.xenginepak"), ZipArchiveMode.Create))
            WriteEntry(archive, expected, [1]);
        File.WriteAllBytes(Path.Combine(content, "asset_manifest.bin"), [7]);
        WriteHashManifest(descending: true);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            HarmonyOSAssetPayloadInspector.Inspect(
                _testRoot,
                AssetPackagingMode.XEnginePak,
                [expected]));

        Assert.Contains("ordinally sorted", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectionAcceptsExpectedPrecompiledSpirVClosure()
    {
        Guid expected = Guid.NewGuid();
        string content = Path.Combine(_testRoot, "Content");
        Directory.CreateDirectory(content);
        using (ZipArchive archive = ZipFile.Open(Path.Combine(content, "data_0.xenginepak"), ZipArchiveMode.Create))
            WriteEntry(archive, expected, [1]);
        string shaders = Path.Combine(_testRoot, "Shaders");
        Directory.CreateDirectory(shaders);
        File.WriteAllBytes(Path.Combine(shaders, "triangle.spv"), [0x03, 0x02, 0x23, 0x07]);
        WriteHashManifest();

        HarmonyOSPhysicalAssetPayload inspection = HarmonyOSAssetPayloadInspector.Inspect(
            _testRoot,
            AssetPackagingMode.XEnginePak,
            [expected],
            ["triangle.spv"]);

        Assert.True(inspection.IsExactClosure);
    }

    [Fact]
    public void InspectionRejectsUnownedNativeLibrary()
    {
        Guid expected = Guid.NewGuid();
        string content = Path.Combine(_testRoot, "Content");
        Directory.CreateDirectory(content);
        using (ZipArchive archive = ZipFile.Open(Path.Combine(content, "data_0.xenginepak"), ZipArchiveMode.Create))
            WriteEntry(archive, expected, [1]);
        string libraries = Path.Combine(_testRoot, "libs", "arm64-v8a");
        Directory.CreateDirectory(libraries);
        File.WriteAllBytes(Path.Combine(libraries, "libforeign.so"), [0]);
        WriteHashManifest();

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            HarmonyOSAssetPayloadInspector.Inspect(
                _testRoot,
                AssetPackagingMode.XEnginePak,
                [expected]));

        Assert.Contains("unowned native binary", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalizeAsync_WritesSortedCompleteManifestAndVerifiesClosure()
    {
        Guid expected = Guid.NewGuid();
        string content = Path.Combine(_testRoot, "Content");
        string settings = Path.Combine(_testRoot, "Settings");
        Directory.CreateDirectory(content);
        Directory.CreateDirectory(settings);
        await File.WriteAllBytesAsync(Path.Combine(content, expected + ".asset"), [1, 2, 3]);
        string settingsPath = Path.Combine(settings, "General.yaml");
        await File.WriteAllTextAsync(settingsPath, "product: Smoke");

        HarmonyOSFinalizedAssetPayload result = await HarmonyOSAssetPayloadFinalizer.FinalizeAsync(
            _testRoot,
            AssetPackagingMode.LooseFiles,
            [expected]);

        Assert.True(result.Inspection.IsExactClosure);
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestPath));
        string[] paths = manifest.RootElement.GetProperty("files")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("path").GetString()!)
            .ToArray();
        Assert.Equal(paths.OrderBy(path => path, StringComparer.Ordinal), paths);
        Assert.Contains("Content/" + expected + ".asset", paths);
        Assert.Contains("Settings/General.yaml", paths);

        await File.AppendAllTextAsync(settingsPath, "\nchanged: true");
        Assert.Throws<InvalidDataException>(() => HarmonyOSAssetPayloadInspector.Inspect(
            _testRoot,
            AssetPackagingMode.LooseFiles,
            [expected]));
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
        AssetBundlePlatform platform = AssetBundlePlatform.HarmonyOS,
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
            Platform = platform,
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
            BuildTarget = platform,
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
        WriteHashManifest();
        return package;
    }

    private void WriteHashManifest(bool descending = false)
    {
        string manifestPath = Path.Combine(_testRoot, HarmonyOSAssetPayloadInspector.HashManifestFileName);
        string[] discoveredFiles = Directory.GetFiles(_testRoot, "*", SearchOption.AllDirectories);
        var fileList = new List<string>(discoveredFiles.Length);
        for (int i = 0; i < discoveredFiles.Length; i++)
        {
            if (!string.Equals(discoveredFiles[i], manifestPath, StringComparison.OrdinalIgnoreCase))
                fileList.Add(discoveredFiles[i]);
        }
        string[] files = [.. fileList];
        Array.Sort(files, static (left, right) => StringComparer.Ordinal.Compare(
            left.Replace('\\', '/'), right.Replace('\\', '/')));
        if (descending)
            Array.Reverse(files);

        var entries = new List<object>(files.Length);
        for (int i = 0; i < files.Length; i++)
        {
            FileInfo file = new(files[i]);
            entries.Add(new
            {
                path = Path.GetRelativePath(_testRoot, files[i]).Replace('\\', '/'),
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(files[i]))).ToLowerInvariant(),
                bytes = file.Length,
            });
        }
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { schemaVersion = 1, files = entries }));
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
