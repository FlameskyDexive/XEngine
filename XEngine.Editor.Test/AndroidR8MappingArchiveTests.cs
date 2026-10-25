// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

using XEngine.Editor.Build.Android;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class AndroidR8MappingArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XEngineAndroidR8MappingTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ArchivesFinalMappingAndVerifiesAabMetadata()
    {
        AndroidPlayerProjectLayout layout = CreateLayout(AndroidOutputMode.Aab);
        string mappingPath = Path.Combine(
            _root,
            "bin",
            "Release",
            "net10.0-android",
            "android-arm64",
            "mapping.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(mappingPath)!);
        byte[] mapping = "com.xengine.Generated -\u003e a:\n    void MainActivity() -\u003e a\n"u8.ToArray();
        File.WriteAllBytes(mappingPath, mapping);

        AndroidR8MappingSnapshot snapshot = AndroidR8MappingArchive.Create(layout, "Release");
        Assert.Equal(mapping.Length, snapshot.Bytes);
        Assert.Equal(mapping, File.ReadAllBytes(snapshot.ArchivePath));

        string bundlePath = Path.Combine(_root, "game.aab");
        using (ZipArchive bundle = ZipFile.Open(bundlePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = bundle.CreateEntry(
                "BUNDLE-METADATA/com.android.tools.build.obfuscation/proguard.map");
            using Stream output = entry.Open();
            output.Write(mapping);
        }

        AndroidR8MappingArchive.VerifyAndWrite(snapshot, layout, bundlePath);
        string reportPath = Path.Combine(_root, "verification", AndroidR8MappingArchive.ManifestFileName);
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.Equal(1, report.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(report.RootElement.GetProperty("bundledInAab").GetBoolean());
        Assert.Equal(snapshot.Sha256, report.RootElement.GetProperty("sha256").GetString());
    }

    [Fact]
    public void RejectsMissingOrEmptyReleaseMapping()
    {
        AndroidPlayerProjectLayout layout = CreateLayout(AndroidOutputMode.Apk);
        Assert.Throws<FileNotFoundException>(() => AndroidR8MappingArchive.Create(layout, "Release"));

        string mappingPath = Path.Combine(
            _root,
            "bin",
            "Release",
            "net10.0-android",
            "android-arm64",
            "mapping.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(mappingPath)!);
        File.WriteAllText(mappingPath, " \r\n");
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            AndroidR8MappingArchive.Create(layout, "Release"));
        Assert.Contains("empty", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AabContainerMayCompressSeekableAssetsButDeviceApkMayNot()
    {
        Directory.CreateDirectory(_root);
        string manifestPath = Path.Combine(_root, "android-assets.json");
        byte[] payload = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(payload));
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                assetStrippingReport = "Reports/report.json",
                assets = new[]
                {
                    new { path = "Content/seek.bytes", bytes = payload.LongLength, sha256 = hash, requiresSeek = true },
                },
            }));

        string report = "{\"schemaVersion\":1,\"packagingMode\":\"XEnginePak\",\"physicalPayloadVerified\":true}";
        AndroidPlayerProjectLayout aabLayout = CreateLayout(AndroidOutputMode.Aab) with
        {
            IncludesAssetPayload = true,
            AssetManifestPath = manifestPath,
        };
        string aabPath = Path.Combine(_root, "payload.aab");
        using (ZipArchive aab = ZipFile.Open(aabPath, ZipArchiveMode.Create))
        {
            WriteEntry(aab, "base/assets/xengine/Content/seek.bytes", payload, CompressionLevel.Optimal);
            WriteEntry(aab, "base/assets/xengine/android-assets.json", "{}"u8.ToArray(), CompressionLevel.Optimal);
            WriteEntry(aab, "base/assets/xengine/Reports/report.json", System.Text.Encoding.UTF8.GetBytes(report), CompressionLevel.Optimal);
        }
        using (ZipArchive aab = ZipFile.OpenRead(aabPath))
            AndroidPackageBuilder.VerifyAssetPayload(aab, aabLayout);

        AndroidPlayerProjectLayout apkLayout = aabLayout with { OutputMode = AndroidOutputMode.Apk };
        string apkPath = Path.Combine(_root, "payload.apk");
        using (ZipArchive apk = ZipFile.Open(apkPath, ZipArchiveMode.Create))
        {
            WriteEntry(apk, "assets/xengine/Content/seek.bytes", payload, CompressionLevel.Optimal);
            WriteEntry(apk, "assets/xengine/android-assets.json", "{}"u8.ToArray(), CompressionLevel.Optimal);
            WriteEntry(apk, "assets/xengine/Reports/report.json", System.Text.Encoding.UTF8.GetBytes(report), CompressionLevel.Optimal);
        }
        using (ZipArchive apk = ZipFile.OpenRead(apkPath))
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                AndroidPackageBuilder.VerifyAssetPayload(apk, apkLayout, AndroidOutputMode.Apk));
            Assert.Contains("compressed", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private AndroidPlayerProjectLayout CreateLayout(AndroidOutputMode outputMode) => new(
        _root,
        Path.Combine(_root, "Game.Android.csproj"),
        Path.Combine(_root, "Properties", "AndroidManifest.xml"),
        Path.Combine(_root, "xengine-android-project.json"),
        null,
        "Game.Android",
        24,
        ["arm64-v8a"],
        ["android-arm64"],
        ["XEngine.Runtime"],
        [],
        outputMode,
        false,
        false,
        null,
        XEngine.Runtime.AssetPackagingMode.XEnginePak,
        AndroidPackageSizeBudget.Disabled);

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        byte[] contents,
        CompressionLevel compression)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, compression);
        using Stream output = entry.Open();
        output.Write(contents);
    }
}
