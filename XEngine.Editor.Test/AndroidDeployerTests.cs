// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build.Android;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class AndroidDeployerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "XEngineAndroidDeployer", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DeploysApkToTheSingleMatchingDeviceAndFiltersLogcat()
    {
        Directory.CreateDirectory(_root);
        string apk = Write("smoke.apk");
        var runner = new FakeAdbRunner();
        var deployer = new AndroidDeployer(runner);

        AndroidDeploymentResult result = await deployer.DeployAsync(
            apk,
            "com.xengine.smoke",
            "x86_64",
            requestedSerial: null,
            Path.Combine(_root, "adb.exe"),
            Path.Combine(_root, "java.exe"),
            Path.Combine(_root, "bundletool.jar"),
            TimeSpan.FromSeconds(10));

        Assert.Equal("emulator-5554", result.DeviceSerial);
        Assert.Equal("x86_64", result.DeviceAbi);
        Assert.Equal(["x86_64", "arm64-v8a"], result.DeviceAbis);
        Assert.Equal(16, result.BuildId.Length);
        Assert.Contains(result.BuildId, string.Join("\n", result.FilteredLogcat), StringComparison.Ordinal);
        Assert.Equal("com.xengine.smoke/crc64abc.MainActivity", result.Activity);
        Assert.Contains("Initialization complete", string.Join("\n", result.FilteredLogcat));
        Assert.True(runner.HasCall("install", "-r", apk));
        Assert.True(runner.HasCall("am", "start", "-W", "-S", "-n", result.Activity));
        Assert.True(runner.HasCall("logcat", "--pid", "4321"));
    }

    [Fact]
    public async Task RejectsAbiMismatchBeforeInstalling()
    {
        Directory.CreateDirectory(_root);
        string apk = Write("arm64.apk");
        var runner = new FakeAdbRunner();
        var deployer = new AndroidDeployer(runner);

        AndroidDeploymentException error = await Assert.ThrowsAsync<AndroidDeploymentException>(() =>
            deployer.DeployAsync(
                apk,
                "com.xengine.smoke",
                "mips64",
                requestedSerial: null,
                Path.Combine(_root, "adb.exe"),
                Path.Combine(_root, "java.exe"),
                Path.Combine(_root, "bundletool.jar"),
                TimeSpan.FromSeconds(10)));

        Assert.Contains("do not match", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(runner.HasCall("install"));
    }

    [Fact]
    public async Task AcceptsAnyPackageAbiReportedByTheDevice()
    {
        Directory.CreateDirectory(_root);
        string apk = Write("arm64.apk");
        var runner = new FakeAdbRunner();
        var deployer = new AndroidDeployer(runner);

        AndroidDeploymentResult result = await deployer.DeployAsync(
            apk,
            "com.xengine.smoke",
            ["arm64-v8a", "armeabi-v7a"],
            requestedSerial: null,
            Path.Combine(_root, "adb.exe"),
            Path.Combine(_root, "java.exe"),
            Path.Combine(_root, "bundletool.jar"),
            TimeSpan.FromSeconds(10));

        Assert.Equal(["x86_64", "arm64-v8a"], result.DeviceAbis);
        Assert.True(runner.HasCall("install", "-r", apk));
    }

    [Fact]
    public void DeviceSelectionRequiresAnExplicitSerialForMultipleOnlineDevices()
    {
        AndroidConnectedDevice[] devices =
        [
            new("emulator-5554", "device", "sdk", "Pixel", "1"),
            new("R5CT", "device", "raven", "Pixel_6", "2"),
        ];

        AndroidDeploymentException error = Assert.Throws<AndroidDeploymentException>(() =>
            AndroidDeployer.SelectDevice(devices, requestedSerial: null));

        Assert.Contains("Multiple Android devices", error.Message, StringComparison.Ordinal);
        Assert.Equal("R5CT", AndroidDeployer.SelectDevice(devices, "R5CT").Serial);
    }

    [Fact]
    public async Task InstallsAabDeviceSplitThroughBundletool()
    {
        Directory.CreateDirectory(_root);
        string aab = Write("smoke.aab");
        string java = Write("java.exe");
        string bundletool = Write("bundletool.jar");
        var runner = new FakeAdbRunner();
        var deployer = new AndroidDeployer(runner);

        AndroidDeploymentResult result = await deployer.DeployAsync(
            aab,
            "com.xengine.smoke",
            "x86_64",
            requestedSerial: "emulator-5554",
            Path.Combine(_root, "adb.exe"),
            java,
            bundletool,
            TimeSpan.FromSeconds(10));

        Assert.Equal("emulator-5554", result.DeviceSerial);
        Assert.True(runner.HasCall("build-apks", "--connected-device"));
        Assert.True(runner.HasCall("build-apks", "--device-id", "emulator-5554"));
        Assert.True(runner.HasCall("install-apks", "--adb"));
        Assert.True(runner.HasCall("install-apks", "--device-id", "emulator-5554"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Write(string fileName)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, "test");
        return path;
    }

    private sealed record ProcessCall(string ExecutablePath, string[] Arguments);

    private sealed class FakeAdbRunner : IAndroidToolchainProcessRunner
    {
        private readonly List<ProcessCall> _calls = [];

        public Task<AndroidToolchainProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copied = new string[arguments.Count];
            for (int i = 0; i < arguments.Count; i++)
                copied[i] = arguments[i];
            _calls.Add(new ProcessCall(executablePath, copied));
            CaptureBuildId(copied);

            string output = Contains(arguments, "devices")
                ? "List of devices attached\nemulator-5554 device product:sdk_gphone64_x86_64 model:Pixel_9 transport_id:1\n"
                : Contains(arguments, "getprop")
                    ? "x86_64,arm64-v8a\n"
                    : Contains(arguments, "pidof")
                        ? "4321\n"
                    : Contains(arguments, "resolve-activity")
                        ? "priority=0\ncom.xengine.smoke/crc64abc.MainActivity\n"
                        : Contains(arguments, "logcat")
                            ? "I/XEngine: build-id=" + _buildId + "\nI/XEngine: Initialization complete\nI/Other: ignored\n"
                            : "Success\n";
            return Task.FromResult(new AndroidToolchainProcessResult(0, 1, output, string.Empty));
        }

        internal bool HasCall(params string[] requiredArguments)
        {
            for (int i = 0; i < _calls.Count; i++)
            {
                ProcessCall call = _calls[i];
                bool matches = true;
                for (int requiredIndex = 0; requiredIndex < requiredArguments.Length; requiredIndex++)
                {
                    if (!Contains(call.Arguments, requiredArguments[requiredIndex]))
                    {
                        matches = false;
                        break;
                    }
                }
                if (matches)
                    return true;
            }
            return false;
        }

        private static bool Contains(IReadOnlyList<string> arguments, string value)
        {
            for (int i = 0; i < arguments.Count; i++)
            {
                if (string.Equals(arguments[i], value, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private string _buildId = "missing";

        private void CaptureBuildId(IReadOnlyList<string> arguments)
        {
            for (int i = 0; i + 1 < arguments.Count; i++)
            {
                if (string.Equals(arguments[i], "xengine.build-id", StringComparison.Ordinal))
                {
                    _buildId = arguments[i + 1];
                    return;
                }
            }
        }
    }
}
