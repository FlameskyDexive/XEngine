// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build.HarmonyOS;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSSdkBundleImporterTests : IDisposable
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
        "XEngineHarmonyOSSdkBundleImporterTests",
        Guid.NewGuid().ToString("N"));

    private string ProjectRoot => Path.Combine(_testRoot, "Project");

    [Fact]
    public async Task ImportAsync_ValidBundlePublishesImmutableDirectoryAndLock()
    {
        BundleFixture fixture = await CreateBundleAsync("10.0.10-ohos.2-preview.1");

        HarmonyOSSdkBundleImportResult result = await new HarmonyOSSdkBundleImporter().ImportAsync(
            new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath));

        Assert.False(result.Reused);
        Assert.True(File.Exists(Path.Combine(result.ImportedRoot, "release-manifest.json")));
        Assert.True(File.Exists(result.LockPath));
        Assert.Equal(fixture.ArchiveSha256, result.Lock.ArchiveSha256);
        Assert.Equal("ThirdParty/OpenHarmonySdk/10.0.10-ohos.2-preview.1", result.Lock.BundleRelativePath);
        Assert.Equal(6, result.Lock.Packages.Count);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("feed/../../escape.nupkg")]
    public async Task ImportAsync_RejectsZipTraversalWithoutPublishing(string entryName)
    {
        string archive = await CreateTraversalBundleAsync(entryName);

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, archive)));

        Assert.Equal(HarmonyOSBundleErrorCode.ZipTraversal, error.Code);
        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_RejectsMissingRequiredPackageWithoutPublishing()
    {
        BundleFixture fixture = await CreateBundleAsync(
            "10.0.10-ohos.2-preview.1",
            specification => specification.OmittedPackageId = s_requiredPackageIds[0]);

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath)));

        Assert.Equal(HarmonyOSBundleErrorCode.PackageContractMismatch, error.Code);
        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_RejectsPackageVersionMismatchWithoutPublishing()
    {
        BundleFixture fixture = await CreateBundleAsync(
            "10.0.10-ohos.2-preview.1",
            specification => specification.PackageVersionOverride = "10.0.10-ohos.2-preview.2");

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath)));

        Assert.Equal(HarmonyOSBundleErrorCode.PackageContractMismatch, error.Code);
        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_RejectsWrongDeclaredFileHashWithoutPublishing()
    {
        BundleFixture fixture = await CreateBundleAsync(
            "10.0.10-ohos.2-preview.1",
            specification => specification.CorruptDeclaredFileHash = true);

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath)));

        Assert.Equal(HarmonyOSBundleErrorCode.HashMismatch, error.Code);
        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_RejectsDuplicateZipEntryWithoutPublishing()
    {
        BundleFixture fixture = await CreateBundleAsync(
            "10.0.10-ohos.2-preview.1",
            specification => specification.DuplicateEntryPath = "schemas/sdk-catalog.json");

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath)));

        Assert.Equal(HarmonyOSBundleErrorCode.InvalidArchive, error.Code);
        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_RejectsUndeclaredZipEntryWithoutPublishing()
    {
        BundleFixture fixture = await CreateBundleAsync(
            "10.0.10-ohos.2-preview.1",
            specification => specification.UndeclaredEntryPath = "feed/not-declared.nupkg");

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath)));

        Assert.Equal(HarmonyOSBundleErrorCode.ManifestInvalid, error.Code);
        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_RejectsUnknownDeclaredBundleCapabilityWithoutPublishing()
    {
        BundleFixture fixture = await CreateBundleAsync(
            "10.0.10-ohos.2-preview.1",
            specification => specification.Capabilities = ["playerPublish", "untrustedCapability"]);

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath)));

        Assert.Equal(HarmonyOSBundleErrorCode.ManifestInvalid, error.Code);
        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_RejectsCorruptZipWithoutPublishing()
    {
        Directory.CreateDirectory(_testRoot);
        string archive = Path.Combine(_testRoot, "corrupt.zip");
        await File.WriteAllTextAsync(archive, "not a ZIP", Encoding.UTF8);

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, archive)));

        Assert.Equal(HarmonyOSBundleErrorCode.InvalidArchive, error.Code);
        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_CanceledBeforeValidationLeavesNoArtifacts()
    {
        BundleFixture fixture = await CreateBundleAsync("10.0.10-ohos.2-preview.1");
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => new HarmonyOSSdkBundleImporter().ImportAsync(
            new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath),
            cancellationToken: cancellationSource.Token));

        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_RejectsExistingReleaseWithDifferentArchiveBytes()
    {
        BundleFixture initial = await CreateBundleAsync("10.0.10-ohos.2-preview.1");
        await new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, initial.ArchivePath));

        BundleFixture conflicting = await CreateBundleAsync(
            "10.0.10-ohos.2-preview.1",
            specification => specification.CatalogMarker = "conflicting");

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, conflicting.ArchivePath)));

        Assert.Equal(HarmonyOSBundleErrorCode.ReleaseConflict, error.Code);
        Assert.True(File.Exists(Path.Combine(ProjectRoot, "ThirdParty", "OpenHarmonySdk", "10.0.10-ohos.2-preview.1", "release-manifest.json")));
    }

    [Fact]
    public async Task ImportAsync_ReusesVerifiedMatchingRelease()
    {
        BundleFixture fixture = await CreateBundleAsync("10.0.10-ohos.2-preview.1");
        var importer = new HarmonyOSSdkBundleImporter();

        HarmonyOSSdkBundleImportResult first = await importer.ImportAsync(
            new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath));
        HarmonyOSSdkBundleImportResult second = await importer.ImportAsync(
            new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath));

        Assert.False(first.Reused);
        Assert.True(second.Reused);
        Assert.Equal(first.Lock.Fingerprint, second.Lock.Fingerprint);
    }

    [Fact]
    public async Task ImportAsync_RejectsTamperedPublishedRelease()
    {
        BundleFixture fixture = await CreateBundleAsync("10.0.10-ohos.2-preview.1");
        var importer = new HarmonyOSSdkBundleImporter();
        HarmonyOSSdkBundleImportResult imported = await importer.ImportAsync(
            new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath));

        string catalogPath = Path.Combine(imported.ImportedRoot, "schemas", "sdk-catalog.json");
        await File.WriteAllTextAsync(catalogPath, "tampered", Encoding.UTF8);

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            importer.ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath)));

        Assert.Equal(HarmonyOSBundleErrorCode.ReleaseConflict, error.Code);
    }

    [Fact]
    public async Task ImportAsync_RejectsDeclaredEntryOutsideBundleContract()
    {
        BundleFixture fixture = await CreateBundleAsync(
            "10.0.10-ohos.2-preview.1",
            specification => specification.DeclaredExtraPath = "unexpected/extra.txt");

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath)));

        Assert.Equal(HarmonyOSBundleErrorCode.ManifestInvalid, error.Code);
        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_RejectsWindowsAmbiguousEntryName()
    {
        BundleFixture fixture = await CreateBundleAsync(
            "10.0.10-ohos.2-preview.1",
            specification => specification.DeclaredExtraPath = "schemas/CON.json");

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath)));

        Assert.Equal(HarmonyOSBundleErrorCode.ZipTraversal, error.Code);
        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_RejectsPackagePathThatDoesNotMatchItsIdAndVersion()
    {
        BundleFixture fixture = await CreateBundleAsync(
            "10.0.10-ohos.2-preview.1",
            specification => specification.FirstPackagePathOverride = "feed/OpenHarmony.NET.Player.Sdk.invalid.nupkg");

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath)));

        Assert.Equal(HarmonyOSBundleErrorCode.PackageContractMismatch, error.Code);
        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_RejectsNupkgWhoseNuspecIdentityDoesNotMatchManifest()
    {
        BundleFixture fixture = await CreateBundleAsync(
            "10.0.10-ohos.2-preview.1",
            specification => specification.FirstPackageNuspecIdOverride = "OpenHarmony.NET.Wrong.Package");

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath)));

        Assert.Equal(HarmonyOSBundleErrorCode.PackageContractMismatch, error.Code);
        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_RejectsNupkgWhoseNuspecDoesNotUsePackageMetadataStructure()
    {
        BundleFixture fixture = await CreateBundleAsync(
            "10.0.10-ohos.2-preview.1",
            specification => specification.FirstPackageNuspecRootOverride = "not-package");

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath)));

        Assert.Equal(HarmonyOSBundleErrorCode.PackageContractMismatch, error.Code);
        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_RejectsNupkgWhoseMetadataIsNotADirectPackageChild()
    {
        BundleFixture fixture = await CreateBundleAsync(
            "10.0.10-ohos.2-preview.1",
            specification => specification.FirstPackageNuspecNestedMetadata = true);

        HarmonyOSBundleException error = await Assert.ThrowsAsync<HarmonyOSBundleException>(() =>
            new HarmonyOSSdkBundleImporter().ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath)));

        Assert.Equal(HarmonyOSBundleErrorCode.PackageContractMismatch, error.Code);
        AssertNoPublishedArtifacts();
    }

    [Fact]
    public async Task ImportAsync_CanceledDuringExtractionCleansStagingAndPreservesExistingLock()
    {
        BundleFixture initial = await CreateBundleAsync("10.0.10-ohos.2-preview.1");
        var importer = new HarmonyOSSdkBundleImporter();
        await importer.ImportAsync(new HarmonyOSSdkBundleImportRequest(ProjectRoot, initial.ArchivePath));
        string bundleRoot = Path.Combine(ProjectRoot, "ThirdParty", "OpenHarmonySdk");
        string lockPath = Path.Combine(bundleRoot, HarmonyOSSdkBundleLockFile.DefaultFileName);
        byte[] originalLock = await File.ReadAllBytesAsync(lockPath);

        BundleFixture next = await CreateBundleAsync("10.0.10-ohos.2-preview.2");
        using var cancellationSource = new CancellationTokenSource();
        var progress = new CallbackProgress(snapshot =>
        {
            if (snapshot.Stage == "Extracting")
                cancellationSource.Cancel();
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => importer.ImportAsync(
            new HarmonyOSSdkBundleImportRequest(ProjectRoot, next.ArchivePath),
            progress,
            cancellationSource.Token));

        Assert.False(Directory.Exists(Path.Combine(bundleRoot, "10.0.10-ohos.2-preview.2")));
        Assert.Empty(Directory.GetDirectories(bundleRoot, ".*.staging"));
        Assert.Equal(originalLock, await File.ReadAllBytesAsync(lockPath));
    }

    [Fact]
    public async Task ImportAsync_ConcurrentSameReleasePublishesOnceAndReusesOnce()
    {
        BundleFixture fixture = await CreateBundleAsync("10.0.10-ohos.2-preview.1");
        var importer = new HarmonyOSSdkBundleImporter();
        using var progress = new BlockingExtractionProgress();
        Task<HarmonyOSSdkBundleImportResult> first = Task.Run(() => importer.ImportAsync(
            new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath),
            progress));

        try
        {
            Assert.True(progress.Entered.Wait(TimeSpan.FromSeconds(10)));
            Task<HarmonyOSSdkBundleImportResult> second = Task.Run(() => importer.ImportAsync(
                new HarmonyOSSdkBundleImportRequest(ProjectRoot, fixture.ArchivePath)));
            progress.Continue.Set();

            HarmonyOSSdkBundleImportResult[] results = await Task.WhenAll(first, second);
            int reused = 0;
            for (int i = 0; i < results.Length; i++)
            {
                if (results[i].Reused)
                    reused++;
            }
            Assert.Equal(1, reused);
        }
        finally
        {
            progress.Continue.Set();
            await first;
        }
    }

    [Fact]
    public async Task ImportAsync_ConcurrentReleasesUpdateSharedLockInAcquisitionOrder()
    {
        BundleFixture firstFixture = await CreateBundleAsync("10.0.10-ohos.2-preview.1");
        BundleFixture secondFixture = await CreateBundleAsync("10.0.10-ohos.2-preview.2");
        var importer = new HarmonyOSSdkBundleImporter();
        using var progress = new BlockingExtractionProgress();
        Task<HarmonyOSSdkBundleImportResult> first = Task.Run(() => importer.ImportAsync(
            new HarmonyOSSdkBundleImportRequest(ProjectRoot, firstFixture.ArchivePath),
            progress));

        try
        {
            Assert.True(progress.Entered.Wait(TimeSpan.FromSeconds(10)));
            Task<HarmonyOSSdkBundleImportResult> second = Task.Run(() => importer.ImportAsync(
                new HarmonyOSSdkBundleImportRequest(ProjectRoot, secondFixture.ArchivePath)));
            progress.Continue.Set();
            await Task.WhenAll(first, second);

            HarmonyOSSdkBundleLock current = await HarmonyOSSdkBundleLockFile.ReadAsync(
                Path.Combine(ProjectRoot, "ThirdParty", "OpenHarmonySdk", HarmonyOSSdkBundleLockFile.DefaultFileName));
            Assert.Equal("10.0.10-ohos.2-preview.2", current.ReleaseId);
        }
        finally
        {
            progress.Continue.Set();
            await first;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private void AssertNoPublishedArtifacts()
    {
        string bundleRoot = Path.Combine(ProjectRoot, "ThirdParty", "OpenHarmonySdk");
        string importLockDirectory = Path.Combine(ProjectRoot, "Library", "OpenHarmonySdk");
        Assert.False(Directory.Exists(ProjectRoot));
        Assert.False(Directory.Exists(bundleRoot));
        Assert.False(Directory.Exists(importLockDirectory));
    }

    private async Task<BundleFixture> CreateBundleAsync(string releaseId, Action<BundleSpecification>? configure = null)
    {
        Directory.CreateDirectory(_testRoot);
        var specification = new BundleSpecification();
        configure?.Invoke(specification);

        var payloads = new List<BundlePayload>
        {
            new("SHA256SUMS", Encoding.UTF8.GetBytes("fixture checksums\n")),
            new("sbom.spdx.json", Encoding.UTF8.GetBytes("{\"spdxVersion\":\"SPDX-2.3\"}")),
            new("schemas/sdk-catalog.json", Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"marker\":\"" + specification.CatalogMarker + "\"}")),
            new("templates/stage-player/template.json", Encoding.UTF8.GetBytes("{\"template\":\"stage-player\"}")),
            new("licenses/LICENSE.txt", Encoding.UTF8.GetBytes("fixture license\n")),
        };

        var packages = new List<BundlePackage>();
        for (int i = 0; i < s_requiredPackageIds.Length; i++)
        {
            string id = s_requiredPackageIds[i];
            if (string.Equals(id, specification.OmittedPackageId, StringComparison.Ordinal))
                continue;

            string path = i == 0 && specification.FirstPackagePathOverride != null
                ? specification.FirstPackagePathOverride
                : "feed/" + id + "." + releaseId + ".nupkg";
            string nuspecId = i == 0 && specification.FirstPackageNuspecIdOverride != null
                ? specification.FirstPackageNuspecIdOverride
                : id;
            byte[] bytes = CreateNupkg(
                nuspecId,
                releaseId,
                i == 0 ? specification.FirstPackageNuspecRootOverride : null,
                i == 0 && specification.FirstPackageNuspecNestedMetadata);
            payloads.Add(new BundlePayload(path, bytes));
            packages.Add(new BundlePackage
            {
                Id = id,
                Version = specification.PackageVersionOverride ?? releaseId,
                Path = path,
                Sha256 = ComputeSha256(bytes),
            });
        }
        if (specification.DeclaredExtraPath != null)
            payloads.Add(new BundlePayload(specification.DeclaredExtraPath, Encoding.UTF8.GetBytes("declared extra")));

        var files = new List<BundleFile>(payloads.Count);
        for (int i = 0; i < payloads.Count; i++)
        {
            BundlePayload payload = payloads[i];
            files.Add(new BundleFile
            {
                Path = payload.Path,
                Length = payload.Bytes.Length,
                Sha256 = ComputeSha256(payload.Bytes),
            });
        }
        if (specification.CorruptDeclaredFileHash)
            files[0].Sha256 = new string('0', 64);

        var manifest = new BundleManifest
        {
            SchemaVersion = 1,
            ReleaseId = releaseId,
            PackageVersion = releaseId,
            Capabilities = specification.Capabilities,
            Files = files,
            Packages = packages,
        };

        string archivePath = Path.Combine(_testRoot, Guid.NewGuid().ToString("N") + ".zip");
        await using (var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
        {
            for (int i = 0; i < payloads.Count; i++)
                await WriteEntryAsync(archive, payloads[i].Path, payloads[i].Bytes);

            if (!string.IsNullOrEmpty(specification.DuplicateEntryPath))
                await WriteEntryAsync(archive, specification.DuplicateEntryPath, Encoding.UTF8.GetBytes("duplicate"));
            if (!string.IsNullOrEmpty(specification.UndeclaredEntryPath))
                await WriteEntryAsync(archive, specification.UndeclaredEntryPath, Encoding.UTF8.GetBytes("undeclared"));

            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            await WriteEntryAsync(archive, "release-manifest.json", manifestBytes);
        }

        return new BundleFixture(archivePath, await ComputeSha256Async(archivePath));
    }

    private async Task<string> CreateTraversalBundleAsync(string entryName)
    {
        Directory.CreateDirectory(_testRoot);
        string archivePath = Path.Combine(_testRoot, Guid.NewGuid().ToString("N") + ".zip");
        await using var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        await WriteEntryAsync(archive, entryName, Encoding.UTF8.GetBytes("escape"));
        await WriteEntryAsync(archive, "release-manifest.json", Encoding.UTF8.GetBytes("{}"));
        return archivePath;
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string path, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        await using Stream stream = entry.Open();
        await stream.WriteAsync(bytes);
    }

    private static byte[] CreateNupkg(string id, string version, string? rootOverride, bool nestedMetadata)
    {
        using var output = new MemoryStream();
        using (var package = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry nuspec = package.CreateEntry(id + ".nuspec", CompressionLevel.NoCompression);
            using Stream stream = nuspec.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false);
            string root = rootOverride ?? "package";
            writer.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><");
            writer.Write(root);
            writer.Write("><metadata><id>");
            writer.Write(id);
            writer.Write("</id><version>");
            writer.Write(version);
            writer.Write("</version></metadata>");
            if (nestedMetadata)
                writer.Write("<identity><metadata><id>nested</id><version>nested</version></metadata></identity>");
            writer.Write("</");
            writer.Write(root);
            writer.Write(">");
        }
        return output.ToArray();
    }

    private static string ComputeSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class BundleSpecification
    {
        public string? OmittedPackageId { get; set; }
        public string? PackageVersionOverride { get; set; }
        public bool CorruptDeclaredFileHash { get; set; }
        public string? DuplicateEntryPath { get; set; }
        public string? UndeclaredEntryPath { get; set; }
        public string? DeclaredExtraPath { get; set; }
        public string? FirstPackagePathOverride { get; set; }
        public string? FirstPackageNuspecIdOverride { get; set; }
        public string? FirstPackageNuspecRootOverride { get; set; }
        public bool FirstPackageNuspecNestedMetadata { get; set; }
        public string CatalogMarker { get; set; } = "valid";
        public List<string> Capabilities { get; set; } = [];
    }

    private sealed class CallbackProgress(Action<HarmonyOSSdkBundleImportProgress> callback) : IProgress<HarmonyOSSdkBundleImportProgress>
    {
        public void Report(HarmonyOSSdkBundleImportProgress value) => callback(value);
    }

    private sealed class BlockingExtractionProgress : IProgress<HarmonyOSSdkBundleImportProgress>, IDisposable
    {
        private int _blocked;

        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Continue { get; } = new(false);

        public void Report(HarmonyOSSdkBundleImportProgress value)
        {
            if (value.Stage == "Extracting" && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                Entered.Set();
                Continue.Wait(TimeSpan.FromSeconds(20));
            }
        }

        public void Dispose()
        {
            Entered.Dispose();
            Continue.Dispose();
        }
    }

    private sealed record BundleFixture(string ArchivePath, string ArchiveSha256);

    private sealed record BundlePayload(string Path, byte[] Bytes);

    private sealed class BundleManifest
    {
        public int SchemaVersion { get; init; }
        public string ReleaseId { get; init; } = string.Empty;
        public string PackageVersion { get; init; } = string.Empty;
        public List<string> Capabilities { get; init; } = [];
        public List<BundleFile> Files { get; init; } = [];
        public List<BundlePackage> Packages { get; init; } = [];
    }

    private sealed class BundleFile
    {
        public string Path { get; init; } = string.Empty;
        public long Length { get; init; }
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class BundlePackage
    {
        public string Id { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
    }
}
