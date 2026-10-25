using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XHub.Infrastructure;
using XHub.Models;

namespace XHub.Services;

public enum PlatformToolchainTarget
{
    Android,
    HarmonyOS,
    Ios,
}

/// <summary>One captured output line from the editor CLI (diagnostics surface, §3.4).</summary>
public sealed record ToolchainLogLine(bool IsError, string Text, DateTime TimestampUtc);

/// <summary>Final result of a toolchain CLI run.</summary>
public sealed record ToolchainRunResult(
    bool Ok,
    bool LicenseConsentRequired,
    int ExitCode,
    IReadOnlyList<ToolchainLogLine> Log);

/// <summary>
/// Runs the editor installation's own platform-toolchain CLI (§3.4: 复用该编辑器版本的
/// CLI/协议，不在 XHub 复制工具链实现): <c>--install-platform-tools</c> with optional
/// <c>--accept-sdk-licenses</c> (explicit Android SDK license consent) and
/// <c>--harmonyos-bundle</c> (local OpenHarmony SDK bundle import). Output is streamed for the
/// diagnostics log; cancellation kills the editor process.
/// </summary>
public static class PlatformToolchainRunner
{
    public const int ExitLicenseConsentRequired = 2;

    public static string[] BuildArgs(PlatformToolchainTarget target, bool acceptLicenses, string? harmonyBundlePath)
    {
        var args = new List<string>
        {
            "--install-platform-tools",
            target switch
            {
                PlatformToolchainTarget.Android => "android",
                PlatformToolchainTarget.HarmonyOS => "harmonyos",
                _ => "ios",
            },
        };
        if (acceptLicenses && target == PlatformToolchainTarget.Android)
            args.Add("--accept-sdk-licenses"); // explicit Google SDK license consent (§3.4)
        if (target == PlatformToolchainTarget.HarmonyOS && !string.IsNullOrWhiteSpace(harmonyBundlePath))
        {
            args.Add("--harmonyos-bundle");
            args.Add(harmonyBundlePath);
        }
        return args.ToArray();
    }

    /// <summary>Spawns the editor CLI and streams output. Never throws; failures come back in the result.</summary>
    public static async Task<ToolchainRunResult> RunAsync(
        InstallationRecord installation, PlatformToolchainTarget target,
        bool acceptLicenses, string? harmonyBundlePath,
        Action<ToolchainLogLine>? onLine, CancellationToken ct)
    {
        if (installation.IsBroken || !File.Exists(installation.EditorExePath))
        {
            var preFailed = new List<ToolchainLogLine> { new(true, "editor executable missing", DateTime.UtcNow) };
            return new ToolchainRunResult(false, false, -1, preFailed);
        }

        var log = new List<ToolchainLogLine>();
        var startInfo = new ProcessStartInfo
        {
            FileName = installation.EditorExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in BuildArgs(target, acceptLicenses, harmonyBundlePath))
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return Fail("editor process could not be started", log);
        }
        catch (Exception ex)
        {
            return Fail($"editor process start failed: {ex.Message}", log);
        }

        void Capture(bool isError, string? line)
        {
            if (string.IsNullOrEmpty(line)) return;
            var entry = new ToolchainLogLine(isError, line, DateTime.UtcNow);
            lock (log) log.Add(entry);
            onLine?.Invoke(entry);
        }
        process.OutputDataReceived += (_, e) => Capture(false, e.Data);
        process.ErrorDataReceived += (_, e) => Capture(true, e.Data);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            log.Add(new ToolchainLogLine(true, "canceled by user", DateTime.UtcNow));
            return new ToolchainRunResult(false, false, -1, log);
        }

        int exitCode = process.HasExited ? process.ExitCode : -1;
        bool licenseGate = exitCode == ExitLicenseConsentRequired && target == PlatformToolchainTarget.Android;
        bool ok = exitCode == 0;
        return new ToolchainRunResult(ok, licenseGate, exitCode, log);

        static ToolchainRunResult Fail(string message, List<ToolchainLogLine> log)
        {
            log.Add(new ToolchainLogLine(true, message, DateTime.UtcNow));
            return new ToolchainRunResult(false, false, -1, log);
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>iOS toolchain detection requires macOS + Xcode (§3.4 host limitation).</summary>
    public static bool IosSupportedOnHost => OperatingSystem.IsMacOS();
}
