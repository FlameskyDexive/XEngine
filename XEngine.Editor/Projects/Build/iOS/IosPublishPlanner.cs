// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;

namespace XEngine.Editor.Build.Ios;

internal sealed record IosPublishPlan(
    IReadOnlyList<string> PublishArgv,
    IReadOnlyList<string> DsymArchiveArgv,
    IReadOnlyList<string> StripArgv,
    string AppBundlePath,
    string DsymArchivePath);

/// <summary>
/// Composes <c>dotnet publish -r ios-arm64</c>, dSYM archive, and in-package strip.
/// Execution is macOS-only; Windows hosts keep the command list and fail closed.
/// </summary>
internal static class IosPublishPlanner
{
    internal const string DsymVerificationFileName = "ios-dsym.json";
    internal const string DsymArchiveDirectoryName = "dSYM";

    internal const string OffMacExecuteError =
        "iOS `dotnet publish -r ios-arm64` requires macOS + Xcode. This host is not a Mac; no .ipa was emitted.";

    public static IosPublishPlan Create(
        string projectPath,
        string outputDirectory,
        string assemblyName,
        IosSigningPlan signing,
        BuildConfiguration configuration,
        string runtimeIdentifier = "ios-arm64")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        ArgumentNullException.ThrowIfNull(signing);
        bool simulator = runtimeIdentifier.StartsWith("iossimulator", StringComparison.Ordinal);

        string configName = configuration == BuildConfiguration.Release ? "Release" : "Debug";
        // `dotnet publish` rejects simulator RIDs (device-only target); simulator apps come
        // out of `dotnet build` under bin/<config>/net10.0-ios/<rid>/<app>.app.
        string appPath = simulator
            ? Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(projectPath))!,
                "bin",
                configName,
                "net10.0-ios",
                runtimeIdentifier,
                assemblyName + ".app")
            : Path.Combine(outputDirectory, assemblyName + ".app");
        string dsymPath = Path.Combine(outputDirectory, DsymArchiveDirectoryName, assemblyName + ".app.dSYM");
        string binaryPath = Path.Combine(appPath, assemblyName);

        var publish = new List<string>
        {
            "dotnet",
            simulator ? "build" : "publish",
            projectPath,
            "-f",
            "net10.0-ios",
            "-r",
            runtimeIdentifier,
            "-c",
            configName,
        };
        if (simulator)
        {
            // The SDK build omits developer signing; the completed bundle receives an ad-hoc
            // Simulator signature after the pipeline copies all final resources and dylibs.
            publish.Add("-p:EnableCodeSigning=false");
        }
        else
        {
            publish.AddRange(
            [
                "-p:EnableCodeSigning=true",
                "-p:CodesignKey=" + signing.CodesignIdentity,
                "-p:MtouchNoSymbolStrip=" + (signing.StripLinkedSymbols ? "false" : "true"),
            ]);
        }
        if (!simulator)
            publish.AddRange(["-o", outputDirectory]);
        if (!simulator)
        {
            if (!string.IsNullOrWhiteSpace(signing.TeamId))
                publish.Add("-p:DevelopmentTeam=" + signing.TeamId);
            if (signing.Style == IosSigningStyle.Automatic)
                publish.Add("-p:EnableAutomaticProvisioning=true");
            else if (!string.IsNullOrWhiteSpace(signing.ProvisioningProfileUuid))
                publish.Add("-p:CodesignProvision=" + signing.ProvisioningProfileUuid);
        }

        IReadOnlyList<string> dsym = !simulator && signing.ArchiveDsym
            ? ["dsymutil", binaryPath, "-o", dsymPath]
            : [];
        IReadOnlyList<string> strip = !simulator && signing.StripLinkedSymbols
            ? ["strip", "-x", binaryPath]
            : [];

        return new IosPublishPlan(publish, dsym, strip, appPath, dsymPath);
    }

    public static void WriteDsymVerification(string exportRoot, IosSigningPlan plan, bool executed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        ArgumentNullException.ThrowIfNull(plan);
        string directory = Path.Combine(exportRoot, "verification");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, DsymVerificationFileName),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    archiveRelativePath = DsymArchiveDirectoryName,
                    stripInPackage = plan.StripLinkedSymbols,
                    pairing = "dwarfdump -u on the archived dSYM must match the UUID of the stripped binary inside the .app",
                    tool = "dsymutil",
                    executed,
                    hostCanExecute = OperatingSystem.IsMacOS(),
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task RunAsync(IosPublishPlan plan, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!OperatingSystem.IsMacOS())
            throw new InvalidOperationException(OffMacExecuteError);

        await RunArgvAsync(plan.PublishArgv, cancellation).ConfigureAwait(false);
        if (plan.DsymArchiveArgv.Count > 0)
            await RunArgvAsync(plan.DsymArchiveArgv, cancellation).ConfigureAwait(false);
        if (plan.StripArgv.Count > 0)
            await RunArgvAsync(plan.StripArgv, cancellation).ConfigureAwait(false);
    }

    private static async Task RunArgvAsync(IReadOnlyList<string> argv, CancellationToken cancellation)
    {
        if (argv.Count == 0)
            return;
        if (ContainsForbiddenTool(argv))
            throw new InvalidOperationException("iOS publish must not invoke adb or ios-deploy.");

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
            throw new InvalidOperationException(
                "Failed to start '" + argv[0] + "'. Install Xcode and the .NET iOS workload on this Mac. " +
                ex.Message,
                ex);
        }

        string stdout = await process.StandardOutput.ReadToEndAsync(cancellation).ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync(cancellation).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "'" + argv[0] + "' exited " + process.ExitCode + ". " +
                (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim());
        }
    }

    internal static bool ContainsForbiddenTool(IReadOnlyList<string> argv)
    {
        for (int i = 0; i < argv.Count; i++)
        {
            string token = argv[i];
            if (token.Contains("adb", StringComparison.OrdinalIgnoreCase) ||
                token.Contains("ios-deploy", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
