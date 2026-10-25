// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build;

namespace XEngine.Editor.Build.Ios;

/// <summary>
/// <c>xcrun devicectl</c> install + launch. Never depends on <c>ios-deploy</c> or adb.
/// Resolve against the iPhone SDK instead of the host's default macOS SDK.
/// </summary>
internal static class IosDeviceCtlCommands
{
    public static IReadOnlyList<string> ListDevices() =>
        ["xcrun", "--sdk", "iphoneos", "devicectl", "list", "devices"];

    public static IReadOnlyList<string> InstallApp(string deviceUdid, string appPath) =>
        ["xcrun", "--sdk", "iphoneos", "devicectl", "device", "install", "app", "--device", deviceUdid, appPath];

    public static IReadOnlyList<string> LaunchProcess(string deviceUdid, string bundleId) =>
        ["xcrun", "--sdk", "iphoneos", "devicectl", "device", "process", "launch", "--device", deviceUdid, bundleId];
}

internal static class IosDeviceBridge
{
    internal const string OffMacLaunchError =
        "iOS Build & Run requires a macOS + Xcode build host and `xcrun devicectl`. " +
        "This host is not a Mac and cannot talk to an iPhone.";

    internal const string MissingAppError =
        "iOS Build & Run could not find the signed .app bundle.";

    internal const string MissingUdidError =
        "iOS Build & Run requires DeploymentDeviceUdid on IosBuildProfile.";

    internal const string MissingXcrunError =
        "xcrun devicectl is not available. Install Xcode on this Mac.";

    public static async Task<BuildLaunchResult> InstallAndLaunchAsync(
        string appPath,
        string bundleId,
        string deviceUdid,
        CancellationToken cancellation = default)
    {
        if (!OperatingSystem.IsMacOS())
            return BuildLaunchResult.Failed(OffMacLaunchError);
        if (string.IsNullOrWhiteSpace(appPath) || !Directory.Exists(appPath))
            return BuildLaunchResult.Failed(MissingAppError);
        if (string.IsNullOrWhiteSpace(deviceUdid))
            return BuildLaunchResult.Failed(MissingUdidError);
        if (string.IsNullOrWhiteSpace(bundleId))
            return BuildLaunchResult.Failed("iOS Build & Run requires a Bundle ID.");

        IReadOnlyList<string> install = IosDeviceCtlCommands.InstallApp(deviceUdid, appPath);
        IReadOnlyList<string> launch = IosDeviceCtlCommands.LaunchProcess(deviceUdid, bundleId);
        if (IosPublishPlanner.ContainsForbiddenTool(install) ||
            IosPublishPlanner.ContainsForbiddenTool(launch))
        {
            return BuildLaunchResult.Failed("iOS Build & Run must not invoke adb or ios-deploy.");
        }

        try
        {
            await RunArgvAsync(install, cancellation).ConfigureAwait(false);
            await RunArgvAsync(launch, cancellation).ConfigureAwait(false);
            return BuildLaunchResult.Succeeded(
                "Installed and launched " + Path.GetFileName(appPath) + " via xcrun devicectl.");
        }
        catch (Exception ex)
        {
            return BuildLaunchResult.Failed(ex.Message);
        }
    }

    private static async Task RunArgvAsync(IReadOnlyList<string> argv, CancellationToken cancellation)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = argv[0],
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        for (int i = 1; i < argv.Count; i++)
            process.StartInfo.ArgumentList.Add(argv[i]);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(MissingXcrunError + " " + ex.Message, ex);
        }

        string stdout = await process.StandardOutput.ReadToEndAsync(cancellation).ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync(cancellation).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "xcrun devicectl failed (" + process.ExitCode + "). " +
                (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim());
        }
    }
}
