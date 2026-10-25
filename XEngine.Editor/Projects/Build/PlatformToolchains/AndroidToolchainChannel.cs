// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build.Android;
using XEngine.Editor.Projects.Packages;
using XEngine.Editor.Theming;

namespace XEngine.Editor.Projects.Build.PlatformToolchains;

public sealed class AndroidChannelOptions
{
    /// <summary>Project whose PackageManager may supply the module (legacy manifest pin). Editor-level
    /// resolution (module store / Editor bundle) runs first, so this can be null — the launcher
    /// installs SDKs without any project open.</summary>
    public required Project? Project { get; init; }
    public string? CacheRootOverride { get; init; }

    /// <summary>Explicit user consent to the Google Android SDK licenses
    /// (https://developer.android.com/studio/terms). Without it the channel stops before any
    /// sdkmanager step and reports <see cref="AndroidChannelResult.NeedsLicenseConsent"/> — the
    /// existing resolver's "no non-interactive license acceptance" rule applies to us too.</summary>
    public bool AcceptLicenses { get; init; }

    /// <summary>Write resolved roots into EditorSettings (skipped by tests; the settings singleton
    /// persists to the user profile).</summary>
    public bool WriteEditorSettings { get; init; } = true;

    /// <summary>Test seam for the sdkmanager process (internal: the runner interface is
    /// editor-internal; the editor uses the real one by default).</summary>
    internal IAndroidToolchainProcessRunner? Runner { get; init; }

    public IProgress<PlatformToolchainProgress>? Progress { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed class AndroidChannelResult
{
    public required PlatformToolchainInstallResult Core { get; init; }
    public bool NeedsLicenseConsent { get; init; }
    public IReadOnlyList<string> InstalledPackages { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
    public bool Success => Error == null && Core.Success && !NeedsLicenseConsent;
}

/// <summary>
/// The Android acquisition channel on top of <see cref="PlatformToolchainService"/>: installs the
/// archive/file components (JDK, cmdline-tools, bundletool), then — only after explicit license
/// consent — writes the accepted license hashes and delegates SDK package installation
/// (platform-tools, build-tools, platform, NDK) to Google's sdkmanager from the freshly installed
/// cmdline-tools, and finally writes the resolved roots back into EditorSettings so the existing
/// AndroidToolchainResolver discovery picks them up (user-set values are never overwritten).
/// </summary>
public static class AndroidToolchainChannel
{
    /// <summary>The canonical license-hash files sdkmanager accepts as proof of consent. Extra
    /// hash lines are harmless (the check is membership), so all known generations are recorded.</summary>
    private static readonly (string File, string[] Hashes)[] LicenseFiles =
    {
        ("android-sdk-license", new[]
        {
            "24333f8a63b6825ea9c5514f83c2829b004d1fee",
            "d56f5c8bd41b273ffde60ed6cbaf2f129a79ab01",
            "8933bad161af4178b1185d1a37fbf41ea5269c55",
        }),
        ("android-sdk-preview-license", new[] { "84831b9409646a918e30573bab4c9c91346d8abd" }),
    };

    public const string LicenseTermsUrl = "https://developer.android.com/studio/terms";

    public static async Task<AndroidChannelResult> InstallAsync(AndroidChannelOptions options)
    {
        IProgress<PlatformToolchainProgress>? progress = options.Progress;
        void Emit(string stage, string? detail = null)
            => progress?.Report(new PlatformToolchainProgress(stage, "sdk", 0, 0, detail));

        // 1. Archive/file components through the shared core (fingerprint cache applies).
        PlatformToolchainInstallResult core = await PlatformToolchainService.InstallAsync(
            options.Project, PlatformToolchainKind.Android, options.CacheRootOverride,
            progress, options.CancellationToken).ConfigureAwait(false);
        if (!core.Success)
            return new AndroidChannelResult { Core = core, Error = "component download failed" };

        ToolchainManifest? manifest = ToolchainManifest.ReadForPlatform(options.Project, PlatformToolchainKind.Android);
        ToolchainComponent? sdkComponent = manifest?.Components.FirstOrDefault(c => c.Kind == ToolchainComponentKind.SdkManager);
        if (manifest == null || sdkComponent == null)
            return new AndroidChannelResult { Core = core };

        string sdkRoot = Path.Combine(core.InstallRoot, sdkComponent.Layout.Replace('/', Path.DirectorySeparatorChar));
        string licenseRoot = Path.Combine(sdkRoot, "licenses");

        // Fingerprint cache applies to the sdk component too: state + sentinel (written by a
        // previous channel run) mean the packages are already installed — skip sdkmanager entirely.
        PlatformToolchainComponentResult? sdkStatus = core.Components.FirstOrDefault(c => c.Id == sdkComponent.Id);
        if (sdkStatus is { Status: PlatformToolchainComponentStatus.Cached })
        {
            Emit("cached", "sdk packages already installed under " + sdkRoot);
            return new AndroidChannelResult { Core = core };
        }

        // 2. License gate.
        if (!LicensesAccepted(licenseRoot))
        {
            if (!options.AcceptLicenses)
            {
                Emit("consent-required",
                    "Google Android SDK licenses must be accepted explicitly before sdkmanager runs.");
                return new AndroidChannelResult { Core = core, NeedsLicenseConsent = true };
            }
            WriteAcceptedLicenses(licenseRoot);
        }

        // 3. sdkmanager packages, driven by the freshly installed cmdline-tools under the JDK it needs.
        string sdkmanager = Path.Combine(sdkRoot, "cmdline-tools", "latest", "bin", "sdkmanager.bat");
        if (!File.Exists(sdkmanager))
            return new AndroidChannelResult
            {
                Core = core,
                Error = $"cmdline-tools are missing their sdkmanager at '{sdkmanager}'.",
            };
        PlatformToolchainComponentResult? jdk = core.Components.FirstOrDefault(c => c.Id == "jdk");
        string javaHome = jdk?.Root ?? "";

        var runner = options.Runner ?? new AndroidToolchainProcessRunner();
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(javaHome))
        {
            environment["JAVA_HOME"] = javaHome;
            environment["PATH"] = Path.Combine(javaHome, "bin") + Path.PathSeparator +
                (Environment.GetEnvironmentVariable("PATH") ?? "");
        }

        var installed = new List<string>();
        foreach (string packageId in sdkComponent.Packages)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            Emit("sdkmanager", packageId);
            AndroidToolchainProcessResult run = await RunSdkManagerAsync(runner, sdkmanager, sdkRoot,
                packageId, environment, options.CancellationToken).ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                // Self-heal one round: Google can add a new license hash; the error names it.
                string? missingHash = ExtractLicenseHash(run.CombinedOutput);
                if (missingHash != null)
                {
                    AppendLicenseHash(licenseRoot, "android-sdk-license", missingHash);
                    run = await RunSdkManagerAsync(runner, sdkmanager, sdkRoot, packageId,
                        environment, options.CancellationToken).ConfigureAwait(false);
                }
                if (run.ExitCode != 0)
                    return new AndroidChannelResult
                    {
                        Core = core,
                        Error = $"sdkmanager failed for '{packageId}' (exit {run.ExitCode}): {Trim(run.CombinedOutput, 400)}",
                    };
            }
            installed.Add(packageId);
        }

        string sentinel = sdkComponent.Verify == null
            ? ""
            : Path.Combine(sdkRoot, sdkComponent.Verify.Replace('/', Path.DirectorySeparatorChar));
        if (sentinel.Length > 0 && !File.Exists(sentinel))
            return new AndroidChannelResult
            {
                Core = core,
                Error = $"sdkmanager reported success but the sentinel '{sdkComponent.Verify}' is missing.",
            };

        // 4. Record state with the component's spec fingerprint so reruns reuse without sdkmanager.
        string statePath = Path.Combine(core.InstallRoot, ".state", sdkComponent.Id + ".json");
        ToolchainInstallState.Write(statePath, new ToolchainInstallState
        {
            Id = sdkComponent.Id,
            Version = sdkComponent.Version,
            Fingerprint = ToolchainInstallState.ComputeFingerprint(sdkComponent),
            SourceUrl = "sdkmanager",
            Sha256 = null,
            InstalledAtUtc = DateTime.UtcNow.ToString("O"),
            Root = sdkRoot,
            ManagedBy = "sdkmanager",
        });

        // 5. Write back the roots the resolver consumes — only into empty or already-managed slots.
        if (options.WriteEditorSettings)
        {
            EditorSettings settings = EditorSettings.Instance;
            string jdkRoot = jdk?.Root ?? "";
            string bundletool = core.Components.FirstOrDefault(c => c.Id == "bundletool")?.Root ?? "";
            settings.AndroidJavaSdkRoot = ManageSetting(settings.AndroidJavaSdkRoot, jdkRoot, core.InstallRoot);
            settings.AndroidSdkRoot = ManageSetting(settings.AndroidSdkRoot, sdkRoot, core.InstallRoot);
            settings.AndroidBundletoolPath = string.IsNullOrEmpty(bundletool)
                ? settings.AndroidBundletoolPath
                : ManageSetting(settings.AndroidBundletoolPath, bundletool, core.InstallRoot);
            settings.Save();
        }

        Emit("installed", $"{installed.Count} sdk packages ready under {sdkRoot}");
        return new AndroidChannelResult { Core = core, InstalledPackages = installed };
    }

