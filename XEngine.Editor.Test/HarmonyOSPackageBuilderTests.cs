// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO.Compression;
using System.Text;
using System.Text.Json;

using XEngine.Editor.Build.HarmonyOS;
using XEngine.Editor.Projects.Build;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSPackageBuilderTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineHarmonyOSPackageBuilderTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BuildAsync_HvigorFailureLeavesNoPublishedArtifact()
    {
        string stage = Path.Combine(_testRoot, "Stage");
        string output = Path.Combine(_testRoot, "Output");
        Directory.CreateDirectory(stage);
        var runner = new RecordingProcessRunner(exitCode: 1, standardError: "hvigor failed");

        HarmonyOSBuildException error = await Assert.ThrowsAsync<HarmonyOSBuildException>(() =>
            new HarmonyOSPackageBuilder(runner).BuildAsync(new HarmonyOSPackageBuildRequest
            {
                StageProjectDirectory = stage,
                ArtifactRelativePath = "outputs/SmokeGame.hap",
                PublishedOutputDirectory = output,
                OutputMode = HarmonyOSOutputMode.Hap,
                HvigorExecutablePath = "hvigorw",
                HvigorArguments = ["assembleHap"],
            }));

        Assert.Equal(HarmonyOSBuildErrorCode.PackageBuildFailed, error.Code);
        Assert.False(File.Exists(Path.Combine(output, "SmokeGame.hap")));
        Assert.Single(runner.Commands);
        Assert.Equal(stage, runner.Commands[0].WorkingDirectory);
    }

    [Fact]
    public async Task VerifyAsync_RejectsUnexpectedElfDependencyAndRedactsSigningReference()
    {
        string hapPath = Path.Combine(_testRoot, "SmokeGame.hap");
        CreateHap(hapPath, "libforbidden.so");

        HarmonyOSPackageVerification verification = await new HarmonyOSPackageVerifier().VerifyAsync(
            new HarmonyOSPackageVerificationRequest
            {
                ArtifactPath = hapPath,
                OutputMode = HarmonyOSOutputMode.Hap,
                ApplicationId = "com.xengine.smoke",
                VersionCode = 1,
                Abi = "x86_64",
                BundleFingerprint = new string('a', 64),
                OfficialSdkHash = new string('b', 64),
                XEngineCommit = new string('c', 40),
                Provenance = CreateProvenance(),
                SigningReference = "EnvironmentVariable:CI_OHOS_SIGNING",
                ForbiddenPlaintext = ["signing-password"],
            });

        Assert.False(verification.Success);
        Assert.Contains(verification.Errors, error => error.Contains("DT_NEEDED", StringComparison.Ordinal));
        Assert.DoesNotContain("signing-password", verification.ReceiptJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsPackageWithoutPayloadHashManifest()
    {
        string hapPath = Path.Combine(_testRoot, "MissingPayloadManifest.hap");
        CreateHap(hapPath, "libforbidden.so", includePayloadManifest: false);

        HarmonyOSPackageVerification verification = await new HarmonyOSPackageVerifier().VerifyAsync(
            new HarmonyOSPackageVerificationRequest
            {
                ArtifactPath = hapPath,
                OutputMode = HarmonyOSOutputMode.Hap,
                ApplicationId = "com.xengine.smoke",
                VersionCode = 1,
                Abi = "x86_64",
                BundleFingerprint = new string('a', 64),
                OfficialSdkHash = new string('b', 64),
                XEngineCommit = new string('c', 40),
                Provenance = CreateProvenance(),
            });

        Assert.Contains(verification.Errors, error => error.Contains("payload hash manifest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PublishVerifiedAsync_MovesArtifactAndReceiptAtomically()
    {
        string stage = Path.Combine(_testRoot, "Stage");
        string artifact = Path.Combine(stage, "outputs", "SmokeGame.hap");
        string output = Path.Combine(_testRoot, "Published");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllBytesAsync(artifact, [1, 2, 3]);

        string published = await new HarmonyOSPackageBuilder().PublishVerifiedAsync(
            new HarmonyOSPendingPackage
            {
                StageProjectDirectory = stage,
                ArtifactPath = artifact,
                PublishedOutputDirectory = output,
                OutputMode = HarmonyOSOutputMode.Hap,
            },
            new HarmonyOSPackageVerification
            {
                Success = true,
                ReceiptJson = "{\"success\":true}\n",
            });

        Assert.Equal(Path.Combine(output, "SmokeGame.hap"), published);
        Assert.True(File.Exists(published));
        Assert.Equal("{\"success\":true}\n", await File.ReadAllTextAsync(Path.Combine(output, "SmokeGame.hap.receipt.json")));
        Assert.Empty(Directory.GetDirectories(_testRoot, ".Published.xengine-harmonyos-*.staging"));
    }

    [Fact]
    public async Task PublishVerifiedAsync_RejectsFailedVerificationWithoutTouchingOutput()
    {
        string stage = Path.Combine(_testRoot, "Stage");
        string artifact = Path.Combine(stage, "outputs", "SmokeGame.hap");
        string output = Path.Combine(_testRoot, "Published");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await File.WriteAllBytesAsync(artifact, [1]);

        HarmonyOSBuildException error = await Assert.ThrowsAsync<HarmonyOSBuildException>(() =>
            new HarmonyOSPackageBuilder().PublishVerifiedAsync(
                new HarmonyOSPendingPackage
                {
                    StageProjectDirectory = stage,
                    ArtifactPath = artifact,
                    PublishedOutputDirectory = output,
                    OutputMode = HarmonyOSOutputMode.Hap,
                },
                new HarmonyOSPackageVerification
                {
                    Success = false,
                    ReceiptJson = "{\"success\":false}\n",
                }));

        Assert.Equal(HarmonyOSBuildErrorCode.PackageVerificationFailed, error.Code);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task VerifyAsync_DoesNotTreatIncidentalSymbolTextAsAnExport()
    {
        string hapPath = Path.Combine(_testRoot, "IncidentalSymbol.hap");
        CreateHap(hapPath, "liballowed.so", includePayloadManifest: true, incidentalSymbolText: "xengine_player_create");

        HarmonyOSPackageVerification verification = await new HarmonyOSPackageVerifier().VerifyAsync(
            new HarmonyOSPackageVerificationRequest
            {
                ArtifactPath = hapPath,
                OutputMode = HarmonyOSOutputMode.Hap,
                ApplicationId = "com.xengine.smoke",
                VersionCode = 1,
                Abi = "x86_64",
                BundleFingerprint = new string('a', 64),
                OfficialSdkHash = new string('b', 64),
                XEngineCommit = new string('c', 40),
                Provenance = CreateProvenance(),
                AllowedElfDependencies = ["liballowed.so"],
                RequiredNativeSymbols = ["xengine_player_create"],
            });

        Assert.False(verification.Success);
        Assert.Contains(verification.Errors, error => error.Contains("dynamic symbol table", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAsync_AcceptsExpectedAbiAndDynamicPlayerExports()
    {
        string hapPath = Path.Combine(_testRoot, "ExportingPlayer.hap");
        CreateExportingHap(hapPath);

        HarmonyOSPackageVerification verification = await new HarmonyOSPackageVerifier().VerifyAsync(
            new HarmonyOSPackageVerificationRequest
            {
                ArtifactPath = hapPath,
                OutputMode = HarmonyOSOutputMode.Hap,
                ApplicationId = "com.xengine.smoke",
                VersionCode = 1,
                Abi = "x86_64",
                BundleFingerprint = new string('a', 64),
                OfficialSdkHash = new string('b', 64),
                XEngineCommit = new string('c', 40),
                Provenance = CreateProvenance(),
                AllowedElfDependencies = ["liballowed.so"],
            });

        Assert.True(verification.Success, string.Join(Environment.NewLine, verification.Errors));
        using JsonDocument receipt = JsonDocument.Parse(verification.ReceiptJson);
        JsonElement buildIds = receipt.RootElement.GetProperty("nativeBuildIds");
        Assert.Equal("libxengine_host.so", buildIds[0].GetProperty("name").GetString());
        Assert.Equal("202122232425262728292a2b2c2d2e2f30313233", buildIds[0].GetProperty("gnuBuildId").GetString());
        Assert.Equal("libxengine_player.so", buildIds[1].GetProperty("name").GetString());
        Assert.Equal("000102030405060708090a0b0c0d0e0f10111213", buildIds[1].GetProperty("gnuBuildId").GetString());
    }

    [Fact]
    public async Task VerifyAsync_RejectsNativeLibrariesWithoutGnuBuildIds()
    {
        string hapPath = Path.Combine(_testRoot, "MissingBuildIds.hap");
        CreateExportingHap(hapPath, includeBuildIds: false);

        HarmonyOSPackageVerification verification = await new HarmonyOSPackageVerifier().VerifyAsync(
            new HarmonyOSPackageVerificationRequest
            {
                ArtifactPath = hapPath,
                OutputMode = HarmonyOSOutputMode.Hap,
                ApplicationId = "com.xengine.smoke",
                VersionCode = 1,
                Abi = "x86_64",
                BundleFingerprint = new string('a', 64),
                OfficialSdkHash = new string('b', 64),
                XEngineCommit = new string('c', 40),
                Provenance = CreateProvenance(),
                AllowedElfDependencies = ["liballowed.so"],
            });

        Assert.False(verification.Success);
        Assert.Contains(verification.Errors, error => error.Contains("GNU build-id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyAsync_RejectsPackageWithoutNativeHost()
    {
        string hapPath = Path.Combine(_testRoot, "MissingNativeHost.hap");
        CreateExportingHap(hapPath, includeNativeHost: false);

        HarmonyOSPackageVerification verification = await new HarmonyOSPackageVerifier().VerifyAsync(
            new HarmonyOSPackageVerificationRequest
            {
                ArtifactPath = hapPath,
                OutputMode = HarmonyOSOutputMode.Hap,
                ApplicationId = "com.xengine.smoke",
                VersionCode = 1,
                Abi = "x86_64",
                BundleFingerprint = new string('a', 64),
                OfficialSdkHash = new string('b', 64),
                XEngineCommit = new string('c', 40),
                Provenance = CreateProvenance(),
                AllowedElfDependencies = ["liballowed.so"],
            });

        Assert.False(verification.Success);
        Assert.Contains(verification.Errors, error => error.Contains("libxengine_host.so", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private static HarmonyOSBuildProvenanceInput CreateProvenance() => new()
    {
        SourceState = "Clean",
        ProviderReleaseId = "release-fixture",
        ProviderManifestSha256 = new string('e', 64),
        ProviderPackageHashes = ["OpenHarmony.NET.HapTooling:" + new string('1', 64)],
        TargetApi = 13,
        TargetAbi = "x86_64",
        BuildConfiguration = "Release",
        BuildSettingsSha256 = new string('3', 64),
        OwnershipManifestSha256 = new string('4', 64),
        PayloadManifestSha256 = new string('5', 64),
    };

    private static void CreateHap(
        string path,
        string dependency,
        bool includePayloadManifest = true,
        string? incidentalSymbolText = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteText(archive, "module.json", "{\"bundleName\":\"com.xengine.smoke\",\"versionCode\":1}");
        if (includePayloadManifest)
            WriteBytes(archive, "resources/rawfile/xengine/xengine-harmonyos-payload.json", "{\"schemaVersion\":1,\"files\":[]}"u8.ToArray());
        WriteBytes(archive, "libs/x86_64/libxengine_player.so", CreateElf(dependency, incidentalSymbolText));
    }

    private static void CreateExportingHap(string path, bool includeNativeHost = true, bool includeBuildIds = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteText(archive, "module.json", "{\"bundleName\":\"com.xengine.smoke\",\"versionCode\":1}");
        WriteBytes(archive, "resources/rawfile/xengine/xengine-harmonyos-payload.json", "{\"schemaVersion\":1,\"files\":[]}"u8.ToArray());
        WriteBytes(archive, "libs/x86_64/libxengine_player.so", CreateElfWithExports(0, includeBuildIds));
        if (includeNativeHost)
            WriteBytes(archive, "libs/x86_64/libxengine_host.so", CreateElfWithExports(32, includeBuildIds));
    }

    private static byte[] CreateElf(string dependency, string? incidentalSymbolText)
    {
        byte[] dependencyBytes = Encoding.UTF8.GetBytes("\0" + dependency + "\0" + incidentalSymbolText + "\0");
        byte[] result = new byte[0x300 + dependencyBytes.Length];
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
        BitConverter.GetBytes((ushort)2).CopyTo(result, 56);

        WriteProgramHeader(result, 64, 1, 0, 0, (ulong)result.Length, (ulong)result.Length);
        WriteProgramHeader(result, 120, 2, 0x200, 0x200, 48, 48);
        WriteDynamicEntry(result, 0x200, 1, 1);
        WriteDynamicEntry(result, 0x210, 5, 0x300);
        WriteDynamicEntry(result, 0x220, 10, (ulong)dependencyBytes.Length);
        WriteDynamicEntry(result, 0x230, 0, 0);
        dependencyBytes.CopyTo(result, 0x300);
        return result;
    }

    private static byte[] CreateElfWithExports(byte buildIdSeed, bool includeBuildId)
    {
        string[] symbols =
        [
            "xengine_player_abi_version",
            "xengine_player_create",
            "xengine_player_attach_surface",
            "xengine_player_resize_surface",
            "xengine_player_detach_surface",
            "xengine_player_set_resumed",
            "xengine_player_touch",
            "xengine_player_key",
            "xengine_player_mouse",
            "xengine_player_mouse_wheel",
            "xengine_player_focus",
            "xengine_player_tick",
            "xengine_player_destroy",
        ];
        var strings = new List<byte> { 0 };
        int dependencyOffset = strings.Count;
        strings.AddRange(Encoding.UTF8.GetBytes("liballowed.so\0"));
        var symbolOffsets = new List<int>(symbols.Length);
        foreach (string symbol in symbols)
        {
            symbolOffsets.Add(strings.Count);
            strings.AddRange(Encoding.UTF8.GetBytes(symbol + "\0"));
        }

        const int stringTableOffset = 0x300;
        const int dynamicSymbolOffset = 0x500;
        const int buildIdOffset = 0x700;
        const int sectionTableOffset = 0x800;
        byte[] result = new byte[sectionTableOffset + 3 * 64];
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
        BitConverter.GetBytes((ulong)sectionTableOffset).CopyTo(result, 40);
        BitConverter.GetBytes((ushort)64).CopyTo(result, 52);
        BitConverter.GetBytes((ushort)56).CopyTo(result, 54);
        BitConverter.GetBytes((ushort)(includeBuildId ? 3 : 2)).CopyTo(result, 56);
        BitConverter.GetBytes((ushort)64).CopyTo(result, 58);
        BitConverter.GetBytes((ushort)3).CopyTo(result, 60);

        WriteProgramHeader(result, 64, 1, 0, 0, (ulong)result.Length, (ulong)result.Length);
        WriteProgramHeader(result, 120, 2, 0x200, 0x200, 64, 64);
        if (includeBuildId)
            WriteGnuBuildId(result, 176, buildIdOffset, buildIdSeed);
        WriteDynamicEntry(result, 0x200, 1, (ulong)dependencyOffset);
        WriteDynamicEntry(result, 0x210, 5, stringTableOffset);
        WriteDynamicEntry(result, 0x220, 10, (ulong)strings.Count);
        WriteDynamicEntry(result, 0x230, 0, 0);
        strings.CopyTo(result, stringTableOffset);

        for (int i = 0; i < symbolOffsets.Count; i++)
        {
            int offset = dynamicSymbolOffset + (i + 1) * 24;
            BitConverter.GetBytes((uint)symbolOffsets[i]).CopyTo(result, offset);
            result[offset + 4] = 0x12;
            BitConverter.GetBytes((ushort)1).CopyTo(result, offset + 6);
            BitConverter.GetBytes((ulong)(0x1000 + i)).CopyTo(result, offset + 8);
        }

        WriteSectionHeader(result, sectionTableOffset + 64, 3, stringTableOffset, (ulong)strings.Count, 0, 0);
        WriteSectionHeader(result, sectionTableOffset + 128, 11, dynamicSymbolOffset, (ulong)((symbols.Length + 1) * 24), 1, 24);
        return result;
    }

    private static void WriteGnuBuildId(byte[] target, int programHeaderOffset, int noteOffset, byte seed)
    {
        WriteProgramHeader(target, programHeaderOffset, 4, (ulong)noteOffset, (ulong)noteOffset, 36, 36);
        BitConverter.GetBytes((uint)4).CopyTo(target, noteOffset);
        BitConverter.GetBytes((uint)20).CopyTo(target, noteOffset + 4);
        BitConverter.GetBytes((uint)3).CopyTo(target, noteOffset + 8);
        target[noteOffset + 12] = (byte)'G';
        target[noteOffset + 13] = (byte)'N';
        target[noteOffset + 14] = (byte)'U';
        for (int i = 0; i < 20; i++)
            target[noteOffset + 16 + i] = (byte)(seed + i);
    }

    private static void WriteProgramHeader(byte[] target, int offset, uint type, ulong fileOffset, ulong virtualAddress, ulong fileSize, ulong memorySize)
    {
        BitConverter.GetBytes(type).CopyTo(target, offset);
        BitConverter.GetBytes((uint)5).CopyTo(target, offset + 4);
        BitConverter.GetBytes(fileOffset).CopyTo(target, offset + 8);
        BitConverter.GetBytes(virtualAddress).CopyTo(target, offset + 16);
        BitConverter.GetBytes(fileSize).CopyTo(target, offset + 32);
        BitConverter.GetBytes(memorySize).CopyTo(target, offset + 40);
        BitConverter.GetBytes((ulong)0x1000).CopyTo(target, offset + 48);
    }

    private static void WriteDynamicEntry(byte[] target, int offset, long tag, ulong value)
    {
        BitConverter.GetBytes(tag).CopyTo(target, offset);
        BitConverter.GetBytes(value).CopyTo(target, offset + 8);
    }

    private static void WriteSectionHeader(byte[] target, int offset, uint type, ulong fileOffset, ulong size, uint link, ulong entrySize)
    {
        BitConverter.GetBytes(type).CopyTo(target, offset + 4);
        BitConverter.GetBytes(fileOffset).CopyTo(target, offset + 24);
        BitConverter.GetBytes(size).CopyTo(target, offset + 32);
        BitConverter.GetBytes(link).CopyTo(target, offset + 40);
        BitConverter.GetBytes(entrySize).CopyTo(target, offset + 56);
    }

    private static void WriteText(ZipArchive archive, string path, string content) =>
        WriteBytes(archive, path, Encoding.UTF8.GetBytes(content));

    private static void WriteBytes(ZipArchive archive, string path, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using Stream stream = entry.Open();
        stream.Write(bytes);
    }

    private sealed class RecordingProcessRunner(int exitCode, string standardError) : IHarmonyOSProcessRunner
    {
        public List<HarmonyOSProcessCommand> Commands { get; } = [];

        public Task<HarmonyOSProcessResult> RunAsync(
            HarmonyOSProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(new HarmonyOSProcessResult(
                exitCode,
                1,
                string.Empty,
                standardError,
                command.ExecutablePath,
                false,
                false));
        }
    }
}
