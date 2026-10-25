// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using XEngine.Editor.Build.HarmonyOS;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSSdkBundleLockTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineHarmonyOSSdkBundleLockTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void BundleException_PreservesStableErrorCode()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        var exception = new HarmonyOSBundleException(
            HarmonyOSBundleErrorCode.ManifestInvalid,
            "The SDK bundle signature is invalid.");

        Assert.Equal(HarmonyOSBundleErrorCode.ManifestInvalid, exception.Code);
        Assert.Equal("The SDK bundle signature is invalid.", exception.Message);
    }

    [Fact]
    public async Task LockRoundTrip_UsesRelativePathsAndStableFingerprint()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        HarmonyOSSdkBundleLock input = CreateLock("ThirdParty/OpenHarmonySdk/10.0.10-ohos.2-preview.1");
        string lockPath = Path.Combine(_testRoot, "ThirdParty", "OpenHarmonySdk", "openharmony-sdk-lock.json");

        await HarmonyOSSdkBundleLockFile.WriteAsync(lockPath, input);
        HarmonyOSSdkBundleLock loaded = await HarmonyOSSdkBundleLockFile.ReadAsync(lockPath);

        Assert.Equal(HarmonyOSSdkBundleLockFile.ComputeFingerprint(input), loaded.Fingerprint);
        Assert.Equal(input.BundleRelativePath, loaded.BundleRelativePath);
        Assert.False(Path.IsPathFullyQualified(loaded.BundleRelativePath));
    }

    [Fact]
    public void ComputeFingerprint_IsIndependentOfFileAndPackageInputOrder()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        HarmonyOSSdkBundleLock first = CreateLock("ThirdParty/OpenHarmonySdk/10.0.10-ohos.2-preview.1");
        first.Files.Add(new HarmonyOSSdkBundleLockedFile
        {
            RelativePath = "schemas/build-profile.json",
            Length = 456,
            Sha256 = new string('e', 64),
        });
        first.Packages.Add(new HarmonyOSSdkBundleLockedPackage
        {
            Id = "OpenHarmony.NDK.Bindings",
            Version = "10.0.10-ohos.2-preview.1",
            RelativePath = "feed/OpenHarmony.NDK.Bindings.10.0.10-ohos.2-preview.1.nupkg",
            Sha256 = new string('f', 64),
        });

        HarmonyOSSdkBundleLock reordered = CreateLock("ThirdParty/OpenHarmonySdk/10.0.10-ohos.2-preview.1");
        reordered.Files.Add(first.Files[1]);
        reordered.Files.Reverse();
        reordered.Packages.Add(first.Packages[1]);
        reordered.Packages.Reverse();

        Assert.Equal(
            HarmonyOSSdkBundleLockFile.ComputeFingerprint(first),
            HarmonyOSSdkBundleLockFile.ComputeFingerprint(reordered));
    }

    [Theory]
    [InlineData("C:/outside")]
    [InlineData("../outside")]
    [InlineData("ThirdParty/../outside")]
    public async Task WriteAsync_RejectsUnsafeBundleRelativePath(string path)
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        HarmonyOSSdkBundleLock invalid = CreateLock(path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HarmonyOSSdkBundleLockFile.WriteAsync(Path.Combine(_testRoot, "lock.json"), invalid));
    }

    [Fact]
    public async Task ReadAsync_RejectsFingerprintMismatch()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        HarmonyOSSdkBundleLock input = CreateLock("ThirdParty/OpenHarmonySdk/10.0.10-ohos.2-preview.1");
        string lockPath = Path.Combine(_testRoot, "openharmony-sdk-lock.json");
        await HarmonyOSSdkBundleLockFile.WriteAsync(lockPath, input);

        string json = await File.ReadAllTextAsync(lockPath);
        string tampered = json.Replace(
            HarmonyOSSdkBundleLockFile.ComputeFingerprint(input),
            new string('0', 64),
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(lockPath, tampered);

        await Assert.ThrowsAsync<InvalidDataException>(() => HarmonyOSSdkBundleLockFile.ReadAsync(lockPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private static HarmonyOSSdkBundleLock CreateLock(string bundleRelativePath) => new()
    {
        ReleaseId = "10.0.10-ohos.2-preview.1",
        PackageVersion = "10.0.10-ohos.2-preview.1",
        BundleRelativePath = bundleRelativePath,
        ArchiveSha256 = new string('a', 64),
        ManifestSha256 = new string('b', 64),
        SourceKind = "LocalArchive",
        Files = new List<HarmonyOSSdkBundleLockedFile>
        {
            new()
            {
                RelativePath = "schemas/sdk-catalog.json",
                Sha256 = new string('c', 64),
                Length = 123,
            },
        },
        Packages = new List<HarmonyOSSdkBundleLockedPackage>
        {
            new()
            {
                Id = "OpenHarmony.NET.Player.Sdk",
                Version = "10.0.10-ohos.2-preview.1",
                RelativePath = "feed/OpenHarmony.NET.Player.Sdk.10.0.10-ohos.2-preview.1.nupkg",
                Sha256 = new string('d', 64),
            },
        },
    };
}
