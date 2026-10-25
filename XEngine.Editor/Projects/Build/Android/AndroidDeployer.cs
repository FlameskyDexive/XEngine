// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.Android;

internal sealed record AndroidConnectedDevice(
    string Serial,
    string State,
    string Product,
    string Model,
    string Transport);

internal sealed record AndroidDeploymentResult(
    string DeviceSerial,
    string DeviceAbi,
    IReadOnlyList<string> DeviceAbis,
    string Activity,
    string PackagePath,
    string BuildId,
    IReadOnlyList<string> FilteredLogcat,
    string Output);

internal sealed class AndroidDeploymentException : InvalidOperationException
{
    internal AndroidDeploymentException(string message) : base(message) { }
}

/// <summary>Installs and launches a verified Android package through the locked adb tool.</summary>
internal sealed class AndroidDeployer(IAndroidToolchainProcessRunner? processRunner = null)
{
    private const string BuildIdIntentExtra = "xengine.build-id";

    private readonly IAndroidToolchainProcessRunner _processRunner =
        processRunner ?? new AndroidToolchainProcessRunner();

    internal async Task<AndroidDeploymentResult> DeployAsync(
        string packagePath,
        string applicationId,
        string expectedAbi,
        string? requestedSerial,
        AndroidToolchainSnapshot toolchain,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAbi);
        return await DeployAsync(
            packagePath,
            applicationId,
            [expectedAbi],
            requestedSerial,
            toolchain,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AndroidDeploymentResult> DeployAsync(
        string packagePath,
        string applicationId,
        IReadOnlyList<string> expectedAbis,
        string? requestedSerial,
        AndroidToolchainSnapshot toolchain,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ValidateExpectedAbis(expectedAbis);
        ArgumentNullException.ThrowIfNull(toolchain);

        string adbPath = FindComponent(toolchain, "adb");
        string javaPath = Path.Combine(
            toolchain.JavaSdkRoot,
            "bin",
            OperatingSystem.IsWindows() ? "java.exe" : "java");
        string bundletoolPath = toolchain.BundletoolPath;
        return await DeployAsync(
            packagePath,
            applicationId,
            expectedAbis,
            requestedSerial,
            adbPath,
            javaPath,
            bundletoolPath,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AndroidDeploymentResult> DeployAsync(
        string packagePath,
        string applicationId,
        string expectedAbi,
        string? requestedSerial,
        string adbPath,
        string javaPath,
        string bundletoolPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAbi);
        return await DeployAsync(
            packagePath,
            applicationId,
            [expectedAbi],
            requestedSerial,
            adbPath,
            javaPath,
            bundletoolPath,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AndroidDeploymentResult> DeployAsync(
        string packagePath,
        string applicationId,
        IReadOnlyList<string> expectedAbis,
        string? requestedSerial,
        string adbPath,
        string javaPath,
        string bundletoolPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ValidateExpectedAbis(expectedAbis);
        ArgumentException.ThrowIfNullOrWhiteSpace(adbPath);
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Android deployment package is missing.", packagePath);

        string buildId = CreateBuildId(packagePath);
        IReadOnlyDictionary<string, string> environment = CreateEnvironment(adbPath);
        IReadOnlyList<AndroidConnectedDevice> devices = await ListDevicesAsync(
            adbPath,
            environment,
            timeout,
            cancellationToken).ConfigureAwait(false);
        AndroidConnectedDevice device = SelectDevice(devices, requestedSerial);

        IReadOnlyList<string> deviceAbis = await ReadDeviceAbisAsync(
            adbPath,
            device.Serial,
            environment,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (!AbiMatches(expectedAbis, deviceAbis))
        {
            throw new AndroidDeploymentException(
                $"Android device '{device.Serial}' ABI(s) '{FormatAbis(deviceAbis)}' do not match package ABI(s) " +
                $"'{FormatAbis(expectedAbis)}'. " +
                "Build the package for the connected device ABI or select a compatible device.");
        }

        string extension = Path.GetExtension(packagePath);
        string output;
        if (string.Equals(extension, ".apk", StringComparison.OrdinalIgnoreCase))
        {
            AndroidToolchainProcessResult install = await RunAdbAsync(
                adbPath,
                ["-s", device.Serial, "install", "-r", packagePath],
                environment,
                timeout,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(install, "adb install");
            output = install.CombinedOutput;
        }
        else if (string.Equals(extension, ".aab", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(javaPath) || !File.Exists(bundletoolPath))
                throw new AndroidDeploymentException(
                    "AAB deployment requires the locked Java and bundletool executables.");
            string apksPath = Path.Combine(
                Path.GetDirectoryName(packagePath)!,
                Path.GetFileNameWithoutExtension(packagePath) + "." + device.Serial + ".apks");
            try
            {
                AndroidToolchainProcessResult buildApks = await RunAsync(
                    javaPath,
                    [
                        "-jar", bundletoolPath, "build-apks",
                        "--bundle", packagePath,
                        "--output", apksPath,
                        "--connected-device",
                        "--device-id", device.Serial,
                        "--adb", adbPath,
                        "--overwrite",
                    ],
                    environment,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccess(buildApks, "bundletool build-apks");

                AndroidToolchainProcessResult installApks = await RunAsync(
                    javaPath,
                    [
                        "-jar", bundletoolPath, "install-apks",
                        "--apks", apksPath,
                        "--device-id", device.Serial,
                        "--adb", adbPath,
                    ],
                    environment,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccess(installApks, "bundletool install-apks");
                output = buildApks.CombinedOutput + Environment.NewLine + installApks.CombinedOutput;
            }
            finally
            {
                TryDelete(apksPath);
            }
        }
        else
        {
            throw new AndroidDeploymentException(
                $"Android deployment only supports APK and AAB artifacts, not '{extension}'.");
        }

        string activity = await ResolveActivityAsync(
            adbPath,
            device.Serial,
            applicationId,
            environment,
            timeout,
            cancellationToken).ConfigureAwait(false);
        AndroidToolchainProcessResult launch = await RunAdbAsync(
            adbPath,
            [
                "-s", device.Serial, "shell", "am", "start", "-W", "-S", "-n", activity,
                "--es", BuildIdIntentExtra, buildId,
            ],
            environment,
            timeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(launch, "adb launch");

        string processId = await ResolveProcessIdAsync(
            adbPath,
            device.Serial,
            applicationId,
            environment,
            timeout,
            cancellationToken).ConfigureAwait(false);

        AndroidToolchainProcessResult logcat = await RunAdbAsync(
            adbPath,
            ["-s", device.Serial, "logcat", "-d", "-v", "brief", "--pid", processId],
            environment,
            timeout,
            cancellationToken).ConfigureAwait(false);
        string[] filtered = FilterLogcat(logcat.CombinedOutput, applicationId, buildId);
        if (!ContainsBuildId(filtered, buildId))
        {
            throw new AndroidDeploymentException(
                $"Android package '{applicationId}' launched but did not report build id '{buildId}' from process {processId}. " +
                "The generated Android Activity must log the deployment build id before Build & Run can succeed.");
        }
        return new AndroidDeploymentResult(
            device.Serial,
            deviceAbis[0],
            deviceAbis,
            activity,
            packagePath,
            buildId,
            filtered,
            output + Environment.NewLine + launch.CombinedOutput);
    }

    internal async Task<IReadOnlyList<AndroidConnectedDevice>> ListDevicesAsync(
        string adbPath,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        AndroidToolchainProcessResult result = await RunAsync(
            adbPath,
            ["devices", "-l"],
            environment,
            timeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "adb devices");

        var devices = new List<AndroidConnectedDevice>();
        foreach (string rawLine in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                continue;
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
                continue;

            string product = FindField(fields, "product:");
            string model = FindField(fields, "model:");
            string transport = FindField(fields, "transport_id:");
            devices.Add(new AndroidConnectedDevice(fields[0], fields[1], product, model, transport));
        }
        return devices;
    }

    internal static AndroidConnectedDevice SelectDevice(
        IReadOnlyList<AndroidConnectedDevice> devices,
        string? requestedSerial)
    {
        var online = new List<AndroidConnectedDevice>();
        AndroidConnectedDevice? requested = null;
        for (int i = 0; i < devices.Count; i++)
        {
            AndroidConnectedDevice device = devices[i];
            if (string.Equals(device.State, "device", StringComparison.OrdinalIgnoreCase))
                online.Add(device);
            if (!string.IsNullOrWhiteSpace(requestedSerial) &&
                string.Equals(device.Serial, requestedSerial, StringComparison.Ordinal))
            {
                requested = device;
            }
        }
        if (!string.IsNullOrWhiteSpace(requestedSerial))
        {
            if (requested == null)
                throw new AndroidDeploymentException(
                    $"Requested Android device '{requestedSerial}' was not found. " +
                    DescribeDevices(devices));
            if (!string.Equals(requested.State, "device", StringComparison.OrdinalIgnoreCase))
                throw new AndroidDeploymentException(
                    $"Requested Android device '{requestedSerial}' is '{requested.State}', not ready. " +
                    DescribeDevices(devices));
            return requested;
        }

        return online.Count switch
        {
            0 => throw new AndroidDeploymentException(
                "No online Android device is connected. " + DescribeDevices(devices)),
            1 => online[0],
            _ => throw new AndroidDeploymentException(
                "Multiple Android devices are connected; set an explicit device serial. " +
                DescribeDevices(online)),
        };
    }

    private async Task<IReadOnlyList<string>> ReadDeviceAbisAsync(
        string adbPath,
        string serial,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        AndroidToolchainProcessResult result = await RunAdbAsync(
            adbPath,
            ["-s", serial, "shell", "getprop", "ro.product.cpu.abilist"],
            environment,
            timeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "adb read device ABI");
        string[] abis = ReadAbis(result.StandardOutput);
        if (abis.Length == 0)
        {
            result = await RunAdbAsync(
                adbPath,
                ["-s", serial, "shell", "getprop", "ro.product.cpu.abi"],
                environment,
                timeout,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "adb read device ABI");
            abis = ReadAbis(result.StandardOutput);
        }
        if (abis.Length == 0)
            throw new AndroidDeploymentException($"Android device '{serial}' did not report a CPU ABI.");
        return abis;
    }

    private async Task<string> ResolveActivityAsync(
        string adbPath,
        string serial,
        string applicationId,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        AndroidToolchainProcessResult result = await RunAdbAsync(
            adbPath,
            ["-s", serial, "shell", "cmd", "package", "resolve-activity", "--brief", "--user", "0", applicationId],
            environment,
            timeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "adb resolve activity");
        string? activity = null;
        string[] lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            string candidate = lines[i].Trim();
            if (candidate.StartsWith(applicationId + "/", StringComparison.Ordinal))
            {
                activity = candidate;
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(activity))
            throw new AndroidDeploymentException(
                $"No launchable Activity was found for Android package '{applicationId}'.");
        return activity;
    }

    private async Task<string> ResolveProcessIdAsync(
        string adbPath,
        string serial,
        string applicationId,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        AndroidToolchainProcessResult result = await RunAdbAsync(
            adbPath,
            ["-s", serial, "shell", "pidof", applicationId],
            environment,
            timeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "adb resolve app process");
        string[] processIds = result.StandardOutput.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (processIds.Length == 0)
            throw new AndroidDeploymentException(
                $"Android package '{applicationId}' launched but did not expose a running process for logcat filtering.");
        return processIds[0];
    }

    private async Task<AndroidToolchainProcessResult> RunAdbAsync(
        string adbPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => await RunAsync(adbPath, arguments, environment, timeout, cancellationToken).ConfigureAwait(false);

    private async Task<AndroidToolchainProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => await _processRunner.RunAsync(
            executablePath,
            arguments,
            Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
            environment,
            timeout,
            cancellationToken).ConfigureAwait(false);

    private static string FindComponent(AndroidToolchainSnapshot toolchain, string id)
    {
        for (int i = 0; i < toolchain.Components.Count; i++)
        {
            AndroidToolchainComponentLock component = toolchain.Components[i];
            if (string.Equals(component.Id, id, StringComparison.OrdinalIgnoreCase))
                return component.ResolvedPath;
        }
        throw new AndroidDeploymentException($"Locked Android toolchain component '{id}' is missing.");
    }

    private static IReadOnlyDictionary<string, string> CreateEnvironment(string adbPath)
    {
        string sdkRoot = Directory.GetParent(Path.GetDirectoryName(adbPath)!)?.FullName ?? string.Empty;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ANDROID_HOME"] = sdkRoot,
            ["ANDROID_SDK_ROOT"] = sdkRoot,
        };
    }

    private static void EnsureSuccess(AndroidToolchainProcessResult result, string operation)
    {
        if (result.ExitCode == 0)
            return;
        throw new AndroidDeploymentException(
            $"{operation} failed with exit code {result.ExitCode}: {Tail(result.CombinedOutput)}");
    }

    private static string[] FilterLogcat(string output, string applicationId, string buildId)
    {
        var lines = new List<string>();
        string[] source = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < source.Length; i++)
        {
            string line = source[i];
            if (
                line.Contains(applicationId, StringComparison.OrdinalIgnoreCase) ||
                line.Contains("XEngine", StringComparison.OrdinalIgnoreCase) ||
                line.Contains(buildId, StringComparison.Ordinal) ||
                line.Contains("AndroidRuntime", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("FATAL EXCEPTION", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add(line);
            }
        }
        int first = Math.Max(0, lines.Count - 200);
        if (first == 0)
            return lines.ToArray();
        string[] tail = new string[lines.Count - first];
        lines.CopyTo(first, tail, 0, tail.Length);
        return tail;
    }

    private static bool ContainsBuildId(IReadOnlyList<string> lines, string buildId)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains(buildId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool AbiMatches(IReadOnlyList<string> expectedAbis, IReadOnlyList<string> deviceAbis)
    {
        for (int expectedIndex = 0; expectedIndex < expectedAbis.Count; expectedIndex++)
        {
            string expectedAbi = expectedAbis[expectedIndex];
            for (int deviceIndex = 0; deviceIndex < deviceAbis.Count; deviceIndex++)
            {
                if (string.Equals(expectedAbi, deviceAbis[deviceIndex], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static void ValidateExpectedAbis(IReadOnlyList<string> expectedAbis)
    {
        ArgumentNullException.ThrowIfNull(expectedAbis);
        for (int i = 0; i < expectedAbis.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(expectedAbis[i]))
                return;
        }
        throw new ArgumentException("At least one Android package ABI is required.", nameof(expectedAbis));
    }

    private static string FindField(IReadOnlyList<string> fields, string prefix)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return fields[i][prefix.Length..];
        }
        return string.Empty;
    }

    private static string DescribeDevices(IEnumerable<AndroidConnectedDevice> devices)
    {
        var descriptions = new List<string>();
        foreach (AndroidConnectedDevice device in devices)
        {
            descriptions.Add(
                $"{device.Serial} state={device.State}" +
                (string.IsNullOrWhiteSpace(device.Model) ? string.Empty : $" model={device.Model}"));
        }
        return descriptions.Count == 0
            ? "adb reported no devices."
            : "Devices: " + string.Join(", ", descriptions) + ".";
    }

    private static string[] ReadAbis(string value)
    {
        string[] values = value.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var abis = new List<string>(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            string abi = values[i].Trim();
            if (abi.Length > 0 && !ContainsAbi(abis, abi))
                abis.Add(abi);
        }
        return abis.ToArray();
    }

    private static bool ContainsAbi(IReadOnlyList<string> abis, string abi)
    {
        for (int i = 0; i < abis.Count; i++)
        {
            if (string.Equals(abis[i], abi, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string FormatAbis(IReadOnlyList<string> abis) => string.Join(", ", abis);

    private static string CreateBuildId(string packagePath)
    {
        using FileStream stream = File.OpenRead(packagePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash)[..16];
    }

    private static string Tail(string value)
    {
        const int max = 2000;
        value = value.Trim();
        return value.Length <= max ? value : value[^max..];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
