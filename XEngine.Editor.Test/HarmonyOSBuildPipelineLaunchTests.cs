// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build;
using XEngine.Editor.Build.HarmonyOS;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSBuildPipelineLaunchTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineHarmonyOSBuildPipelineLaunchTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LaunchAsync_DeploysHapAndWritesRedactedRunReceipt()
    {
        Directory.CreateDirectory(_testRoot);
        string hap = Path.Combine(_testRoot, "Smoke.hap");
        await File.WriteAllTextAsync(hap, "verified-hap");
        await File.WriteAllTextAsync(hap + ".receipt.json", "{\"verified\":true}\n");
        BuildSettings settings = CreateSettings();
        var resolver = new FixtureToolchainResolver();
        var bridge = new FixtureDeviceBridge();
        var pipeline = new HarmonyOSBuildPipeline(resolver, bridge);

        BuildLaunchResult launch = await pipeline.LaunchAsync(new BuildLaunchRequest
        {
            ProjectPath = _testRoot,
            Settings = settings,
            Build = new BuildResult { Success = true, OutputPath = hap },
        });

        Assert.True(launch.Handled);
        Assert.True(launch.Success);
        Assert.Contains(HarmonyOSBundleCapability.HdcDeploy, resolver.RequiredCapabilities);
        Assert.NotNull(bridge.Request);
        Assert.Equal(hap, bridge.Request.HapPath);
        Assert.Equal("serial-sensitive-123", bridge.Request.RequestedDeviceId);
        Assert.Equal("MainAbility", bridge.Request.AbilityName);
        Assert.Equal(HarmonyOSInstallPolicy.LaunchOnly, bridge.Request.InstallPolicy);
        Assert.EndsWith("Smoke.run.png", bridge.Request.ScreenshotOutputPath, StringComparison.Ordinal);

        string receiptPath = hap + ".run.receipt.json";
        string receipt = await File.ReadAllTextAsync(receiptPath);
        Assert.DoesNotContain("serial-sensitive-123", receipt, StringComparison.Ordinal);
        Assert.Contains("\"deviceIdHash\"", receipt, StringComparison.Ordinal);
        Assert.Contains("\"action\": \"deploy-run\"", receipt, StringComparison.Ordinal);
        Assert.Contains("deploymentAction=LaunchOnly", receipt, StringComparison.Ordinal);
        Assert.Contains("screenshotSha256=" + new string('d', 64), receipt, StringComparison.Ordinal);
        using JsonDocument receiptDocument = JsonDocument.Parse(receipt);
        JsonElement provenance = receiptDocument.RootElement.GetProperty("provenance");
        Assert.Equal(
            ComputeSha256(hap + ".receipt.json"),
            provenance.GetProperty("parentReceiptSha256").GetString());
        Assert.False(string.IsNullOrWhiteSpace(provenance.GetProperty("observedAtUtc").GetString()));
    }

    [Fact]
    public async Task LaunchAsync_RejectsNonHapOutputWithoutResolvingDeviceTools()
    {
        Directory.CreateDirectory(_testRoot);
        BuildSettings settings = CreateSettings();
        settings.GetProfile<HarmonyOSBuildProfile>(typeof(HarmonyOSBuildPipeline)).OutputMode = HarmonyOSOutputMode.App;
        var resolver = new FixtureToolchainResolver();
        var bridge = new FixtureDeviceBridge();

        BuildLaunchResult launch = await new HarmonyOSBuildPipeline(resolver, bridge).LaunchAsync(new BuildLaunchRequest
        {
            ProjectPath = _testRoot,
            Settings = settings,
            Build = new BuildResult { Success = true, OutputPath = Path.Combine(_testRoot, "Smoke.app") },
        });

        Assert.True(launch.Handled);
        Assert.False(launch.Success);
        Assert.Contains("HAP", launch.Message, StringComparison.Ordinal);
        Assert.False(resolver.Called);
        Assert.Null(bridge.Request);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private static BuildSettings CreateSettings()
    {
        var settings = new BuildSettings();
        HarmonyOSBuildProfile profile = settings.GetProfile<HarmonyOSBuildProfile>(typeof(HarmonyOSBuildPipeline));
        profile.OutputMode = HarmonyOSOutputMode.Hap;
        profile.TargetApi = 13;
        profile.TargetAbis = ["x86_64"];
        profile.ApplicationId = "com.xengine.smoke";
        profile.AbilityName = "MainAbility";
        profile.RunDeviceId = "serial-sensitive-123";
        profile.InstallMode = HarmonyOSBuildRunInstallMode.LaunchOnly;
        profile.CaptureRunScreenshot = true;
        return settings;
    }

    private static string ComputeSha256(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class FixtureToolchainResolver : IHarmonyOSToolchainResolver
    {
        internal bool Called { get; private set; }
        internal IReadOnlyList<HarmonyOSBundleCapability> RequiredCapabilities { get; private set; } = [];

        public Task<HarmonyOSToolchainSnapshot> ResolveAsync(
            HarmonyOSToolchainResolveOptions options,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<HarmonyOSHdcToolchainResolution> ResolveHdcAsync(
            HarmonyOSToolchainResolveOptions options,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            RequiredCapabilities = options.RequiredCapabilities;
            return Task.FromResult(new HarmonyOSHdcToolchainResolution
            {
                HdcExecutablePath = "fixture-hdc",
                Snapshot = new HarmonyOSToolchainSnapshot
                {
                    ReleaseId = "release-fixture",
                    BundleFingerprint = new string('a', 64),
                    ManifestSha256 = new string('e', 64),
                    SdkRootSha256 = new string('b', 64),
                    SdkFolderName = "13",
                    TargetApi = 13,
                    TargetAbi = "x86_64",
                    PackageHashes = ["OpenHarmony.NET.HapTooling:" + new string('1', 64)],
                },
            });
        }
    }

    private sealed class FixtureDeviceBridge : IHarmonyOSDeviceBridge
    {
        internal HarmonyOSDeviceRunRequest? Request { get; private set; }

        public Task<HarmonyOSDeviceRunResult> InstallAndLaunchAsync(
            HarmonyOSDeviceRunRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new HarmonyOSDeviceRunResult
            {
                DeviceId = "serial-sensitive-123",
                DeviceModel = "Fixture Emulator",
                DeviceAbi = "x86_64",
                DeviceApi = 13,
                DeploymentAction = HarmonyOSDeploymentAction.LaunchOnly,
                Ready = true,
                HdcVersion = "Ver: 3.1.0e",
                RedactedLogs = "XENGINE_HARMONYOS_READY",
                ScreenshotSha256 = new string('d', 64),
            });
        }
    }
}
