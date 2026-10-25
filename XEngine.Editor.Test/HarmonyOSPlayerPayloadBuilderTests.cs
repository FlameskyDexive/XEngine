// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build.HarmonyOS;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSPlayerPayloadBuilderTests : IDisposable
{
    private static readonly string[] s_packageIds =
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
        "XEngineHarmonyOSPlayerPayloadBuilderTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PublishAsync_UsesOnlyTheLockedFeedAndHashesPayload()
    {
        PublishFixture fixture = await CreateFixtureAsync();
        var runner = new RecordingRunner(fixture.OutputDirectory, writesUnexpectedLibrary: false);

        HarmonyOSPlayerPayloadResult result = await new HarmonyOSPlayerPayloadBuilder(runner).PublishAsync(
            new HarmonyOSPlayerPublishRequest
            {
                PlayerProjectPath = fixture.PlayerProjectPath,
                BundleRoot = fixture.BundleRoot,
                BundleLock = fixture.Lock,
                BundleFingerprint = fixture.Lock.Fingerprint,
                RuntimeIdentifier = "linux-musl-x64",
                OutputDirectory = fixture.OutputDirectory,
                IntermediateDirectory = fixture.IntermediateDirectory,
            });

        HarmonyOSProcessCommand command = Assert.Single(runner.Commands);
        Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(command.ExecutablePath));
        Assert.Contains("publish", command.Arguments, StringComparer.Ordinal);
        Assert.Contains("--configfile", command.Arguments, StringComparer.Ordinal);
        Assert.Contains("--source", command.Arguments, StringComparer.Ordinal);
        Assert.Contains("linux-musl-x64", command.Arguments, StringComparer.Ordinal);
        Assert.Contains(
            "--property:BaseIntermediateOutputPath=" +
            Path.Combine(fixture.IntermediateDirectory, "obj") + Path.DirectorySeparatorChar,
            command.Arguments,
            StringComparer.Ordinal);
        Assert.Contains(
            "--property:BaseOutputPath=" +
            Path.Combine(fixture.IntermediateDirectory, "bin") + Path.DirectorySeparatorChar,
            command.Arguments,
            StringComparer.Ordinal);
        Assert.DoesNotContain(command.Arguments, value => value.Contains("D:\\Engine\\OHOS", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            Path.Combine(fixture.OutputDirectory, "libxengine_player.so"),
            result.PlayerLibraryPath);
        Assert.Contains(result.Files, file => file.RelativePath.EndsWith("libxengine_player.so", StringComparison.Ordinal));
        Assert.All(result.Files, file => Assert.Matches("^[0-9a-f]{64}$", file.Sha256));
        Assert.Equal("000102030405060708090a0b0c0d0e0f10111213", result.PlayerBuildId);
    }

    [Fact]
    public async Task PublishAsync_RejectsUnexpectedNativeLibrary()
    {
        PublishFixture fixture = await CreateFixtureAsync();
        var runner = new RecordingRunner(fixture.OutputDirectory, writesUnexpectedLibrary: true);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HarmonyOSPlayerPayloadBuilder(runner).PublishAsync(new HarmonyOSPlayerPublishRequest
            {
                PlayerProjectPath = fixture.PlayerProjectPath,
                BundleRoot = fixture.BundleRoot,
                BundleLock = fixture.Lock,
                BundleFingerprint = fixture.Lock.Fingerprint,
                RuntimeIdentifier = "linux-musl-x64",
                OutputDirectory = fixture.OutputDirectory,
                IntermediateDirectory = fixture.IntermediateDirectory,
            }));
    }

    [Fact]
    public async Task PublishAsync_RejectsPlayerWithoutGnuBuildId()
    {
        PublishFixture fixture = await CreateFixtureAsync();
        var runner = new RecordingRunner(fixture.OutputDirectory, writesUnexpectedLibrary: false, includeBuildId: false);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HarmonyOSPlayerPayloadBuilder(runner).PublishAsync(new HarmonyOSPlayerPublishRequest
            {
                PlayerProjectPath = fixture.PlayerProjectPath,
                BundleRoot = fixture.BundleRoot,
                BundleLock = fixture.Lock,
                BundleFingerprint = fixture.Lock.Fingerprint,
                RuntimeIdentifier = "linux-musl-x64",
                OutputDirectory = fixture.OutputDirectory,
                IntermediateDirectory = fixture.IntermediateDirectory,
            }));

        Assert.Contains("GNU build-id", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private async Task<PublishFixture> CreateFixtureAsync()
    {
        const string releaseId = "10.0.10-ohos.2-preview.1";
        string bundleRoot = Path.Combine(_testRoot, "bundle");
        string feedRoot = Path.Combine(bundleRoot, "feed");
        Directory.CreateDirectory(feedRoot);
        string playerProjectPath = Path.Combine(_testRoot, "Player", "Smoke.HarmonyOS.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(playerProjectPath)!);
        await File.WriteAllTextAsync(playerProjectPath, "<Project />", new UTF8Encoding(false));

        var files = new List<HarmonyOSSdkBundleLockedFile>();
        var packages = new List<HarmonyOSSdkBundleLockedPackage>();
        for (int i = 0; i < s_packageIds.Length; i++)
        {
            string id = s_packageIds[i];
            string relativePath = "feed/" + id + "." + releaseId + ".nupkg";
            string packagePath = Path.Combine(bundleRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
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

        string lockPath = Path.Combine(_testRoot, "openharmony-sdk-lock.json");
        await HarmonyOSSdkBundleLockFile.WriteAsync(lockPath, new HarmonyOSSdkBundleLock
        {
            ReleaseId = releaseId,
            PackageVersion = releaseId,
            BundleRelativePath = "ThirdParty/OpenHarmonySdk/" + releaseId,
            ArchiveSha256 = new string('a', 64),
            ManifestSha256 = new string('b', 64),
            SourceKind = "Fixture",
            Files = files,
            Packages = packages,
        });

        string outputDirectory = Path.Combine(_testRoot, "payload");
        string intermediateDirectory = Path.Combine(_testRoot, "intermediate");
        return new PublishFixture(
            bundleRoot,
            playerProjectPath,
            outputDirectory,
            intermediateDirectory,
            await HarmonyOSSdkBundleLockFile.ReadAsync(lockPath));
    }

    private static async Task<HarmonyOSSdkBundleLockedFile> CreateLockedFileAsync(string root, string relativePath)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        FileInfo info = new(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new HarmonyOSSdkBundleLockedFile
        {
            RelativePath = relativePath,
            Length = info.Length,
            Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant(),
        };
    }

    private sealed record PublishFixture(
        string BundleRoot,
        string PlayerProjectPath,
        string OutputDirectory,
        string IntermediateDirectory,
        HarmonyOSSdkBundleLock Lock);

    private sealed class RecordingRunner(
        string outputDirectory,
        bool writesUnexpectedLibrary,
        bool includeBuildId = true) : IHarmonyOSProcessRunner
    {
        internal List<HarmonyOSProcessCommand> Commands { get; } = [];

        public Task<HarmonyOSProcessResult> RunAsync(
            HarmonyOSProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            // NativeAOT publishes the .so directly into the --output root, not runtimes/<rid>/native/.
            string runtimeRoot = outputDirectory;
            Directory.CreateDirectory(runtimeRoot);
            File.WriteAllBytes(
                Path.Combine(runtimeRoot, "libxengine_player.so"),
                CreateElfWithBuildId(includeBuildId));
            if (writesUnexpectedLibrary)
                File.WriteAllBytes(Path.Combine(runtimeRoot, "libforeign.so"), [5, 6]);
            return Task.FromResult(new HarmonyOSProcessResult(0, 1, string.Empty, string.Empty, "dotnet publish", false, false));
        }

        private static byte[] CreateElfWithBuildId(bool includeBuildId)
        {
            byte[] result = new byte[0x200];
            result[0] = 0x7f;
            result[1] = (byte)'E';
            result[2] = (byte)'L';
            result[3] = (byte)'F';
            result[4] = 2;
            result[5] = 1;
            result[6] = 1;
            BitConverter.GetBytes((ushort)3).CopyTo(result, 16);
            BitConverter.GetBytes((ushort)62).CopyTo(result, 18);
            BitConverter.GetBytes((uint)1).CopyTo(result, 20);
            BitConverter.GetBytes((ulong)64).CopyTo(result, 32);
            BitConverter.GetBytes((ushort)64).CopyTo(result, 52);
            BitConverter.GetBytes((ushort)56).CopyTo(result, 54);
            BitConverter.GetBytes((ushort)(includeBuildId ? 1 : 0)).CopyTo(result, 56);
            if (!includeBuildId)
                return result;

            const int noteOffset = 0x100;
            BitConverter.GetBytes((uint)4).CopyTo(result, 64);
            BitConverter.GetBytes((uint)4).CopyTo(result, 68);
            BitConverter.GetBytes((ulong)noteOffset).CopyTo(result, 72);
            BitConverter.GetBytes((ulong)noteOffset).CopyTo(result, 80);
            BitConverter.GetBytes((ulong)36).CopyTo(result, 96);
            BitConverter.GetBytes((ulong)36).CopyTo(result, 104);
            BitConverter.GetBytes((uint)4).CopyTo(result, noteOffset);
            BitConverter.GetBytes((uint)20).CopyTo(result, noteOffset + 4);
            BitConverter.GetBytes((uint)3).CopyTo(result, noteOffset + 8);
            result[noteOffset + 12] = (byte)'G';
            result[noteOffset + 13] = (byte)'N';
            result[noteOffset + 14] = (byte)'U';
            for (int i = 0; i < 20; i++)
                result[noteOffset + 16 + i] = (byte)i;
            return result;
        }
    }
}
