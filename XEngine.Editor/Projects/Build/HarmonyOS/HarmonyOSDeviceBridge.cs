// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.HarmonyOS;

internal interface IHarmonyOSDeviceBridge
{
    Task<HarmonyOSDeviceRunResult> InstallAndLaunchAsync(
        HarmonyOSDeviceRunRequest request,
        CancellationToken cancellationToken = default);
}

internal enum HarmonyOSDeploymentAction
{
    Installed,
    Replaced,
    LaunchOnly,
}

internal enum HarmonyOSInstallPolicy
{
    InstallOrReplace,
    LaunchOnly,
}

internal sealed class HarmonyOSDeviceInfo
{
    public string DeviceId { get; init; } = string.Empty;
    public string ConnectionState { get; init; } = string.Empty;
    public string Transport { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Abi { get; init; } = string.Empty;
    public int ApiLevel { get; init; }
    public bool IsConnected { get; init; }
}

internal sealed class HarmonyOSDeviceRunRequest
{
    public string HdcExecutablePath { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public string HapPath { get; init; } = string.Empty;
    public string ApplicationId { get; init; } = string.Empty;
    public string AbilityName { get; init; } = "EntryAbility";
    public string TargetAbi { get; init; } = string.Empty;
    public int TargetApi { get; init; }
    public string? RequestedDeviceId { get; init; }
    public HarmonyOSInstallPolicy InstallPolicy { get; init; } = HarmonyOSInstallPolicy.InstallOrReplace;
    public string ReadinessMarker { get; init; } = "XENGINE_HARMONYOS_READY";
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool OwnsTestInstallation { get; init; }
    public string? ScreenshotOutputPath { get; init; }
    public IReadOnlyList<string> Redactions { get; init; } = [];
}

internal sealed class HarmonyOSDeviceRunResult
{
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceModel { get; init; } = string.Empty;
    public string DeviceAbi { get; init; } = string.Empty;
    public int DeviceApi { get; init; }
    public HarmonyOSDeploymentAction DeploymentAction { get; init; }
    public bool Ready { get; init; }
    public string HdcVersion { get; init; } = string.Empty;
    public string RedactedLogs { get; init; } = string.Empty;
    public string? ScreenshotSha256 { get; init; }
}

/// <summary>
/// Device-aware HDC deployment that never chooses an ambiguous target and never treats HDC's
/// historical zero exit code as sufficient proof of success.
/// </summary>
internal sealed class HarmonyOSDeviceBridge(IHarmonyOSProcessRunner? processRunner = null) : IHarmonyOSDeviceBridge
{
    private static readonly byte[] s_pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly TimeSpan s_cleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly IHarmonyOSProcessRunner _processRunner = processRunner ?? new HarmonyOSProcessRunner();

    internal async Task<IReadOnlyList<HarmonyOSDeviceInfo>> DiscoverAsync(
        HarmonyOSDeviceRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, requireHap: false);
        HarmonyOSProcessResult targets = await RunAsync(
            request,
            ["list", "targets", "-v"],
            request.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccessful(targets, "list connected targets");

        IReadOnlyList<TargetLine> parsed = ParseTargets(targets.StandardOutput);
        var devices = new List<HarmonyOSDeviceInfo>(parsed.Count);
        for (int i = 0; i < parsed.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TargetLine target = parsed[i];
            if (!target.IsConnected)
            {
                devices.Add(new HarmonyOSDeviceInfo
                {
                    DeviceId = target.DeviceId,
                    ConnectionState = target.ConnectionState,
                    Transport = target.Transport,
                    Model = target.Model,
                    IsConnected = false,
                });
                continue;
            }

            string abi = NormalizeAbi(await ReadPropertyAsync(
                request,
                target.DeviceId,
                "const.product.cpu.abilist",
                cancellationToken).ConfigureAwait(false));
            string apiText = await ReadPropertyAsync(
                request,
                target.DeviceId,
                "const.ohos.apiversion",
                cancellationToken).ConfigureAwait(false);
            if (!TryParseApi(apiText, out int apiLevel))
                throw new InvalidDataException("A connected HarmonyOS target returned an invalid API level.");
            string model = target.Model;
            if (string.IsNullOrWhiteSpace(model))
            {
                model = await ReadPropertyAsync(
                    request,
                    target.DeviceId,
                    "const.product.model",
                    cancellationToken).ConfigureAwait(false);
            }

            devices.Add(new HarmonyOSDeviceInfo
            {
                DeviceId = target.DeviceId,
                ConnectionState = target.ConnectionState,
                Transport = target.Transport,
                Model = SingleLine(model),
                Abi = abi,
                ApiLevel = apiLevel,
                IsConnected = true,
            });
        }
        return devices.AsReadOnly();
    }