    public static bool LicensesAccepted(string licenseRoot)
    {
        foreach ((string file, _) in LicenseFiles)
            if (!File.Exists(Path.Combine(licenseRoot, file))) return false;
        return true;
    }

    private static void WriteAcceptedLicenses(string licenseRoot)
    {
        Directory.CreateDirectory(licenseRoot);
        foreach ((string file, string[] hashes) in LicenseFiles)
            File.WriteAllLines(Path.Combine(licenseRoot, file), hashes);
    }

    private static void AppendLicenseHash(string licenseRoot, string file, string hash)
    {
        Directory.CreateDirectory(licenseRoot);
        string path = Path.Combine(licenseRoot, file);
        var lines = new List<string>();
        if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path));
        if (!lines.Contains(hash, StringComparer.OrdinalIgnoreCase)) lines.Add(hash);
        File.WriteAllLines(path, lines);
    }

    private static async Task<AndroidToolchainProcessResult> RunSdkManagerAsync(
        IAndroidToolchainProcessRunner runner, string sdkmanager, string sdkRoot, string packageId,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellation)
        => await runner.RunAsync(
            sdkmanager,
            // No trailing separator: `sdk\"` would escape the runner's closing quote on cmd.exe and
            // merge the package id into the path argument.
            new[] { $"--sdk_root={sdkRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}", packageId },
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(sdkmanager)))!,
            environment,
            // NDK downloads extract for minutes; the runner's timeout is a hard kill so keep it generous.
            TimeSpan.FromMinutes(30),
            cancellation).ConfigureAwait(false);

    private static string? ExtractLicenseHash(string output)
    {
        Match match = Regex.Match(output, @"[0-9a-f]{40}", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    /// <summary>Keep user-configured values; take ownership only of empty slots or paths that
    /// already live inside our managed install root.</summary>
    private static string ManageSetting(string current, string managed, string installRoot)
    {
        if (string.IsNullOrWhiteSpace(managed)) return current;
        if (string.IsNullOrWhiteSpace(current)) return managed;
        return current.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase) ? managed : current;
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
