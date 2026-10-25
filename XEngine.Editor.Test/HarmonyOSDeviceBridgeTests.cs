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

public sealed class HarmonyOSDeviceBridgeTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineHarmonyOSDeviceBridgeTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallAndLaunchAsync_SelectsRequestedCompatibleDevice()
    {
        string hap = CreateHap();
        var runner = new HdcFixtureRunner();
        runner.Add("-v", "Ver: 3.1.0e");
        AddTwoDevices(runner);
        runner.Add("-t emulator-13 shell bm dump -n com.xengine.smoke", "error: bundle not found");
        runner.Add("-t emulator-13 install -r " + hap, "msg:install bundle successfully.");
        runner.Add("-t emulator-13 shell hilog -r", "");
        runner.Add("-t emulator-13 shell aa start -a EntryAbility -b com.xengine.smoke", "start ability successfully.");
        runner.Add("-t emulator-13 hilog", "I/XEngine: XENGINE_HARMONYOS_READY abi=2", completionMarkerFound: true);

        HarmonyOSDeviceRunResult result = await new HarmonyOSDeviceBridge(runner).InstallAndLaunchAsync(
            CreateRequest(hap, requestedDeviceId: "emulator-13"));

        Assert.Equal("emulator-13", result.DeviceId);
        Assert.Equal("x86_64", result.DeviceAbi);
        Assert.Equal(13, result.DeviceApi);
        Assert.Equal(HarmonyOSDeploymentAction.Installed, result.DeploymentAction);
        Assert.True(result.Ready);
        Assert.Equal("Ver: 3.1.0e", result.HdcVersion);
    }

    [Fact]
    public async Task InstallAndLaunchAsync_RejectsAmbiguousCompatibleDevices()
    {
        string hap = CreateHap();
        var runner = new HdcFixtureRunner();
        runner.Add("-v", "Ver: 3.1.0e");
        runner.Add("list targets -v", "emulator-a USB Connected localhost hdc\nemulator-b USB Connected localhost hdc");
        AddDeviceProperties(runner, "emulator-a", "x86_64", 13, "Emulator A");
        AddDeviceProperties(runner, "emulator-b", "x86_64", 26, "Emulator B");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HarmonyOSDeviceBridge(runner).InstallAndLaunchAsync(CreateRequest(hap)));

        Assert.Contains("multiple compatible", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Commands, command => command.Contains(" install ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAndLaunchAsync_RejectsRequestedIncompatibleDevice()
    {
        string hap = CreateHap();
        var runner = new HdcFixtureRunner();
        runner.Add("-v", "Ver: 3.1.0e");
        AddTwoDevices(runner);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HarmonyOSDeviceBridge(runner).InstallAndLaunchAsync(
                CreateRequest(hap, requestedDeviceId: "phone-26")));

        Assert.Contains("not compatible", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Commands, command => command.Contains(" install ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAndLaunchAsync_TreatsHdcFailureTextAsFailureEvenWithZeroExitCode()
    {
        string hap = CreateHap();
        var runner = new HdcFixtureRunner();
        runner.Add("-v", "Ver: 3.1.0e");
        runner.Add("list targets -v", "[Fail]ExecuteCommand need connect-key?", exitCode: 0);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HarmonyOSDeviceBridge(runner).InstallAndLaunchAsync(CreateRequest(hap)));

        Assert.Contains("list connected targets", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAndLaunchAsync_ReplacesExistingApplicationAndStopsItFirst()
    {
        string hap = CreateHap();
        var runner = new HdcFixtureRunner();
        runner.Add("-v", "Ver: 3.1.0e");
        runner.Add("list targets -v", "emulator-13 USB Connected localhost hdc");
        AddDeviceProperties(runner, "emulator-13", "x86_64", 13, "OpenHarmony Emulator");
        runner.Add("-t emulator-13 shell bm dump -n com.xengine.smoke", "bundleName: com.xengine.smoke");
        runner.Add("-t emulator-13 shell aa force-stop com.xengine.smoke", "force stop process successfully.");
        runner.Add("-t emulator-13 install -r " + hap, "msg:install bundle successfully.");
        runner.Add("-t emulator-13 shell hilog -r", "");
        runner.Add("-t emulator-13 shell aa start -a EntryAbility -b com.xengine.smoke", "start ability successfully.");
        runner.Add("-t emulator-13 hilog", "XENGINE_HARMONYOS_READY", completionMarkerFound: true);

        HarmonyOSDeviceRunResult result = await new HarmonyOSDeviceBridge(runner)
            .InstallAndLaunchAsync(CreateRequest(hap, requestedDeviceId: "emulator-13"));

        Assert.Equal(HarmonyOSDeploymentAction.Replaced, result.DeploymentAction);
        Assert.True(runner.Commands.IndexOf("-t emulator-13 shell aa force-stop com.xengine.smoke") <
                    runner.Commands.IndexOf("-t emulator-13 install -r " + hap));
    }

    [Fact]
    public async Task InstallAndLaunchAsync_LaunchOnlyDoesNotInstallOrUninstall()
    {
        string hap = CreateHap();
        var runner = new HdcFixtureRunner();
        runner.Add("-v", "Ver: 3.1.0e");
        runner.Add("list targets -v", "emulator-13 USB Connected localhost hdc");
        AddDeviceProperties(runner, "emulator-13", "x86_64", 13, "OpenHarmony Emulator");
        runner.Add("-t emulator-13 shell bm dump -n com.xengine.smoke", "bundleName: com.xengine.smoke");
        runner.Add("-t emulator-13 shell aa force-stop com.xengine.smoke", "force stop process successfully.");
        runner.Add("-t emulator-13 shell hilog -r", "");
        runner.Add("-t emulator-13 shell aa start -a EntryAbility -b com.xengine.smoke", "start ability successfully.");
        runner.Add("-t emulator-13 hilog", "XENGINE_HARMONYOS_READY", completionMarkerFound: true);
        HarmonyOSDeviceRunRequest request = CreateRequest(hap, requestedDeviceId: "emulator-13");
        request = CopyWith(request, HarmonyOSInstallPolicy.LaunchOnly, ownsTestInstallation: true);

        HarmonyOSDeviceRunResult result = await new HarmonyOSDeviceBridge(runner).InstallAndLaunchAsync(request);

        Assert.Equal(HarmonyOSDeploymentAction.LaunchOnly, result.DeploymentAction);
        Assert.DoesNotContain(runner.Commands, command => command.Contains(" install ", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Commands, command => command.Contains(" uninstall ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAndLaunchAsync_ReadinessFailureCleansOnlyOwnedNewInstallation()
    {
        string hap = CreateHap();
        var runner = new HdcFixtureRunner();
        runner.Add("-v", "Ver: 3.1.0e");
        runner.Add("list targets -v", "emulator-13 USB Connected localhost hdc");
        AddDeviceProperties(runner, "emulator-13", "x86_64", 13, "OpenHarmony Emulator");
        runner.Add("-t emulator-13 shell bm dump -n com.xengine.smoke", "error: bundle not found");
        runner.Add("-t emulator-13 install -r " + hap, "msg:install bundle successfully.");
        runner.Add("-t emulator-13 shell hilog -r", "");
        runner.Add("-t emulator-13 shell aa start -a EntryAbility -b com.xengine.smoke", "start ability successfully.");
        runner.Add("-t emulator-13 hilog", "application started without marker");
        runner.Add("-t emulator-13 shell aa force-stop com.xengine.smoke", "force stop process successfully.");
        runner.Add("-t emulator-13 uninstall com.xengine.smoke", "msg:uninstall bundle successfully.");
        HarmonyOSDeviceRunRequest request = CopyWith(
            CreateRequest(hap, requestedDeviceId: "emulator-13"),
            HarmonyOSInstallPolicy.InstallOrReplace,
            ownsTestInstallation: true);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            new HarmonyOSDeviceBridge(runner).InstallAndLaunchAsync(request));

        Assert.Contains("-t emulator-13 uninstall com.xengine.smoke", runner.Commands);
    }

    [Fact]
    public async Task InstallAndLaunchAsync_PropagatesCancellationWithoutAResult()
    {
        string hap = CreateHap();
        var runner = new HdcFixtureRunner { BlockUntilCancelled = true };
        using var cancellation = new CancellationTokenSource();

        Task<HarmonyOSDeviceRunResult> run = new HarmonyOSDeviceBridge(runner)
            .InstallAndLaunchAsync(CreateRequest(hap), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(runner.CancellationObserved);
    }

    [Fact]
    public async Task InstallAndLaunchAsync_CapturesScreenshotAndReturnsItsHash()
    {
        string hap = CreateHap();
        string screenshot = Path.Combine(_testRoot, "evidence", "run.png");
        byte[] screenshotContents = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];
        var runner = new HdcFixtureRunner { ScreenshotContents = screenshotContents };
        AddSuccessfulScreenshotRun(runner, hap, screenshot);

        HarmonyOSDeviceRunResult result = await new HarmonyOSDeviceBridge(runner).InstallAndLaunchAsync(
            CreateRequest(hap, requestedDeviceId: "emulator-13", screenshotOutputPath: screenshot));

        string expectedHash = Convert.ToHexString(SHA256.HashData(screenshotContents)).ToLowerInvariant();
        Assert.Equal(expectedHash, result.ScreenshotSha256);
        Assert.True(File.Exists(screenshot));
    }

    [Fact]
    public async Task InstallAndLaunchAsync_RejectsTransferredFileWithoutPngSignature()
    {
        string hap = CreateHap();
        string screenshot = Path.Combine(_testRoot, "run.png");
        var runner = new HdcFixtureRunner { ScreenshotContents = Encoding.UTF8.GetBytes("not-a-png") };
        AddSuccessfulScreenshotRun(runner, hap, screenshot);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HarmonyOSDeviceBridge(runner).InstallAndLaunchAsync(
                CreateRequest(hap, requestedDeviceId: "emulator-13", screenshotOutputPath: screenshot)));

        Assert.Contains("PNG", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAndLaunchAsync_DoesNotAcceptStaleLocalScreenshotAsTransferEvidence()
    {
        string hap = CreateHap();
        string screenshot = Path.Combine(_testRoot, "run.png");
        await File.WriteAllTextAsync(screenshot, "stale");
        var runner = new HdcFixtureRunner();
        AddSuccessfulScreenshotRun(runner, hap, screenshot);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new HarmonyOSDeviceBridge(runner).InstallAndLaunchAsync(
                CreateRequest(hap, requestedDeviceId: "emulator-13", screenshotOutputPath: screenshot)));
    }

    [Fact]
    public async Task InstallAndLaunchAsync_RejectsEmptyHdcVersion()
    {
        string hap = CreateHap();
        var runner = new HdcFixtureRunner();
        runner.Add("-v", "");

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new HarmonyOSDeviceBridge(runner).InstallAndLaunchAsync(CreateRequest(hap)));

        Assert.Contains("empty version", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("list targets -v", runner.Commands);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsUnsupportedFutureApi()
    {
        string hap = CreateHap();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new HarmonyOSDeviceBridge(new HdcFixtureRunner()).DiscoverAsync(CreateRequest(hap, targetApi: 27)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private string CreateHap()
    {
        Directory.CreateDirectory(_testRoot);
        string path = Path.Combine(_testRoot, "Smoke.hap");
        File.WriteAllText(path, "fixture");
        return path;
    }

    private HarmonyOSDeviceRunRequest CreateRequest(
        string hap,
        string? requestedDeviceId = null,
        int targetApi = 13,
        string? screenshotOutputPath = null) => new()
    {
        HdcExecutablePath = "hdc",
        WorkingDirectory = _testRoot,
        HapPath = hap,
        ApplicationId = "com.xengine.smoke",
        AbilityName = "EntryAbility",
        TargetAbi = "x86_64",
        TargetApi = targetApi,
        RequestedDeviceId = requestedDeviceId,
        ReadinessTimeout = TimeSpan.FromSeconds(5),
        ScreenshotOutputPath = screenshotOutputPath,
    };

    private static HarmonyOSDeviceRunRequest CopyWith(
        HarmonyOSDeviceRunRequest value,
        HarmonyOSInstallPolicy installPolicy,
        bool ownsTestInstallation) => new()
    {
        HdcExecutablePath = value.HdcExecutablePath,
        WorkingDirectory = value.WorkingDirectory,
        HapPath = value.HapPath,
        ApplicationId = value.ApplicationId,
        AbilityName = value.AbilityName,
        TargetAbi = value.TargetAbi,
        TargetApi = value.TargetApi,
        RequestedDeviceId = value.RequestedDeviceId,
        InstallPolicy = installPolicy,
        ReadinessMarker = value.ReadinessMarker,
        CommandTimeout = value.CommandTimeout,
        ReadinessTimeout = value.ReadinessTimeout,
        OwnsTestInstallation = ownsTestInstallation,
    };

    private static void AddTwoDevices(HdcFixtureRunner runner)
    {
        runner.Add(
            "list targets -v",
            "emulator-13 USB Connected localhost hdc\nphone-26 USB Connected localhost hdc");
        AddDeviceProperties(runner, "emulator-13", "x86_64", 13, "OpenHarmony Emulator");
        AddDeviceProperties(runner, "phone-26", "arm64-v8a", 26, "OpenHarmony Phone");
    }

    private static void AddSuccessfulScreenshotRun(HdcFixtureRunner runner, string hap, string screenshot)
    {
        runner.Add("-v", "Ver: 3.1.0e");
        runner.Add("list targets -v", "emulator-13 USB Connected localhost hdc");
        AddDeviceProperties(runner, "emulator-13", "x86_64", 13, "OpenHarmony Emulator");
        runner.Add("-t emulator-13 shell bm dump -n com.xengine.smoke", "error: bundle not found");
        runner.Add("-t emulator-13 install -r " + hap, "msg:install bundle successfully.");
        runner.Add("-t emulator-13 shell hilog -r", "");
        runner.Add("-t emulator-13 shell aa start -a EntryAbility -b com.xengine.smoke", "start ability successfully.");
        runner.Add("-t emulator-13 hilog", "XENGINE_HARMONYOS_READY", completionMarkerFound: true);
        runner.Add("-t emulator-13 shell snapshot_display", "Snapshot saved to /data/local/tmp/snapshot.png");
        runner.Add("-t emulator-13 file recv /data/local/tmp/snapshot.png " + screenshot, "FileTransfer finish");
    }

    private static void AddDeviceProperties(
        HdcFixtureRunner runner,
        string deviceId,
        string abi,
        int api,
        string model)
    {
        runner.Add("-t " + deviceId + " shell param get const.product.cpu.abilist", abi);
        runner.Add("-t " + deviceId + " shell param get const.ohos.apiversion", api.ToString(System.Globalization.CultureInfo.InvariantCulture));
        runner.Add("-t " + deviceId + " shell param get const.product.model", model);
    }

    private sealed class HdcFixtureRunner : IHarmonyOSProcessRunner
    {
        private readonly Dictionary<string, Queue<HarmonyOSProcessResult>> _results = new(StringComparer.Ordinal);

        internal List<string> Commands { get; } = [];
        internal bool BlockUntilCancelled { get; init; }
        internal bool CancellationObserved { get; private set; }
        internal byte[]? ScreenshotContents { get; init; }

        internal void Add(
            string command,
            string standardOutput,
            int exitCode = 0,
            bool completionMarkerFound = false)
        {
            if (!_results.TryGetValue(command, out Queue<HarmonyOSProcessResult>? queue))
            {
                queue = new Queue<HarmonyOSProcessResult>();
                _results.Add(command, queue);
            }
            queue.Enqueue(new HarmonyOSProcessResult(
                exitCode,
                1,
                standardOutput,
                string.Empty,
                command,
                TimedOut: false,
                OutputWasTruncated: false,
                CompletionMarkerFound: completionMarkerFound));
        }

        public async Task<HarmonyOSProcessResult> RunAsync(
            HarmonyOSProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            string key = string.Join(' ', command.Arguments);
            Commands.Add(key);
            if (BlockUntilCancelled)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }
            if (!_results.TryGetValue(key, out Queue<HarmonyOSProcessResult>? queue) || queue.Count == 0)
                throw new InvalidOperationException("Unexpected HDC fixture command: " + key);
            if (ScreenshotContents != null && command.Arguments.Count >= 2 &&
                command.Arguments[^2].EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                string screenshotPath = command.Arguments[^1];
                Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
                await File.WriteAllBytesAsync(screenshotPath, ScreenshotContents, cancellationToken);
            }
            return queue.Dequeue();
        }
    }
}