    public async Task<HarmonyOSDeviceRunResult> InstallAndLaunchAsync(
        HarmonyOSDeviceRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, requireHap: request.InstallPolicy != HarmonyOSInstallPolicy.LaunchOnly);
        string hdcVersion = SingleLine((await RunRequiredAsync(
            request,
            ["-v"],
            request.CommandTimeout,
            "query the HDC version",
            cancellationToken).ConfigureAwait(false)).StandardOutput);
        if (string.IsNullOrWhiteSpace(hdcVersion))
            throw new InvalidDataException("HDC returned an empty version response.");
        IReadOnlyList<HarmonyOSDeviceInfo> devices = await DiscoverAsync(request, cancellationToken).ConfigureAwait(false);
        HarmonyOSDeviceInfo device = SelectDevice(devices, request);

        bool installedBefore = await IsInstalledAsync(request, device.DeviceId, cancellationToken).ConfigureAwait(false);
        bool installedByRun = false;
        HarmonyOSDeploymentAction action;
        try
        {
            if (request.InstallPolicy == HarmonyOSInstallPolicy.LaunchOnly)
            {
                if (!installedBefore)
                    throw new InvalidOperationException("HarmonyOS launch-only mode requires the application to be installed already.");
                action = HarmonyOSDeploymentAction.LaunchOnly;
                await StopAsync(request, device.DeviceId, required: false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                action = installedBefore ? HarmonyOSDeploymentAction.Replaced : HarmonyOSDeploymentAction.Installed;
                if (installedBefore)
                    await StopAsync(request, device.DeviceId, required: false, cancellationToken).ConfigureAwait(false);
                await RunRequiredAsync(
                    request,
                    Target(device.DeviceId, "install", "-r", Path.GetFullPath(request.HapPath)),
                    request.CommandTimeout,
                    "install the HarmonyOS HAP",
                    cancellationToken).ConfigureAwait(false);
                installedByRun = !installedBefore;
            }

            await RunRequiredAsync(
                request,
                Target(device.DeviceId, "shell", "hilog", "-r"),
                request.CommandTimeout,
                "clear stale HarmonyOS logs",
                cancellationToken).ConfigureAwait(false);
            await RunRequiredAsync(
                request,
                Target(device.DeviceId, "shell", "aa", "start", "-a", request.AbilityName, "-b", request.ApplicationId),
                request.CommandTimeout,
                "start the HarmonyOS entry ability",
                cancellationToken).ConfigureAwait(false);

            HarmonyOSProcessResult readiness = await RunAsync(
                request,
                Target(device.DeviceId, "hilog"),
                request.ReadinessTimeout,
                cancellationToken,
                request.ReadinessMarker).ConfigureAwait(false);
            bool ready = readiness.CompletionMarkerFound ||
                readiness.CombinedOutput.Contains(request.ReadinessMarker, StringComparison.Ordinal);
            if (!ready)
                throw new TimeoutException("The HarmonyOS application did not report its readiness marker before the timeout.");

            string? screenshotSha256 = await CaptureScreenshotAsync(
                request,
                device.DeviceId,
                cancellationToken).ConfigureAwait(false);
            return new HarmonyOSDeviceRunResult
            {
                DeviceId = device.DeviceId,
                DeviceModel = device.Model,
                DeviceAbi = device.Abi,
                DeviceApi = device.ApiLevel,
                DeploymentAction = action,
                Ready = true,
                HdcVersion = hdcVersion,
                RedactedLogs = readiness.CombinedOutput,
                ScreenshotSha256 = screenshotSha256,
            };
        }
        catch
        {
            if (request.OwnsTestInstallation && installedByRun)
                await CleanupOwnedInstallationAsync(request, device.DeviceId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> IsInstalledAsync(
        HarmonyOSDeviceRunRequest request,
        string deviceId,
        CancellationToken cancellationToken)
    {
        HarmonyOSProcessResult result = await RunAsync(
            request,
            Target(deviceId, "shell", "bm", "dump", "-n", request.ApplicationId),
            request.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (ContainsNotFound(result.CombinedOutput))
            return false;
        EnsureSuccessful(result, "query the installed HarmonyOS application");
        if (!result.CombinedOutput.Contains(request.ApplicationId, StringComparison.Ordinal))
            throw new InvalidDataException("HDC returned an unrecognized bundle-manager response.");
        return true;
    }

    private async Task StopAsync(
        HarmonyOSDeviceRunRequest request,
        string deviceId,
        bool required,
        CancellationToken cancellationToken)
    {
        HarmonyOSProcessResult result = await RunAsync(
            request,
            Target(deviceId, "shell", "aa", "force-stop", request.ApplicationId),
            request.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (required)
            EnsureSuccessful(result, "stop the running HarmonyOS application");
    }

    private async Task CleanupOwnedInstallationAsync(HarmonyOSDeviceRunRequest request, string deviceId)
    {
        try
        {
            await RunAsync(
                request,
                Target(deviceId, "shell", "aa", "force-stop", request.ApplicationId),
                s_cleanupTimeout,
                CancellationToken.None).ConfigureAwait(false);
            await RunAsync(
                request,
                Target(deviceId, "uninstall", request.ApplicationId),
                s_cleanupTimeout,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original launch/cancellation failure; cleanup is best effort.
        }
    }

    private async Task<string?> CaptureScreenshotAsync(
        HarmonyOSDeviceRunRequest request,
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ScreenshotOutputPath))
            return null;

        HarmonyOSProcessResult capture = await RunRequiredAsync(
            request,
            Target(deviceId, "shell", "snapshot_display"),
            request.CommandTimeout,
            "capture a HarmonyOS screenshot",
            cancellationToken).ConfigureAwait(false);
        string remotePath = FindRemotePngPath(capture.CombinedOutput);
        string localPath = Path.GetFullPath(request.ScreenshotOutputPath);
        if (!string.Equals(Path.GetExtension(localPath), ".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("HarmonyOS screenshot evidence must use a .png path.");
        if (string.Equals(localPath, Path.GetFullPath(request.HapPath), PathComparison))
            throw new InvalidDataException("HarmonyOS screenshot evidence cannot overwrite the HAP artifact.");
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.Delete(localPath);
        await RunRequiredAsync(
            request,
            Target(deviceId, "file", "recv", remotePath, localPath),
            request.CommandTimeout,
            "receive the HarmonyOS screenshot",
            cancellationToken).ConfigureAwait(false);
        if (!File.Exists(localPath))
            throw new FileNotFoundException("HDC reported screenshot transfer success without creating the local file.", localPath);
        if (!await HasPngSignatureAsync(localPath, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(localPath);
            throw new InvalidDataException("HDC transferred screenshot evidence without a valid PNG signature.");
        }
        await using FileStream stream = new(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task<bool> HasPngSignatureAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            s_pngSignature.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] actual = new byte[s_pngSignature.Length];
        int offset = 0;
        while (offset < actual.Length)
        {
            int read = await stream.ReadAsync(actual.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return false;
            offset += read;
        }
        return actual.AsSpan().SequenceEqual(s_pngSignature);
    }

    private async Task<string> ReadPropertyAsync(
        HarmonyOSDeviceRunRequest request,
        string deviceId,
        string property,
        CancellationToken cancellationToken)
    {
        HarmonyOSProcessResult result = await RunRequiredAsync(
            request,
            Target(deviceId, "shell", "param", "get", property),
            request.CommandTimeout,
            "query a HarmonyOS device property",
            cancellationToken).ConfigureAwait(false);
        string value = SingleLine(result.StandardOutput);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("A connected HarmonyOS target returned an empty device property.");
        return value;
    }

    private async Task<HarmonyOSProcessResult> RunRequiredAsync(
        HarmonyOSDeviceRunRequest request,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string operation,
        CancellationToken cancellationToken)
    {
        HarmonyOSProcessResult result = await RunAsync(
            request,
            arguments,
            timeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccessful(result, operation);
        return result;
    }

    private Task<HarmonyOSProcessResult> RunAsync(
        HarmonyOSDeviceRunRequest request,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? completionMarker = null)
    {
        var redactions = new List<string>(request.Redactions.Count + 3);
        for (int i = 0; i < request.Redactions.Count; i++)
            redactions.Add(request.Redactions[i]);
        redactions.Add(request.HapPath);
        if (!string.IsNullOrWhiteSpace(request.RequestedDeviceId))
            redactions.Add(request.RequestedDeviceId);
        if (arguments.Count >= 2 && arguments[0] == "-t")
            redactions.Add(arguments[1]);

        return _processRunner.RunAsync(new HarmonyOSProcessCommand
        {
            ExecutablePath = request.HdcExecutablePath,
            Arguments = arguments,
            WorkingDirectory = request.WorkingDirectory,
            Timeout = timeout,
            OutputLimitCharacters = 1024 * 1024,
            Redactions = redactions,
            CompletionOutputMarker = completionMarker,
        }, cancellationToken);
    }

    private static HarmonyOSDeviceInfo SelectDevice(
        IReadOnlyList<HarmonyOSDeviceInfo> devices,
        HarmonyOSDeviceRunRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.RequestedDeviceId))
        {
            for (int i = 0; i < devices.Count; i++)
            {
                HarmonyOSDeviceInfo device = devices[i];
                if (!string.Equals(device.DeviceId, request.RequestedDeviceId, StringComparison.Ordinal))
                    continue;
                if (!IsCompatible(device, request))
                    throw new InvalidOperationException("The requested HarmonyOS target is not compatible with the selected API and ABI.");
                return device;
            }
            throw new InvalidOperationException("The requested HarmonyOS target is not connected.");
        }

        HarmonyOSDeviceInfo? match = null;
        int count = 0;
        for (int i = 0; i < devices.Count; i++)
        {
            if (!IsCompatible(devices[i], request))
                continue;
            match = devices[i];
            count++;
        }
        if (count == 0)
            throw new InvalidOperationException("No connected HarmonyOS target is compatible with the selected API and ABI.");
        if (count > 1)
            throw new InvalidOperationException("There are multiple compatible HarmonyOS targets; select one explicitly.");
        return match!;
    }

    private static bool IsCompatible(HarmonyOSDeviceInfo device, HarmonyOSDeviceRunRequest request) =>
        device.IsConnected &&
        device.ApiLevel >= request.TargetApi &&
        string.Equals(device.Abi, request.TargetAbi, StringComparison.Ordinal);

    private static IReadOnlyList<TargetLine> ParseTargets(string output)
    {
        var result = new List<TargetLine>();
        string[] lines = output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || string.Equals(line, "[Empty]", StringComparison.OrdinalIgnoreCase))
                continue;
            string[] values = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 2)
                throw new InvalidDataException("HDC returned an unrecognized target-list row.");
            string deviceId = RequireDeviceId(values[0]);
            string transport = values.Length > 1 ? values[1] : string.Empty;
            string state = values.Length > 2 ? values[2] : values[1];
            bool connected = string.Equals(values[1], "device", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "Connected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "Ready", StringComparison.OrdinalIgnoreCase);
            string model = string.Empty;
            for (int valueIndex = 1; valueIndex < values.Length; valueIndex++)
            {
                if (values[valueIndex].StartsWith("model:", StringComparison.Ordinal))
                    model = values[valueIndex]["model:".Length..];
            }
            result.Add(new TargetLine(deviceId, transport, state, model, connected));
        }
        return result.AsReadOnly();
    }

    private static string NormalizeAbi(string value)
    {
        if (value.Contains("x86_64", StringComparison.OrdinalIgnoreCase))
            return "x86_64";
        if (value.Contains("arm64-v8a", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("arm64", StringComparison.OrdinalIgnoreCase))
        {
            return "arm64-v8a";
        }
        throw new InvalidDataException("A connected HarmonyOS target reported an unsupported ABI.");
    }

    private static bool TryParseApi(string value, out int api)
    {
        value = SingleLine(value);
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out api))
            return api >= 1;
        int start = 0;
        while (start < value.Length && !char.IsAsciiDigit(value[start]))
            start++;
        int end = start;
        while (end < value.Length && char.IsAsciiDigit(value[end]))
            end++;
        return end > start &&
            int.TryParse(value.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out api) &&
            api >= 1;
    }

    private static string FindRemotePngPath(string output)
    {
        string[] tokens = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            string value = tokens[i].Trim('"', '\'', ',', ';');
            if (value.StartsWith("/", StringComparison.Ordinal) && value.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return value;
        }
        throw new InvalidDataException("The HarmonyOS screenshot command did not report a remote PNG path.");
    }

    private static void EnsureSuccessful(HarmonyOSProcessResult result, string operation)
    {
        if (result.TimedOut)
            throw new TimeoutException("HDC timed out while attempting to " + operation + ".");
        if (result.ExitCode != 0 || ContainsFailure(result.CombinedOutput))
            throw new InvalidOperationException("HDC could not " + operation + ". " + SafeFailure(result.CombinedOutput));
    }

    private static bool ContainsFailure(string value) =>
        value.Contains("[Fail]", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("failed", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsNotFound(string value) =>
        value.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("not exist", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("no bundle", StringComparison.OrdinalIgnoreCase);

    private static string SafeFailure(string output)
    {
        string value = SingleLine(output);
        return value.Length <= 512 ? value : value[..512] + "...";
    }

    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static IReadOnlyList<string> Target(string deviceId, params string[] command)
    {
        var arguments = new List<string>(command.Length + 2) { "-t", RequireDeviceId(deviceId) };
        for (int i = 0; i < command.Length; i++)
            arguments.Add(command[i]);
        return arguments.AsReadOnly();
    }

    private static string RequireDeviceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            throw new InvalidDataException("HarmonyOS device identifiers must be non-empty and bounded.");
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not ':' and not '-')
                throw new InvalidDataException("HarmonyOS device identifiers contain unsupported characters.");
        }
        return value;
    }

    private static void ValidateRequest(HarmonyOSDeviceRunRequest request, bool requireHap)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HdcExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        if (!Directory.Exists(request.WorkingDirectory))
            throw new DirectoryNotFoundException("HarmonyOS HDC working directory is missing.");
        if (requireHap)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.HapPath);
            if (!File.Exists(request.HapPath) || !string.Equals(Path.GetExtension(request.HapPath), ".hap", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("HarmonyOS Build & Run requires a verified HAP artifact.", request.HapPath);
        }
        if (request.TargetApi < 13 || request.TargetApi == 25 || request.TargetApi > 26)
            throw new ArgumentOutOfRangeException(nameof(request.TargetApi), "HarmonyOS Build & Run requires API 13-24 or API 26.");
        if (request.TargetAbi is not "x86_64" and not "arm64-v8a")
            throw new ArgumentException("HarmonyOS Build & Run requires x86_64 or arm64-v8a.", nameof(request.TargetAbi));
        if (!IsApplicationId(request.ApplicationId))
            throw new ArgumentException("HarmonyOS application ID must be dot-separated lowercase identifiers.", nameof(request.ApplicationId));
        ValidateIdentifier(request.AbilityName, nameof(request.AbilityName));
        if (!string.IsNullOrWhiteSpace(request.RequestedDeviceId))
            _ = RequireDeviceId(request.RequestedDeviceId);
        if (request.CommandTimeout <= TimeSpan.Zero || request.ReadinessTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request.CommandTimeout), "HarmonyOS HDC timeouts must be positive.");
        if (string.IsNullOrWhiteSpace(request.ReadinessMarker) || request.ReadinessMarker.Length > 256 ||
            request.ReadinessMarker.Contains('\r') || request.ReadinessMarker.Contains('\n'))
        {
            throw new ArgumentException("HarmonyOS readiness marker must be non-empty and single-line.", nameof(request.ReadinessMarker));
        }
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255)
            throw new ArgumentException("HarmonyOS application identifiers must be non-empty and bounded.", name);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                throw new ArgumentException("HarmonyOS application identifiers contain unsupported characters.", name);
        }
    }

    private static bool IsApplicationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255)
            return false;
        string[] segments = value.Split('.', StringSplitOptions.None);
        if (segments.Length < 2)
            return false;
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (segment.Length == 0 || segment[0] < 'a' || segment[0] > 'z')
                return false;
            for (int j = 1; j < segment.Length; j++)
            {
                char c = segment[j];
                if ((c < 'a' || c > 'z') && (c < '0' || c > '9') && c != '_')
                    return false;
            }
        }
        return true;
    }

    private sealed record TargetLine(
        string DeviceId,
        string Transport,
        string ConnectionState,
        string Model,
        bool IsConnected);
}
