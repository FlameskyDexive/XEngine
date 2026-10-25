// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.Ios;

public sealed record XcodeLocatorResult(
    bool Found,
    string? DeveloperDir,
    Version? Version,
    bool CommandLineToolsOnly,
    bool MeetsMinimum,
    string? Guidance)
{
    public static readonly XcodeLocatorResult NotFound = new(false, null, null, false, false,
        "Install Xcode from the Mac App Store (https://apps.apple.com/app/xcode/id497799835), then run "
        + "`sudo xcode-select -s /Applications/Xcode.app` if needed. Command-Line-Tools alone cannot sign, "
        + "publish for ios-arm64, or drive `xcrun devicectl`.");
}

/// <summary>
/// Locates and validates the user's Xcode. Apple licensing forbids redistributing Xcode, so the iOS
/// platform module downloads nothing — it finds the developer's install and makes failures
/// actionable. Probe chain: <c>DEVELOPER_DIR</c> env → <c>xcode-select -p</c> → <c>xcodebuild</c> on
/// PATH (walked up to Contents/Developer). A Command-Line-Tools-only selection is reported as
/// degraded with guidance — full Xcode is required for signing/publish/devicectl.
/// </summary>
public sealed class XcodeLocator
{
    /// <summary>Process seam: (exitCode, stdout, stderr). Production shells out via cmd/bash;
    /// tests feed canned outputs.</summary>
    public delegate Task<(int ExitCode, string StdOut, string StdErr)> ProcessRunner(
        string executable, string arguments, CancellationToken cancellation);

    private readonly ProcessRunner _runner;

    public XcodeLocator(ProcessRunner? runner = null)
    {
        _runner = runner ?? RunProcessAsync;
    }

    public async Task<XcodeLocatorResult> LocateAsync(Version? minimumVersion = null,
        CancellationToken cancellation = default)
    {
        if (!OperatingSystem.IsMacOS())
            return new XcodeLocatorResult(false, null, null, false, false,
                "iOS signing, `dotnet publish -r ios-arm64`, and `xcrun devicectl` run on macOS. This host can " +
                "still prepare signing plans and preflights (see the iOS build profile).");
        return await LocateCoreAsync(minimumVersion, cancellation).ConfigureAwait(false);
    }

    /// <summary>The macOS probe chain without the host gate (tests feed canned process output).</summary>
    internal async Task<XcodeLocatorResult> LocateCoreAsync(Version? minimumVersion = null,
        CancellationToken cancellation = default)
    {
        string? developerDir = System.Environment.GetEnvironmentVariable("DEVELOPER_DIR");
        if (string.IsNullOrWhiteSpace(developerDir))
        {
            (int exit, string stdout, _) = await _runner("xcode-select", "-p", cancellation).ConfigureAwait(false);
            if (exit == 0) developerDir = stdout.Trim();
        }

        if (string.IsNullOrWhiteSpace(developerDir))
            return XcodeLocatorResult.NotFound;

        bool cltOnly = developerDir.Contains("CommandLineTools", StringComparison.Ordinal);
        if (cltOnly)
            return new XcodeLocatorResult(false, developerDir, null, true, false,
                $"xcode-select points at Command-Line Tools ('{developerDir}'). Full Xcode is required for " +
                "signing and publishing: install Xcode from the App Store, then " +
                "`sudo xcode-select -s /Applications/Xcode.app/Contents/Developer`.");

        // 2. Validate via xcodebuild -version ("Xcode 16.2\nBuild version 16C5032a").
        string xcodebuild = Path.Combine(developerDir, "usr", "bin", "xcodebuild");
        (int versionExit, string versionOut, string versionErr) = await _runner(xcodebuild, "-version", cancellation)
            .ConfigureAwait(false);
        if (versionExit != 0)
            return new XcodeLocatorResult(false, developerDir, null, false, false,
                $"Located '{developerDir}' but `xcodebuild -version` failed: {versionErr.Trim()}");

        Version? version = ParseXcodeVersion(versionOut);
        bool meets = version != null && (minimumVersion == null || version >= minimumVersion);
        string? guidance = meets ? null
            : version == null
                ? $"Could not parse the Xcode version from '{versionOut.Split('\n')[0]}'."
                : $"Xcode {version} is older than the required {minimumVersion}; update Xcode from the App Store.";
        return new XcodeLocatorResult(true, developerDir, version, false, meets, guidance);
    }

    private static Version? ParseXcodeVersion(string versionOutput)
    {
        foreach (string line in versionOutput.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("Xcode ", StringComparison.Ordinal)) continue;
            if (Version.TryParse(trimmed["Xcode ".Length..].Trim(), out Version? version))
                return version;
        }
        return null;
    }

    private static async Task<(int, string, string)> RunProcessAsync(
        string executable, string arguments, CancellationToken cancellation)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(executable, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        if (!process.Start()) return (-1, "", "failed to start");
        string stdout = await process.StandardOutput.ReadToEndAsync(cancellation).ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync(cancellation).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }
}
