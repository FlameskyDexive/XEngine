// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Threading.Tasks;

using XEngine.Editor.Build.HarmonyOS;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSProcessRunnerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineHarmonyOSProcessRunnerTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RunAsync_RedactsSensitiveValuesFromOutputAndDisplayCommand()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(_testRoot);
        const string secret = "harmony-signing-secret";

        HarmonyOSProcessResult result = await new HarmonyOSProcessRunner().RunAsync(new HarmonyOSProcessCommand
        {
            ExecutablePath = CommandProcessorPath,
            Arguments = ["/d", "/c", "echo " + secret + " & echo " + secret + " 1>&2"],
            WorkingDirectory = _testRoot,
            Redactions = [secret],
        });

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(secret, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.DisplayCommand, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_TruncatesCapturedOutputWithoutBlockingTheProcess()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(_testRoot);

        HarmonyOSProcessResult result = await new HarmonyOSProcessRunner().RunAsync(new HarmonyOSProcessCommand
        {
            ExecutablePath = CommandProcessorPath,
            Arguments = ["/d", "/c", "for /L %i in (1,1,200) do @echo 1234567890"],
            WorkingDirectory = _testRoot,
            OutputLimitCharacters = 64,
        });

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.OutputWasTruncated);
        Assert.Contains("[output truncated]", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_KillsTimedOutProcessAndReturnsTimeoutResult()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(_testRoot);

        HarmonyOSProcessResult result = await new HarmonyOSProcessRunner().RunAsync(new HarmonyOSProcessCommand
        {
            ExecutablePath = CommandProcessorPath,
            Arguments = ["/d", "/c", "ping 127.0.0.1 -n 6 > nul"],
            WorkingDirectory = _testRoot,
            Timeout = TimeSpan.FromMilliseconds(100),
        });

        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_StopsStreamingProcessAfterCompletionMarker()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(_testRoot);

        HarmonyOSProcessResult result = await new HarmonyOSProcessRunner().RunAsync(new HarmonyOSProcessCommand
        {
            ExecutablePath = CommandProcessorPath,
            Arguments = ["/d", "/c", "echo XENGINE_HARMONYOS_READY & ping 127.0.0.1 -n 6 > nul"],
            WorkingDirectory = _testRoot,
            Timeout = TimeSpan.FromSeconds(10),
            CompletionOutputMarker = "XENGINE_HARMONYOS_READY",
        });

        Assert.True(result.CompletionMarkerFound);
        Assert.False(result.TimedOut);
        Assert.Contains("XENGINE_HARMONYOS_READY", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(result.DurationMilliseconds < 5_000, $"Marker termination took {result.DurationMilliseconds} ms.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private static string CommandProcessorPath => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
}
