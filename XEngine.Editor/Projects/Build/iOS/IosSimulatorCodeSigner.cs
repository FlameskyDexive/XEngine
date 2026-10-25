// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.Ios;

/// <summary>
/// Applies an ad-hoc signature to the completed Simulator app. The .NET iOS SDK leaves
/// linker-signed nested dylibs when EnableCodeSigning is false; Simulator install needs
/// every nested Mach-O code object signed after the final bundle contents are copied.
/// </summary>
internal static class IosSimulatorCodeSigner
{
    internal const string VerificationFileName = "ios-simulator-signing.json";

    internal static async Task<int> SignAndVerifyAsync(
        string appBundlePath,
        string exportRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("iOS Simulator code signing requires macOS and codesign.");

        string appBundle = Path.GetFullPath(appBundlePath);
        if (!Directory.Exists(appBundle) || !appBundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            throw new DirectoryNotFoundException("iOS Simulator app bundle was not found: " + appBundle);

        string[] dylibs = Directory.EnumerateFiles(appBundle, "*.dylib", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        foreach (string dylib in dylibs)
            await RunCodesignAsync(["--force", "--sign", "-", dylib], cancellationToken).ConfigureAwait(false);

        string[] frameworks = Directory.EnumerateDirectories(appBundle, "*.framework", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        foreach (string framework in frameworks)
            await RunCodesignAsync(["--force", "--sign", "-", framework], cancellationToken).ConfigureAwait(false);

        await RunCodesignAsync(["--force", "--sign", "-", appBundle], cancellationToken).ConfigureAwait(false);
        await RunCodesignAsync(["--verify", "--deep", "--strict", appBundle], cancellationToken)
            .ConfigureAwait(false);

        string verificationDirectory = Path.Combine(Path.GetFullPath(exportRoot), "verification");
        Directory.CreateDirectory(verificationDirectory);
        File.WriteAllText(
            Path.Combine(verificationDirectory, VerificationFileName),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    signingIdentity = "-",
                    signingMode = "ad-hoc (Simulator only)",
                    appBundle = Path.GetFileName(appBundle),
                    signedDynamicLibraries = dylibs.Length,
                    signedFrameworks = frameworks.Length,
                    deepStrictVerificationPassed = true,
                },
                new JsonSerializerOptions { WriteIndented = true }));
        return dylibs.Length + frameworks.Length + 1;
    }

    private static async Task RunCodesignAsync(string[] arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "codesign",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to start codesign for the iOS Simulator app.", ex);
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string output = (await stdout.ConfigureAwait(false)) + Environment.NewLine +
            (await stderr.ConfigureAwait(false));
        if (process.ExitCode == 0)
            return;

        const int maxOutputCharacters = 4096;
        if (output.Length > maxOutputCharacters)
            output = output[^maxOutputCharacters..];
        throw new InvalidOperationException(
            "codesign failed for the iOS Simulator app (exit " + process.ExitCode + "): " + output.Trim());
    }
}
