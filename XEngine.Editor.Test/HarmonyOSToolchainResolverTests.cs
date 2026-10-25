// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using XEngine.Editor.Build.HarmonyOS;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSToolchainResolverTests : IDisposable
{
    private static readonly string[] s_requiredPackageIds =
    [
        "OpenHarmony.NET.Runtime.NativeAot.arm64-v8a",
        "OpenHarmony.NET.Runtime.NativeAot.x86_64",
        "OpenHarmony.NET.PublishAotCross",
        "OpenHarmony.NDK.Bindings",
        "OpenHarmony.NET.Player.Sdk",
        "OpenHarmony.NET.HapTooling",
    ];

    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineHarmonyOSToolchainResolverTests",
        Guid.NewGuid().ToString("N"));

    private string ProjectRoot => Path.Combine(_testRoot, "Project");

    [Fact]
    public async Task ResolveAsync_UsesLockedBundleAndConfiguredSdkRoot()
    {
        ImportedBundleFixture bundle = await CreateImportedBundleAsync();
        string sdkRoot = CreateOfficialSdkFixture("command-line", 13);

        HarmonyOSToolchainSnapshot snapshot = await new HarmonyOSToolchainResolver().ResolveAsync(
            new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = sdkRoot,
                TargetApi = 13,
                TargetAbi = "arm64-v8a",
            });

        Assert.Equal(bundle.Lock.Fingerprint, snapshot.BundleFingerprint);
        Assert.Equal(bundle.Lock.ReleaseId, snapshot.ReleaseId);
        Assert.Equal(13, snapshot.TargetApi);
        Assert.Equal("arm64-v8a", snapshot.TargetAbi);
        Assert.Equal("CommandLine", snapshot.SdkRootSource);
        Assert.DoesNotContain(sdkRoot, snapshot.NormalizedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_RejectsMissingRequiredBundleCapability()
    {
        await CreateImportedBundleAsync(["playerPublish"]);

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSToolchainResolver().ResolveAsync(new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = CreateOfficialSdkFixture("capability", 13),
                TargetApi = 13,
                TargetAbi = "arm64-v8a",
                RequiredCapabilities = [HarmonyOSBundleCapability.StageProject],
            }));

        Assert.Equal(HarmonyOSBundleErrorCode.BundleCapabilityMissing, error.Code);
    }

    [Fact]
    public async Task ResolveAsync_AcceptsDeclaredRequiredBundleCapabilities()
    {
        await CreateImportedBundleAsync(["playerPublish", "stageProject", "hapBuild", "hapVerify"]);

        HarmonyOSToolchainSnapshot snapshot = await new HarmonyOSToolchainResolver().ResolveAsync(
            new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = CreateOfficialSdkFixture("declared-capabilities", 13),
                TargetApi = 13,
                TargetAbi = "arm64-v8a",
                RequiredCapabilities =
                [
                    HarmonyOSBundleCapability.PlayerPublish,
                    HarmonyOSBundleCapability.StageProject,
                    HarmonyOSBundleCapability.HapBuild,
                    HarmonyOSBundleCapability.HapVerify,
                ],
            });

        Assert.Equal(13, snapshot.TargetApi);
    }

    [Fact]
    public async Task ResolveHdcAsync_ReturnsConfiguredSdkToolWithoutLeakingItsRootIntoSnapshot()
    {
        await CreateImportedBundleAsync(["hdcDeploy"]);
        string sdkRoot = CreateOfficialSdkFixture("hdc", 13, includeHdc: true);

        HarmonyOSHdcToolchainResolution resolution = await new HarmonyOSToolchainResolver().ResolveHdcAsync(
            new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = sdkRoot,
                TargetApi = 13,
                TargetAbi = "x86_64",
                RequiredCapabilities = [HarmonyOSBundleCapability.HdcDeploy],
            });

        Assert.Equal(Path.Combine(sdkRoot, "13", "toolchains", "hdc.exe"), resolution.HdcExecutablePath);
        Assert.Equal("13", resolution.Snapshot.SdkFolderName);
        Assert.DoesNotContain(sdkRoot, resolution.Snapshot.NormalizedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveHdcAsync_RejectsSdkWithoutHdcUsingStableCode()
    {
        await CreateImportedBundleAsync(["hdcDeploy"]);

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSToolchainResolver().ResolveHdcAsync(new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = CreateOfficialSdkFixture("missing-hdc", 13),
                TargetApi = 13,
                TargetAbi = "x86_64",
                RequiredCapabilities = [HarmonyOSBundleCapability.HdcDeploy],
            }));

        Assert.Equal(HarmonyOSBundleErrorCode.HdcMissing, error.Code);
    }

    [Fact]
    public async Task ResolveAsync_RejectsDuplicateDeclaredBundleCapability()
    {
        await CreateImportedBundleAsync(["playerPublish", "playerPublish"]);

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSToolchainResolver().ResolveAsync(new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = CreateOfficialSdkFixture("duplicate-capability", 13),
                TargetApi = 13,
                TargetAbi = "arm64-v8a",
            }));

        Assert.Equal(HarmonyOSBundleErrorCode.ManifestInvalid, error.Code);
    }

    [Fact]
    public async Task ResolveAsync_RejectsUnknownDeclaredBundleCapability()
    {
        await CreateImportedBundleAsync(["playerPublish", "untrustedCapability"]);

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSToolchainResolver().ResolveAsync(new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = CreateOfficialSdkFixture("unknown-capability", 13),
                TargetApi = 13,
                TargetAbi = "arm64-v8a",
            }));

        Assert.Equal(HarmonyOSBundleErrorCode.ManifestInvalid, error.Code);
    }

    [Fact]
    public async Task ResolveAsync_RejectsApi25WithStableCode()
    {
        await CreateImportedBundleAsync();

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSToolchainResolver().ResolveAsync(new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = CreateOfficialSdkFixture("api25", 13),
                TargetApi = 25,
                TargetAbi = "arm64-v8a",
            }));

        Assert.Equal(HarmonyOSBundleErrorCode.ApiExcluded, error.Code);
    }

    [Fact]
    public async Task ResolveAsync_RejectsExplicitInvalidRootInsteadOfFallingBack()
    {
        await CreateImportedBundleAsync();

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSToolchainResolver().ResolveAsync(new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = Path.Combine(_testRoot, "missing-sdk"),
                UserPreferenceSdkRoot = CreateOfficialSdkFixture("preference", 13),
                EnvironmentSdkRoot = CreateOfficialSdkFixture("environment", 13),
                TargetApi = 13,
                TargetAbi = "arm64-v8a",
            }));

        Assert.Equal(HarmonyOSBundleErrorCode.SdkRootInvalid, error.Code);
    }

    [Fact]
    public async Task ResolveAsync_UsesPreferenceBeforeEnvironment()
    {
        await CreateImportedBundleAsync();

        HarmonyOSToolchainSnapshot snapshot = await new HarmonyOSToolchainResolver().ResolveAsync(
            new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                UserPreferenceSdkRoot = CreateOfficialSdkFixture("preference", 13),
                EnvironmentSdkRoot = CreateOfficialSdkFixture("environment", 13),
                TargetApi = 13,
                TargetAbi = "x86_64",
            });

        Assert.Equal("EditorPreference", snapshot.SdkRootSource);
        Assert.Equal("x86_64", snapshot.TargetAbi);
    }

    [Fact]
    public async Task ResolveAsync_RejectsAbsentSdkRoot()
    {
        await CreateImportedBundleAsync();

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSToolchainResolver().ResolveAsync(new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                TargetApi = 13,
                TargetAbi = "arm64-v8a",
            }));

        Assert.Equal(HarmonyOSBundleErrorCode.SdkRootMissing, error.Code);
    }

    [Fact]
    public async Task ResolveAsync_RejectsCatalogUnavailableApi()
    {
        await CreateImportedBundleAsync();

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSToolchainResolver().ResolveAsync(new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = CreateOfficialSdkFixture("unavailable", 13),
                TargetApi = 16,
                TargetAbi = "arm64-v8a",
            }));

        Assert.Equal(HarmonyOSBundleErrorCode.ApiUnavailable, error.Code);
    }

    [Fact]
    public async Task ResolveAsync_RejectsApiMissingFromOfficialSdk()
    {
        await CreateImportedBundleAsync();

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSToolchainResolver().ResolveAsync(new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = CreateOfficialSdkFixture("missing-api", 14),
                TargetApi = 13,
                TargetAbi = "arm64-v8a",
            }));

        Assert.Equal(HarmonyOSBundleErrorCode.SdkApiMissing, error.Code);
    }

    [Fact]
    public async Task ResolveAsync_RejectsUnsupportedAbi()
    {
        await CreateImportedBundleAsync();

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSToolchainResolver().ResolveAsync(new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = CreateOfficialSdkFixture("unsupported-abi", 13),
                TargetApi = 13,
                TargetAbi = "armeabi-v7a",
            }));

        Assert.Equal(HarmonyOSBundleErrorCode.AbiUnsupported, error.Code);
    }

    [Fact]
    public async Task ResolveAsync_RejectsTamperedImportedCatalog()
    {
        ImportedBundleFixture bundle = await CreateImportedBundleAsync();
        await File.WriteAllTextAsync(bundle.CatalogPath, "tampered", Encoding.UTF8);

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSToolchainResolver().ResolveAsync(new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = CreateOfficialSdkFixture("tampered", 13),
                TargetApi = 13,
                TargetAbi = "arm64-v8a",
            }));

        Assert.Equal(HarmonyOSBundleErrorCode.HashMismatch, error.Code);
    }

    [Fact]
    public async Task ResolveAsync_RejectsLockPointingOutsideProject()
    {
        await CreateImportedBundleAsync();
        string lockPath = Path.Combine(ProjectRoot, "ThirdParty", "OpenHarmonySdk", HarmonyOSSdkBundleLockFile.DefaultFileName);
        JsonObject lockJson = JsonNode.Parse(await File.ReadAllTextAsync(lockPath))!.AsObject();
        lockJson["BundleRelativePath"] = "ThirdParty/../outside";
        await File.WriteAllTextAsync(lockPath, lockJson.ToJsonString(), new UTF8Encoding(false));

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSToolchainResolver().ResolveAsync(new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = CreateOfficialSdkFixture("unsafe-lock", 13),
                TargetApi = 13,
                TargetAbi = "arm64-v8a",
            }));

        Assert.Equal(HarmonyOSBundleErrorCode.LockInvalid, error.Code);
    }

    [Fact]
    public async Task ResolveAsync_RejectsUndeclaredFeedPackageInjectedAfterImport()
    {
        ImportedBundleFixture bundle = await CreateImportedBundleAsync();
        await File.WriteAllTextAsync(
            Path.Combine(bundle.BundleRoot, "feed", "Injected.Package.1.0.0.nupkg"),
            "injected",
            new UTF8Encoding(false));

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSToolchainResolver().ResolveAsync(new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = CreateOfficialSdkFixture("injected-feed", 13),
                TargetApi = 13,
                TargetAbi = "arm64-v8a",
            }));

        Assert.Equal(HarmonyOSBundleErrorCode.LockInvalid, error.Code);
    }

    [Fact]
    public async Task ResolveAsync_RejectsManifestWhoseReleaseDoesNotMatchLock()
    {
        ImportedBundleFixture bundle = await CreateImportedBundleAsync();
        string manifestPath = Path.Combine(bundle.BundleRoot, "release-manifest.json");
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["ReleaseId"] = "other-release";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(), new UTF8Encoding(false));

        await HarmonyOSSdkBundleLockFile.WriteAsync(bundle.LockPath, new HarmonyOSSdkBundleLock
        {
            ReleaseId = bundle.Lock.ReleaseId,
            PackageVersion = bundle.Lock.PackageVersion,
            BundleRelativePath = bundle.Lock.BundleRelativePath,
            ArchiveSha256 = bundle.Lock.ArchiveSha256,
            ManifestSha256 = await ComputeSha256Async(manifestPath),
            SourceKind = bundle.Lock.SourceKind,
            SourceUri = bundle.Lock.SourceUri,
            Files = bundle.Lock.Files,
            Packages = bundle.Lock.Packages,
        });

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSToolchainResolver().ResolveAsync(new HarmonyOSToolchainResolveOptions
            {
                ProjectRoot = ProjectRoot,
                CommandLineSdkRoot = CreateOfficialSdkFixture("manifest-mismatch", 13),
                TargetApi = 13,
                TargetAbi = "arm64-v8a",
            }));

        Assert.Equal(HarmonyOSBundleErrorCode.LockInvalid, error.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private async Task<ImportedBundleFixture> CreateImportedBundleAsync(IReadOnlyList<string>? capabilities = null)
    {
        string releaseId = "10.0.10-ohos.2-preview.1";
        string relativeRoot = "ThirdParty/OpenHarmonySdk/" + releaseId;
        string bundleRoot = Path.Combine(ProjectRoot, "ThirdParty", "OpenHarmonySdk", releaseId);
        string catalogPath = Path.Combine(bundleRoot, "schemas", "sdk-catalog.json");
        string manifestPath = Path.Combine(bundleRoot, "release-manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        string feedRoot = Path.Combine(bundleRoot, "feed");
        Directory.CreateDirectory(feedRoot);
        Directory.CreateDirectory(Path.Combine(bundleRoot, "templates", "stage-player"));
        Directory.CreateDirectory(Path.Combine(bundleRoot, "licenses"));

        string catalog = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            supportedApis = new[] { 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 26 },
            buildApis = new[] { 13, 14, 15, 18, 20, 23, 26 },
            nativeUnavailableApis = new[] { 16, 17, 19, 21, 22, 24 },
            excludedApis = new[] { 25 },
            packages = new[]
            {
                CreateCatalogPackage(13, "13"),
                CreateCatalogPackage(14, "14"),
                CreateCatalogPackage(15, "15"),
                CreateCatalogPackage(18, "18"),
                CreateCatalogPackage(20, "20"),
                CreateCatalogPackage(23, "23"),
                CreateCatalogPackage(26, "26.0.0"),
            },
        });
        await File.WriteAllTextAsync(catalogPath, catalog, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(bundleRoot, "SHA256SUMS"), "fixture checksums", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(bundleRoot, "sbom.spdx.json"), "{\"spdxVersion\":\"SPDX-2.3\"}", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(bundleRoot, "templates", "stage-player", "template.json"), "{\"template\":true}", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(bundleRoot, "licenses", "LICENSE.txt"), "fixture license", new UTF8Encoding(false));

        var files = new List<HarmonyOSSdkBundleLockedFile>
        {
            await CreateLockedFileAsync(bundleRoot, "SHA256SUMS"),
            await CreateLockedFileAsync(bundleRoot, "sbom.spdx.json"),
            await CreateLockedFileAsync(bundleRoot, "schemas/sdk-catalog.json"),
            await CreateLockedFileAsync(bundleRoot, "templates/stage-player/template.json"),
            await CreateLockedFileAsync(bundleRoot, "licenses/LICENSE.txt"),
        };
        var packages = new List<HarmonyOSSdkBundleLockedPackage>(s_requiredPackageIds.Length);
        for (int i = 0; i < s_requiredPackageIds.Length; i++)
        {
            string id = s_requiredPackageIds[i];
            string relativePath = "feed/" + id + "." + releaseId + ".nupkg";
            string packagePath = Path.Combine(feedRoot, id + "." + releaseId + ".nupkg");
            await File.WriteAllTextAsync(packagePath, "package=" + id, new UTF8Encoding(false));
            HarmonyOSSdkBundleLockedFile file = await CreateLockedFileAsync(bundleRoot, relativePath);
            files.Add(file);
            packages.Add(new HarmonyOSSdkBundleLockedPackage
            {
                Id = id,
                Version = releaseId,
                RelativePath = relativePath,
                Sha256 = file.Sha256,
            });
        }

        var manifestFiles = new List<TestManifestFile>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            HarmonyOSSdkBundleLockedFile file = files[i];
            manifestFiles.Add(new TestManifestFile
            {
                Path = file.RelativePath,
                Length = file.Length,
                Sha256 = file.Sha256,
            });
        }
        var manifestPackages = new List<TestManifestPackage>(packages.Count);
        for (int i = 0; i < packages.Count; i++)
        {
            HarmonyOSSdkBundleLockedPackage package = packages[i];
            manifestPackages.Add(new TestManifestPackage
            {
                Id = package.Id,
                Version = package.Version,
                Path = package.RelativePath,
                Sha256 = package.Sha256,
            });
        }
        string manifestJson = JsonSerializer.Serialize(new TestManifest
        {
            SchemaVersion = 1,
            ReleaseId = releaseId,
            PackageVersion = releaseId,
            Capabilities = capabilities?.ToArray() ?? [],
            Files = manifestFiles,
            Packages = manifestPackages,
        });
        await File.WriteAllTextAsync(manifestPath, manifestJson, new UTF8Encoding(false));

        var lockValue = new HarmonyOSSdkBundleLock
        {
            ReleaseId = releaseId,
            PackageVersion = releaseId,
            BundleRelativePath = relativeRoot,
            ArchiveSha256 = new string('a', 64),
            ManifestSha256 = await ComputeSha256Async(manifestPath),
            SourceKind = "Fixture",
            Files = files,
            Packages = packages,
        };
        string lockPath = Path.Combine(ProjectRoot, "ThirdParty", "OpenHarmonySdk", HarmonyOSSdkBundleLockFile.DefaultFileName);
        await HarmonyOSSdkBundleLockFile.WriteAsync(lockPath, lockValue);
        HarmonyOSSdkBundleLock persisted = await HarmonyOSSdkBundleLockFile.ReadAsync(lockPath);
        return new ImportedBundleFixture(persisted, catalogPath, bundleRoot, lockPath);
    }

    private string CreateOfficialSdkFixture(string name, int api, bool includeHdc = false)
    {
        string sdkRoot = Path.Combine(_testRoot, "OfficialSdk", name);
        string folder = api == 26 ? "26.0.0" : api.ToString();
        string nativeRoot = Path.Combine(sdkRoot, folder, "native");
        Directory.CreateDirectory(nativeRoot);
        File.WriteAllText(
            Path.Combine(nativeRoot, "oh-uni-package.json"),
            CreateNativePackageManifest(api),
            new UTF8Encoding(false));
        if (includeHdc)
        {
            string toolchains = Path.Combine(sdkRoot, folder, "toolchains");
            Directory.CreateDirectory(toolchains);
            File.WriteAllText(Path.Combine(toolchains, "hdc.exe"), "fixture", new UTF8Encoding(false));
        }
        return sdkRoot;
    }

    private static object CreateCatalogPackage(int api, string folder) => new
    {
        apiLevel = api,
        sdkFolderName = folder,
        version = "test-" + api,
        releaseType = "Release",
        manifestSha256 = ComputeTextSha256(CreateNativePackageManifest(api)),
    };

    private static string CreateNativePackageManifest(int api) =>
        // The official OpenHarmony oh-uni-package.json emits apiVersion as a JSON string
        // (e.g. "23"), not a bare number. The resolver must accept this form.
        "{\"apiVersion\":\"" + api + "\",\"version\":\"test-" + api + "\",\"releaseType\":\"Release\"}";

    private static async Task<HarmonyOSSdkBundleLockedFile> CreateLockedFileAsync(string root, string relativePath)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        FileInfo info = new(path);
        return new HarmonyOSSdkBundleLockedFile
        {
            RelativePath = relativePath,
            Length = info.Length,
            Sha256 = await ComputeSha256Async(path),
        };
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeTextSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(value))).ToLowerInvariant();

    private sealed record ImportedBundleFixture(HarmonyOSSdkBundleLock Lock, string CatalogPath, string BundleRoot, string LockPath);

    private sealed class TestManifest
    {
        public int SchemaVersion { get; init; }
        public string ReleaseId { get; init; } = string.Empty;
        public string PackageVersion { get; init; } = string.Empty;
        public string[] Capabilities { get; init; } = [];
        public List<TestManifestFile> Files { get; init; } = [];
        public List<TestManifestPackage> Packages { get; init; } = [];
    }

    private sealed class TestManifestFile
    {
        public string Path { get; init; } = string.Empty;
        public long Length { get; init; }
        public string Sha256 { get; init; } = string.Empty;
    }

    private sealed class TestManifestPackage
    {
        public string Id { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
    }
}
