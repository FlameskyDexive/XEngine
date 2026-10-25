// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Projects.Settings;

namespace XEngine.Editor.Build.Web;

internal sealed record WebPublishPlan(
    IReadOnlyList<string> PublishArgv,
    string PublishDirectory,
    string WwwRootDirectory);

/// <summary>
/// Publishes the generated <c>browser-wasm</c> player. <c>IsBrowserTarget=true</c> must be a
/// <em>global</em> MSBuild property (command-line <c>-p:</c>) so NuGet restore of Runtime
/// drops Magick / Vulkan / Slang; a local csproj property is not enough.
/// </summary>
internal static class WebPublishPlanner
{
    internal const string SizeReportFileName = WebPackageSizeReport.FileName;
    internal const string RuntimeBaselineFileName = "web-runtime-baseline.json";

    internal static WebPublishPlan Create(
        string projectPath,
        string exportRoot,
        BuildConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);

        string configName = configuration == BuildConfiguration.Release ? "Release" : "Debug";
        string publishDirectory = Path.Combine(exportRoot, "publish");
        return new WebPublishPlan(
            [
                "dotnet",
                "publish",
                projectPath,
                "-c",
                configName,
                "-p:IsBrowserTarget=true",
                "-o",
                publishDirectory,
            ],
            publishDirectory,
            Path.Combine(publishDirectory, "wwwroot"));
    }

    internal static async Task RunAsync(WebPublishPlan plan, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Directory.CreateDirectory(plan.PublishDirectory);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = plan.PublishArgv[0],
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        for (int i = 1; i < plan.PublishArgv.Count; i++)
            process.StartInfo.ArgumentList.Add(plan.PublishArgv[i]);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to start dotnet publish for browser-wasm. Install the wasm-tools workload. " +
                ex.Message,
                ex);
        }

        string stdout = await process.StandardOutput.ReadToEndAsync(cancellation).ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync(cancellation).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "dotnet publish (browser-wasm) exited " + process.ExitCode + ". " +
                (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim());
        }
    }

    /// <summary>
    /// .NET 10 still hard-fingerprints WasmResource files (<c>dotnet.{hash}.js</c>) even when
    /// <c>StaticWebAssetFingerprintingEnabled</c> is false. A static host and
    /// <c>import './_framework/dotnet.js'</c> need the stable label. Copies the hashed boot
    /// entry next to itself; native/runtime/wasm keep hashed names inside the boot config.
    /// Call after <see cref="WriteSizeReport"/> so alias bytes are not counted twice.
    /// </summary>
    internal static int WriteStableFrameworkAliases(string frameworkDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkDirectory);
        if (!Directory.Exists(frameworkDirectory))
            throw new DirectoryNotFoundException("Published _framework was not found at " + frameworkDirectory);

        return CopyFingerprintedAlias(
            frameworkDirectory,
            "dotnet.",
            ".js",
            "dotnet.js",
            "dotnet.native.",
            "dotnet.runtime.");
    }

    private static int CopyFingerprintedAlias(
        string directory,
        string prefix,
        string suffix,
        string stableName,
        params string[] excludedPrefixes)
    {
        string? source = null;
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".br", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            if (name.Equals(stableName, StringComparison.Ordinal))
                continue;

            bool excluded = false;
            for (int i = 0; i < excludedPrefixes.Length; i++)
            {
                if (name.StartsWith(excludedPrefixes[i], StringComparison.Ordinal))
                {
                    excluded = true;
                    break;
                }
            }

            if (excluded)
                continue;

            int middleLength = name.Length - prefix.Length - suffix.Length;
            if (middleLength <= 0)
                continue;
            string middle = name.Substring(prefix.Length, middleLength);
            if (middle.IndexOf('.') >= 0)
                continue;

            if (source != null)
            {
                DateTime existing = File.GetLastWriteTimeUtc(source);
                DateTime candidate = File.GetLastWriteTimeUtc(file);
                if (candidate >= existing)
                    source = file;
                continue;
            }

            source = file;
        }

        if (source == null)
            return 0;

        File.Copy(source, Path.Combine(directory, stableName), overwrite: true);
        return 1;
    }

    /// <summary>
    /// Copies fingerprinted <c>dotnet.native.*.wasm</c> / <c>.wasm.br</c> to stable names under
    /// <c>wechat/wasm/</c> so <c>WXWebAssembly.instantiate</c> can use a package-relative path.
    /// Does not copy the rest of <c>_framework</c> (that would blow the 4 MiB main package).
    /// </summary>
    internal static int CopyWeChatRuntimeAliases(string frameworkDirectory, string wechatDirectory) =>
        CopyMiniGameRuntimeAliases(frameworkDirectory, wechatDirectory);

    /// <summary>
    /// Same stable <c>dotnet.native.wasm</c> / <c>.wasm.br</c> aliases under <c>douyin/wasm/</c>
    /// for <c>TTWebAssembly.instantiate</c>. Douyin boot prefers the <c>.br</c> path.
    /// </summary>
    internal static int CopyDouyinRuntimeAliases(string frameworkDirectory, string douyinDirectory) =>
        CopyMiniGameRuntimeAliases(frameworkDirectory, douyinDirectory);

    internal static int CopyMiniGameRuntimeAliases(string frameworkDirectory, string miniGameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(miniGameDirectory);
        if (!Directory.Exists(frameworkDirectory))
            throw new DirectoryNotFoundException("Published _framework was not found at " + frameworkDirectory);

        string wasmDirectory = Path.Combine(miniGameDirectory, "wasm");
        Directory.CreateDirectory(wasmDirectory);
        int copied = 0;
        copied += CopyNamedOrFingerprinted(
            frameworkDirectory,
            wasmDirectory,
            "dotnet.native.",
            ".wasm",
            "dotnet.native.wasm");
        copied += CopyNamedOrFingerprinted(
            frameworkDirectory,
            wasmDirectory,
            "dotnet.native.",
            ".wasm.br",
            "dotnet.native.wasm.br");
        return copied;
    }

    private static int CopyNamedOrFingerprinted(
        string sourceDirectory,
        string destDirectory,
        string prefix,
        string suffix,
        string destFileName)
    {
        string exact = Path.Combine(sourceDirectory, destFileName);
        if (File.Exists(exact))
        {
            File.Copy(exact, Path.Combine(destDirectory, destFileName), overwrite: true);
            return 1;
        }

        string? source = null;
        foreach (string file in Directory.EnumerateFiles(sourceDirectory))
        {
            string name = Path.GetFileName(file);
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(suffix, StringComparison.Ordinal) ||
                name.Equals(destFileName, StringComparison.Ordinal))
            {
                continue;
            }

            int middleLength = name.Length - prefix.Length - suffix.Length;
            if (middleLength <= 0)
                continue;
            string middle = name.Substring(prefix.Length, middleLength);
            if (middle.IndexOf('.') >= 0)
                continue;

            if (source != null)
            {
                DateTime existing = File.GetLastWriteTimeUtc(source);
                DateTime candidate = File.GetLastWriteTimeUtc(file);
                if (candidate >= existing)
                    source = file;
                continue;
            }

            source = file;
        }

        if (source == null)
            return 0;

        File.Copy(source, Path.Combine(destDirectory, destFileName), overwrite: true);
        return 1;
    }

    internal static WebPackageSizeSnapshot WriteSizeReport(string exportRoot, string frameworkDirectory) =>
        WebPackageSizeReport.MeasureAndWrite(exportRoot, frameworkDirectory);

    /// <summary>
    /// Honest runtime gate placeholders. Browser first present is the B5 WebGL2 magenta clear;
    /// WeChat GPU present is N/A without 内测; Douyin is excluded from the framerate gate.
    /// Do not invent milliseconds or FPS.
    /// </summary>
    internal static void WriteRuntimeBaseline(string exportRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        string directory = Path.Combine(exportRoot, "verification");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, RuntimeBaselineFileName),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    browserColdStartToFirstPresentMs = (int?)null,
                    browserEmptySceneFps = (int?)null,
                    weChatColdStartToFirstPresentMs = (int?)null,
                    douyinFramerateTracked = false,
                    notes = new[]
                    {
                        "Browser first present is the B5 WebGL2 magenta CommandBuffer clear, not Dawn and not Paper/Game.",
                        "WeChat GPU present is N/A without getGPU 内测. DevTools may be installed; Mono path boot is not a timing sample until logs prove it.",
                        "Douyin is excluded from the framerate gate.",
                    },
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }
}
