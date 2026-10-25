// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

using XEngine.Editor.Build.HarmonyOS;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSBuildReceiptTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineHarmonyOSBuildReceiptTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Create_UsesHashesAndRedactedDeviceFactsOnly()
    {
        HarmonyOSBuildReceipt receipt = HarmonyOSBuildReceipt.Create(new HarmonyOSBuildReceiptInput
        {
            Action = "deploy",
            BundleFingerprint = new string('a', 64),
            OfficialSdkHash = new string('b', 64),
            XEngineCommit = new string('c', 40),
            SigningReference = "EnvironmentVariable:CI_OHOS_SIGNING",
            DeviceId = "serial-12345",
            ArtifactRelativePath = "output/SmokeGame.hap",
            ArtifactSha256 = new string('d', 64),
            ArtifactBytes = 1024,
            Provenance = CreateProvenance(sourceState: "Dirty"),
            ToolVersions = new Dictionary<string, string>
            {
                ["hvigor"] = "5.0.0",
                ["dotnet"] = "10.0.100",
            },
            Findings = ["signature-valid", "elf-valid"],
        });

        string json = receipt.ToJson();
        Assert.DoesNotContain("serial-12345", json, StringComparison.Ordinal);
        Assert.Contains("deviceIdHash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("CI_OHOS_SIGNING=", json, StringComparison.Ordinal);
        Assert.Contains("EnvironmentVariable", json, StringComparison.Ordinal);
        Assert.DoesNotContain("D:\\Engine\\OHOS", json, StringComparison.OrdinalIgnoreCase);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        JsonElement provenance = document.RootElement.GetProperty("provenance");
        Assert.Equal("Dirty", provenance.GetProperty("sourceState").GetString());
        Assert.Equal("release-fixture", provenance.GetProperty("provider").GetProperty("releaseId").GetString());
        Assert.Equal(13, provenance.GetProperty("build").GetProperty("targetApi").GetInt32());
        Assert.Equal("x86_64", provenance.GetProperty("build").GetProperty("targetAbi").GetString());
    }

    [Fact]
    public async Task WriteAsync_OrdersToolVersionsAndArtifactPaths()
    {
        HarmonyOSBuildReceipt receipt = HarmonyOSBuildReceipt.Create(new HarmonyOSBuildReceiptInput
        {
            Action = "verify",
            BundleFingerprint = new string('a', 64),
            OfficialSdkHash = new string('b', 64),
            XEngineCommit = new string('c', 40),
            SigningReference = "None",
            ArtifactRelativePath = "z/output.hap",
            ArtifactSha256 = new string('d', 64),
            ArtifactBytes = 12,
            Provenance = CreateProvenance(),
            ToolVersions = new Dictionary<string, string>
            {
                ["zeta"] = "1",
                ["alpha"] = "2",
            },
            NativeBuildIds = new Dictionary<string, string>
            {
                ["libxengine_player.so"] = "000102030405060708090a0b0c0d0e0f10111213",
                ["libxengine_host.so"] = "202122232425262728292a2b2c2d2e2f30313233",
            },
            Findings = ["z-last", "a-first"],
        });
        string path = Path.Combine(_testRoot, "receipt.json");

        await receipt.WriteAsync(path);

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        JsonElement tools = document.RootElement.GetProperty("toolVersions");
        Assert.Equal("alpha", tools[0].GetProperty("name").GetString());
        Assert.Equal("zeta", tools[1].GetProperty("name").GetString());
        JsonElement findings = document.RootElement.GetProperty("findings");
        Assert.Equal("a-first", findings[0].GetString());
        Assert.Equal("z-last", findings[1].GetString());
        JsonElement buildIds = document.RootElement.GetProperty("nativeBuildIds");
        Assert.Equal("libxengine_host.so", buildIds[0].GetProperty("name").GetString());
        Assert.Equal("libxengine_player.so", buildIds[1].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(@"toolPath=D:\sdk\hdc.exe")]
    [InlineData("token=secret-value")]
    [InlineData("artifact=/home/ci/output.hap")]
    public void Create_RejectsLocalPathsAndCredentialAssignments(string finding)
    {
        var input = new HarmonyOSBuildReceiptInput
        {
            Action = "verify",
            BundleFingerprint = new string('a', 64),
            OfficialSdkHash = new string('b', 64),
            XEngineCommit = new string('c', 40),
            SigningReference = "None",
            ArtifactRelativePath = "output.hap",
            ArtifactSha256 = new string('d', 64),
            ArtifactBytes = 12,
            Provenance = CreateProvenance(),
            Findings = [finding],
        };

        Assert.Throws<InvalidDataException>(() => HarmonyOSBuildReceipt.Create(input));
    }

    [Fact]
    public void Create_RejectsMissingStructuredProvenance()
    {
        var input = new HarmonyOSBuildReceiptInput
        {
            Action = "verify-hap",
            BundleFingerprint = new string('a', 64),
            OfficialSdkHash = new string('b', 64),
            XEngineCommit = new string('c', 40),
            SigningReference = "None",
            ArtifactRelativePath = "output.hap",
            ArtifactSha256 = new string('d', 64),
            ArtifactBytes = 12,
            NativeBuildIds = new Dictionary<string, string>
            {
                ["libxengine_player.so"] = new string('1', 40),
                ["libxengine_host.so"] = new string('2', 40),
            },
        };

        Assert.Throws<InvalidDataException>(() => HarmonyOSBuildReceipt.Create(input));
    }

    private static HarmonyOSBuildProvenanceInput CreateProvenance(string sourceState = "Clean") => new()
    {
        SourceState = sourceState,
        ProviderReleaseId = "release-fixture",
        ProviderManifestSha256 = new string('e', 64),
        ProviderPackageHashes =
        [
            "OpenHarmony.NET.HapTooling:" + new string('1', 64),
            "OpenHarmony.NET.Player.Sdk:" + new string('2', 64),
        ],
        TargetApi = 13,
        TargetAbi = "x86_64",
        BuildConfiguration = "Release",
        BuildSettingsSha256 = new string('3', 64),
        OwnershipManifestSha256 = new string('4', 64),
        PayloadManifestSha256 = new string('5', 64),
    };

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }
}
