// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.Android;

internal sealed class AndroidToolchainPathOverrides
{
    public string? DotNetRoot { get; init; }
    public string? JavaSdkRoot { get; init; }
    public string? AndroidSdkRoot { get; init; }
    public string? AndroidNdkRoot { get; init; }
    public string? BundletoolPath { get; init; }
}

internal sealed class AndroidToolchainResolveOptions
{
    public AndroidToolchainPathOverrides CommandLineOverrides { get; init; } = new();
    public AndroidToolchainPathOverrides UserPreferences { get; init; } = new();
    public string? ImportedToolchainRoot { get; init; }
    public string ToolchainCacheRoot { get; init; } = AndroidToolchainBootstrapper.GetDefaultDestinationRoot();
    public string? LockFilePath { get; init; }
    public int TargetApi { get; init; } = 36;
    public string RuntimeIdentifier { get; init; } = "android-arm64";
    public bool RunDryRun { get; init; } = true;
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public string? TemporaryRoot { get; init; }
}

internal readonly record struct AndroidToolchainResolveProgress(string Stage, string Detail, int Completed, int Total);

internal sealed class AndroidToolchainResolver(IAndroidToolchainProcessRunner? processRunner = null)
{
    private const int ProbeCount = 8;
    private const int OutputTailLength = 4096;

    private static readonly Regex s_javaVersionRegex = new(
        "version\\s+\\\"(?<version>[^\\\"]+)\\\"",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly IAndroidToolchainProcessRunner _processRunner =
        processRunner ?? new AndroidToolchainProcessRunner();

    internal async Task<AndroidToolchainSnapshot> ResolveAsync(
        AndroidToolchainResolveOptions options,
        IProgress<AndroidToolchainResolveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        AndroidToolchainHostLock host = ResolveHost();
        if (!host.Supported)
            throw new PlatformNotSupportedException("Android builds currently support only Windows x64 build hosts.");

        progress?.Report(new AndroidToolchainResolveProgress("Cache", "Verifying imported toolchain", 0, ProbeCount));
        (string importedRoot, AndroidToolchainImportManifest? importManifest) =
            await ResolveImportedCacheAsync(options, cancellationToken).ConfigureAwait(false);

        ResolvedLocation dotnetRoot = ResolveDirectory(
            "dotnet",
            options.CommandLineOverrides.DotNetRoot,
            options.UserPreferences.DotNetRoot,
            string.Empty,
            importedRoot: string.Empty,
            GetStandardDotNetRoots(),
            static path => File.Exists(GetDotNetExecutable(path)));
        ResolvedLocation javaRoot = ResolveDirectory(
            "JDK",
            options.CommandLineOverrides.JavaSdkRoot,
            options.UserPreferences.JavaSdkRoot,
            "OpenJDK",
            importedRoot,
            GetStandardJavaRoots(),
            static path => File.Exists(GetJavaExecutable(path)) && File.Exists(Path.Combine(path, "release")));
        ResolvedLocation sdkRoot = ResolveDirectory(
            "Android SDK",
            options.CommandLineOverrides.AndroidSdkRoot,
            options.UserPreferences.AndroidSdkRoot,
            "SDK",
            importedRoot,
            GetStandardSdkRoots(),
            static path => Directory.Exists(Path.Combine(path, "platform-tools")) && Directory.Exists(Path.Combine(path, "build-tools")));
        ResolvedLocation ndkRoot = ResolveDirectory(
            "Android NDK",
            options.CommandLineOverrides.AndroidNdkRoot,
            options.UserPreferences.AndroidNdkRoot,
            "NDK",
            importedRoot,
            GetStandardNdkRoots(sdkRoot.Path),
            static path => File.Exists(Path.Combine(path, "source.properties")) && FindNdkClang(path) != null);
        ResolvedLocation bundletool = ResolveFile(
            "bundletool",
            options.CommandLineOverrides.BundletoolPath,
            options.UserPreferences.BundletoolPath,
            FindImportedBundletool(importedRoot),
            importedRoot,
            GetStandardBundletoolPaths(options.ToolchainCacheRoot));

        string dotnet = GetDotNetExecutable(dotnetRoot.Path);
        string java = GetJavaExecutable(javaRoot.Path);
        string buildToolsVersion = ResolveHighestVersionDirectory(Path.Combine(sdkRoot.Path, "build-tools"), "Android build-tools");
        string buildToolsRoot = Path.Combine(sdkRoot.Path, "build-tools", buildToolsVersion);
        string platformRoot = Path.Combine(sdkRoot.Path, "platforms", "android-" + options.TargetApi.ToString(CultureInfo.InvariantCulture));
        if (!Directory.Exists(platformRoot))
            throw new DirectoryNotFoundException($"Android SDK platform android-{options.TargetApi} is missing: {platformRoot}");
        string commandLineToolsVersion = ResolveHighestVersionDirectory(Path.Combine(sdkRoot.Path, "cmdline-tools"), "Android command-line tools");
        string commandLineToolsRoot = Path.Combine(sdkRoot.Path, "cmdline-tools", commandLineToolsVersion);
        string aapt2 = RequireFile(buildToolsRoot, OperatingSystem.IsWindows() ? "aapt2.exe" : "aapt2");
        string apksigner = RequireFile(buildToolsRoot, OperatingSystem.IsWindows() ? "apksigner.bat" : "apksigner");
        string zipalign = RequireFile(buildToolsRoot, OperatingSystem.IsWindows() ? "zipalign.exe" : "zipalign");
        string adb = RequireFile(Path.Combine(sdkRoot.Path, "platform-tools"), OperatingSystem.IsWindows() ? "adb.exe" : "adb");
        string sdkmanager = RequireFile(Path.Combine(commandLineToolsRoot, "bin"), OperatingSystem.IsWindows() ? "sdkmanager.bat" : "sdkmanager");
        string clang = FindNdkClang(ndkRoot.Path)
            ?? throw new FileNotFoundException("Android NDK clang executable is missing.", ndkRoot.Path);

        string temporaryParent = ResolveTemporaryParent(options.TemporaryRoot);
        string temporaryRoot = Path.Combine(temporaryParent, "android-toolchain-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            IReadOnlyDictionary<string, string> environment = CreateEnvironment(
                dotnetRoot.Path,
                javaRoot.Path,
                sdkRoot.Path,
                ndkRoot.Path);
            var probes = new List<AndroidToolchainProbeLock>(ProbeCount);

            AndroidToolchainProcessResult dotnetInfo = await RunRequiredProbeAsync(
                probes, "dotnet-info", dotnet, ["--info"], temporaryRoot, environment, options.ToolTimeout,
                progress, 1, cancellationToken).ConfigureAwait(false);
            string dotNetSdkVersion = ParseDotNetSdkVersion(dotnetInfo.CombinedOutput);
            string dotNetRuntimeVersion = ParseDotNetRuntimeVersion(dotnetInfo.CombinedOutput);

            AndroidToolchainProcessResult workloadList = await RunRequiredProbeAsync(
                probes, "dotnet-workload", dotnet, ["workload", "list"], temporaryRoot, environment, options.ToolTimeout,
                progress, 2, cancellationToken).ConfigureAwait(false);
            string workloadVersion = ParseAndroidWorkloadVersion(workloadList.CombinedOutput);
            string workloadManifestPath = ResolveWorkloadManifestPath(dotnetRoot.Path, dotnetInfo.CombinedOutput, workloadVersion);

            AndroidToolchainProcessResult javaVersionResult = await RunRequiredProbeAsync(
                probes, "java", java, ["-version"], temporaryRoot, environment, options.ToolTimeout,
                progress, 3, cancellationToken).ConfigureAwait(false);
            string javaVersion = ParseJavaVersion(javaVersionResult.CombinedOutput);
            if (!TryParseMajorVersion(javaVersion, out int javaMajor) || javaMajor < 17)
            {
                throw new InvalidDataException(
                    $"Android workload {workloadVersion} requires JDK 17 or newer, but '{javaRoot.Path}' provides {javaVersion}. " +
                    "Override only the JDK component; do not fall back to the global JAVA_HOME.");
            }

            await RunRequiredProbeAsync(probes, "aapt2", aapt2, ["version"], temporaryRoot, environment, options.ToolTimeout,
                progress, 4, cancellationToken).ConfigureAwait(false);
            await RunRequiredProbeAsync(probes, "apksigner", apksigner, ["version"], temporaryRoot, environment, options.ToolTimeout,
                progress, 5, cancellationToken).ConfigureAwait(false);
            await RunRequiredProbeAsync(probes, "adb", adb, ["version"], temporaryRoot, environment, options.ToolTimeout,
                progress, 6, cancellationToken).ConfigureAwait(false);

            string isolatedSdkRoot = Path.Combine(temporaryRoot, "sdk-probe");
            Directory.CreateDirectory(isolatedSdkRoot);
            await RunRequiredProbeAsync(
                probes,
                "sdkmanager",
                sdkmanager,
                ["--sdk_root=" + isolatedSdkRoot, "--version"],
                temporaryRoot,
                environment,
                options.ToolTimeout,
                progress,
                7,
                cancellationToken).ConfigureAwait(false);

            AndroidToolchainProcessResult clangVersionResult = await RunRequiredProbeAsync(
                probes, "clang", clang, ["--version"], temporaryRoot, environment, options.ToolTimeout,
                progress, 8, cancellationToken).ConfigureAwait(false);
            string ndkVersion = ReadProperty(Path.Combine(ndkRoot.Path, "source.properties"), "Pkg.Revision");
            string bundletoolVersion = ParseBundletoolVersion(bundletool.Path);
            AndroidToolchainProcessResult bundletoolVersionResult = await _processRunner.RunAsync(
                java,
                ["-jar", bundletool.Path, "version"],
                temporaryRoot,
                environment,
                options.ToolTimeout,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess("bundletool", bundletoolVersionResult);
            probes.Add(CreateProbeLock("bundletool", java, ["-jar", bundletool.Path, "version"], bundletoolVersionResult));

            if (importManifest != null)
                AndroidToolchainCacheVerifier.VerifyDeclaredFileSet(importedRoot, importManifest);

            progress?.Report(new AndroidToolchainResolveProgress("Dry run", "Building minimal net10.0-android APK", ProbeCount, ProbeCount));
            AndroidToolchainDryRunLock dryRun = options.RunDryRun
                ? await RunDryRunAsync(
                    options,
                    dotnet,
                    javaRoot.Path,
                    sdkRoot.Path,
                    ndkRoot.Path,
                    temporaryRoot,
                    environment,
                    cancellationToken).ConfigureAwait(false)
                : new AndroidToolchainDryRunLock(
                    false,
                    "net10.0-android",
                    options.RuntimeIdentifier,
                    options.TargetApi,
                    0,
                    string.Empty,
                    0,
                    string.Empty,
                    "Skipped by caller");

            var components = new List<AndroidToolchainComponentLock>
            {
                await CreateComponentAsync("dotnet", dotnet, dotNetSdkVersion, dotnetRoot, cancellationToken).ConfigureAwait(false),
                await CreateComponentAsync("dotnet-android-workload", workloadManifestPath, workloadVersion, dotnetRoot, cancellationToken).ConfigureAwait(false),
                await CreateComponentAsync("java", java, javaVersion, javaRoot, cancellationToken).ConfigureAwait(false),
                await CreateComponentAsync("aapt2", aapt2, buildToolsVersion, sdkRoot, cancellationToken).ConfigureAwait(false),
                await CreateComponentAsync("apksigner", apksigner, buildToolsVersion, sdkRoot, cancellationToken).ConfigureAwait(false),
                await CreateComponentAsync("zipalign", zipalign, buildToolsVersion, sdkRoot, cancellationToken).ConfigureAwait(false),
                await CreateComponentAsync("adb", adb, ReadProperty(Path.Combine(sdkRoot.Path, "platform-tools", "source.properties"), "Pkg.Revision"), sdkRoot, cancellationToken).ConfigureAwait(false),
                await CreateComponentAsync("sdkmanager", sdkmanager, commandLineToolsVersion, sdkRoot, cancellationToken).ConfigureAwait(false),
                await CreateComponentAsync("clang", clang, ndkVersion + " / " + FirstLine(clangVersionResult.CombinedOutput), ndkRoot, cancellationToken).ConfigureAwait(false),
                await CreateComponentAsync("bundletool", bundletool.Path, bundletoolVersion, bundletool, cancellationToken).ConfigureAwait(false),
            };

            List<AndroidSdkPackageLock> packages = await CreateSdkPackagesAsync(
                sdkRoot.Path,
                buildToolsVersion,
                commandLineToolsVersion,
                options.TargetApi,
                importManifest,
                cancellationToken).ConfigureAwait(false);
            List<AndroidLicenseLock> licenses = await ReadLicensesAsync(sdkRoot.Path, cancellationToken).ConfigureAwait(false);

            var value = new AndroidToolchainLock
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Host = host,
                DotNetSdkVersion = dotNetSdkVersion,
                DotNetRuntimeVersion = dotNetRuntimeVersion,
                AndroidWorkloadVersion = workloadVersion,
                AndroidWorkloadManifestPath = workloadManifestPath,
                ImportedCachePath = importedRoot,
                ImportedCacheFingerprint = importManifest?.Fingerprint ?? string.Empty,
                Components = components,
                SdkPackages = packages,
                Licenses = licenses,
                Probes = probes,
                DryRun = dryRun,
            };
            value.Fingerprint = AndroidToolchainLockFile.ComputeFingerprint(value);
            var snapshot = new AndroidToolchainSnapshot(
                value,
                dotnet,
                javaRoot.Path,
                sdkRoot.Path,
                ndkRoot.Path,
                bundletool.Path);
            if (!string.IsNullOrWhiteSpace(options.LockFilePath))
                await AndroidToolchainLockFile.WriteAsync(options.LockFilePath, snapshot, cancellationToken).ConfigureAwait(false);
            progress?.Report(new AndroidToolchainResolveProgress("Complete", value.Fingerprint, ProbeCount, ProbeCount));
            return snapshot;
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot, temporaryParent);
        }
    }

    private async Task<AndroidToolchainDryRunLock> RunDryRunAsync(
        AndroidToolchainResolveOptions options,
        string dotnet,
        string javaRoot,
        string sdkRoot,
        string ndkRoot,
        string temporaryRoot,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        string projectRoot = Path.Combine(temporaryRoot, "minimal-android");
        Directory.CreateDirectory(projectRoot);
        string projectPath = Path.Combine(projectRoot, "XEngine.Android.ToolchainProbe.csproj");
        string sourcePath = Path.Combine(projectRoot, "MainActivity.cs");
        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-android</TargetFramework>
                <OutputType>Exe</OutputType>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <ApplicationId>com.xengine.toolchainprobe</ApplicationId>
                <ApplicationVersion>1</ApplicationVersion>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
                <AndroidPackageFormat>apk</AndroidPackageFormat>
              </PropertyGroup>
            </Project>
            """, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(sourcePath, """
            using Android.App;
            using Android.OS;

            namespace XEngine.Android.ToolchainProbe;

            [Activity(Label = "XEngine Toolchain Probe", MainLauncher = true, Exported = true)]
            public sealed class MainActivity : Activity
            {
                protected override void OnCreate(Bundle? savedInstanceState)
                {
                    base.OnCreate(savedInstanceState);
                }
            }
            """, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        string[] arguments =
        [
            "build",
            projectPath,
            "-c", "Release",
            "-r", options.RuntimeIdentifier,
            "-p:JavaSdkDirectory=" + javaRoot,
            "-p:AndroidSdkDirectory=" + sdkRoot,
            "-p:AndroidNdkDirectory=" + ndkRoot,
            "-p:AndroidUseLatestPlatformSdk=false",
            "-p:PublishTrimmed=false",
            "-p:RunAOTCompilation=false",
            "--nologo",
            "-v:minimal",
        ];
        AndroidToolchainProcessResult result = await _processRunner.RunAsync(
            dotnet,
            arguments,
            projectRoot,
            environment,
            options.BuildTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Minimal net10.0-android dry-run failed. The lock was not written." + Environment.NewLine + Tail(result.CombinedOutput));
        }

        string outputRoot = Path.Combine(projectRoot, "bin", "Release", "net10.0-android", options.RuntimeIdentifier);
        string[] artifacts = Directory.Exists(outputRoot)
            ? Directory.GetFiles(outputRoot, "*.apk", SearchOption.AllDirectories)
            : [];
        if (artifacts.Length == 0)
            throw new InvalidDataException("Minimal net10.0-android dry-run succeeded but produced no APK.");
        Array.Sort(artifacts, StringComparer.OrdinalIgnoreCase);
        string artifact = artifacts[^1];
        var info = new FileInfo(artifact);
        return new AndroidToolchainDryRunLock(
            true,
            "net10.0-android",
            options.RuntimeIdentifier,
            options.TargetApi,
            result.DurationMilliseconds,
            info.Name,
            info.Length,
            await HashFileAsync(artifact, cancellationToken).ConfigureAwait(false),
            string.Empty);
    }

    private async Task<AndroidToolchainProcessResult> RunRequiredProbeAsync(
        List<AndroidToolchainProbeLock> probes,
        string id,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        IProgress<AndroidToolchainResolveProgress>? progress,
        int completed,
        CancellationToken cancellationToken)
    {
        AndroidToolchainProcessResult result = await _processRunner.RunAsync(
            executable,
            arguments,
            workingDirectory,
            environment,
            timeout,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(id, result);
        probes.Add(CreateProbeLock(id, executable, arguments, result));
        progress?.Report(new AndroidToolchainResolveProgress("Probe", id, completed, ProbeCount));
        return result;
    }

    private static void EnsureSuccess(string id, AndroidToolchainProcessResult result)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Android toolchain probe '{id}' failed with exit code {result.ExitCode}: {Tail(result.CombinedOutput)}");
    }

    private static AndroidToolchainProbeLock CreateProbeLock(
        string id,
        string executable,
        IReadOnlyList<string> arguments,
        AndroidToolchainProcessResult result) =>
        new(id, executable, JoinArguments(arguments), result.ExitCode, result.DurationMilliseconds, Tail(result.CombinedOutput));

    private static async Task<(string Root, AndroidToolchainImportManifest? Manifest)> ResolveImportedCacheAsync(
        AndroidToolchainResolveOptions options,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.ImportedToolchainRoot))
        {
            string explicitRoot = NormalizeDirectory(options.ImportedToolchainRoot);
            AndroidToolchainImportManifest manifest = await AndroidToolchainCacheVerifier
                .VerifyAsync(explicitRoot, cancellationToken)
                .ConfigureAwait(false);
            return (explicitRoot, manifest);
        }

        if (!Directory.Exists(options.ToolchainCacheRoot))
            return (string.Empty, null);
        string[] manifests = Directory.GetFiles(
            options.ToolchainCacheRoot,
            AndroidToolchainBootstrapper.ManifestFileName,
            SearchOption.TopDirectoryOnly);
        if (manifests.Length == 0)
        {
            string[] directories = Directory.GetDirectories(options.ToolchainCacheRoot);
            var discovered = new List<string>(directories.Length);
            for (int i = 0; i < directories.Length; i++)
            {
                string manifestPath = Path.Combine(directories[i], AndroidToolchainBootstrapper.ManifestFileName);
                if (File.Exists(manifestPath))
                    discovered.Add(manifestPath);
            }
            manifests = [.. discovered];
        }
        if (manifests.Length == 0)
            return (string.Empty, null);

        Array.Sort(manifests, static (left, right) =>
            File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
        string root = Path.GetDirectoryName(manifests[0])!;
        AndroidToolchainImportManifest selected = await AndroidToolchainCacheVerifier
            .VerifyAsync(root, cancellationToken)
            .ConfigureAwait(false);
        return (root, selected);
    }

    private static ResolvedLocation ResolveDirectory(
        string id,
        string? commandLineOverride,
        string? userPreference,
        string importedRelativePath,
        string importedRoot,
        IEnumerable<string> standardPaths,
        Func<string, bool> validate)
    {
        if (!string.IsNullOrWhiteSpace(commandLineOverride))
            return RequireDirectoryLocation(id, commandLineOverride, AndroidToolchainSourceKind.CommandLineOverride, validate);
        if (!string.IsNullOrWhiteSpace(userPreference))
            return RequireDirectoryLocation(id, userPreference, AndroidToolchainSourceKind.UserPreference, validate);
        if (!string.IsNullOrWhiteSpace(importedRoot) && !string.IsNullOrWhiteSpace(importedRelativePath))
        {
            string path = Path.Combine(importedRoot, importedRelativePath);
            if (validate(path))
                return new ResolvedLocation(Path.GetFullPath(path), AndroidToolchainSourceKind.UnityImport, importedRoot);
        }
        foreach (string path in standardPaths)
        {
            if (!string.IsNullOrWhiteSpace(path) && validate(path))
                return new ResolvedLocation(Path.GetFullPath(path), AndroidToolchainSourceKind.StandardInstall, Path.GetFullPath(path));
        }
        throw new DirectoryNotFoundException(
            $"Unable to resolve {id}. Discovery order is CLI override, user preference, verified XEngine import, then standard install locations.");
    }

    private static ResolvedLocation RequireDirectoryLocation(
        string id,
        string value,
        AndroidToolchainSourceKind source,
        Func<string, bool> validate)
    {
        string path = NormalizeDirectory(value);
        if (!validate(path))
            throw new DirectoryNotFoundException($"Configured {id} path is invalid: {path}");
        return new ResolvedLocation(path, source, path);
    }

    private static ResolvedLocation ResolveFile(
        string id,
        string? commandLineOverride,
        string? userPreference,
        string importedPath,
        string importedRoot,
        IEnumerable<string> standardPaths)
    {
        if (!string.IsNullOrWhiteSpace(commandLineOverride))
            return RequireFileLocation(id, commandLineOverride, AndroidToolchainSourceKind.CommandLineOverride);
        if (!string.IsNullOrWhiteSpace(userPreference))
            return RequireFileLocation(id, userPreference, AndroidToolchainSourceKind.UserPreference);
        if (!string.IsNullOrWhiteSpace(importedPath) && File.Exists(importedPath))
            return new ResolvedLocation(Path.GetFullPath(importedPath), AndroidToolchainSourceKind.UnityImport, importedRoot);
        foreach (string path in standardPaths)
        {
            if (File.Exists(path))
                return new ResolvedLocation(Path.GetFullPath(path), AndroidToolchainSourceKind.StandardInstall, Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
        throw new FileNotFoundException(
            $"Unable to resolve {id}. Discovery order is CLI override, user preference, verified XEngine import, then standard install locations.");
    }

    private static ResolvedLocation RequireFileLocation(string id, string value, AndroidToolchainSourceKind source)
    {
        string path = Path.GetFullPath(value);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Configured {id} path is invalid.", path);
        return new ResolvedLocation(path, source, Path.GetDirectoryName(path)!);
    }

    private static IEnumerable<string> GetStandardDotNetRoots()
    {
        yield return Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
    }

    private static IEnumerable<string> GetStandardJavaRoots()
    {
        yield return Environment.GetEnvironmentVariable("JAVA_HOME") ?? string.Empty;
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (string parent in new[] { Path.Combine(programFiles, "Microsoft"), Path.Combine(programFiles, "Eclipse Adoptium"), Path.Combine(programFiles, "Java") })
        {
            if (!Directory.Exists(parent))
                continue;
            string[] directories = Directory.GetDirectories(parent, "*17*", SearchOption.TopDirectoryOnly);
            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            for (int i = directories.Length - 1; i >= 0; i--)
                yield return directories[i];
        }
    }

    private static IEnumerable<string> GetStandardSdkRoots()
    {
        yield return Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? string.Empty;
        yield return Environment.GetEnvironmentVariable("ANDROID_HOME") ?? string.Empty;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
    }

    private static IEnumerable<string> GetStandardNdkRoots(string sdkRoot)
    {
        yield return Environment.GetEnvironmentVariable("ANDROID_NDK_ROOT") ?? string.Empty;
        yield return Environment.GetEnvironmentVariable("ANDROID_NDK_HOME") ?? string.Empty;
        string ndkParent = Path.Combine(sdkRoot, "ndk");
        if (Directory.Exists(ndkParent))
        {
            string[] directories = Directory.GetDirectories(ndkParent);
            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            for (int i = directories.Length - 1; i >= 0; i--)
                yield return directories[i];
        }
        yield return Path.Combine(sdkRoot, "ndk-bundle");
    }

    private static IEnumerable<string> GetStandardBundletoolPaths(string cacheRoot)
    {
        yield return Path.Combine(cacheRoot, "bundletool.jar");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "tools", "bundletool.jar");
    }

    private static string FindImportedBundletool(string importedRoot)
    {
        if (string.IsNullOrWhiteSpace(importedRoot))
            return string.Empty;
        string tools = Path.Combine(importedRoot, "Tools");
        if (!Directory.Exists(tools))
            return string.Empty;
        string[] paths = Directory.GetFiles(tools, "bundletool*.jar", SearchOption.TopDirectoryOnly);
        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
        return paths.Length == 0 ? string.Empty : paths[^1];
    }

    private static string GetDotNetExecutable(string root) =>
        File.Exists(root) ? Path.GetFullPath(root) : Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

    private static string GetJavaExecutable(string root) =>
        Path.Combine(root, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");

    private static string? FindNdkClang(string root)
    {
        string prebuilt = Path.Combine(root, "toolchains", "llvm", "prebuilt");
        if (!Directory.Exists(prebuilt))
            return null;
        string fileName = OperatingSystem.IsWindows() ? "clang.exe" : "clang";
        string[] paths = Directory.GetFiles(prebuilt, fileName, SearchOption.AllDirectories);
        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
        return paths.Length == 0 ? null : paths[0];
    }

    private static string ResolveHighestVersionDirectory(string parent, string description)
    {
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException($"{description} directory is missing: {parent}");
        string[] directories = Directory.GetDirectories(parent);
        Array.Sort(directories, static (left, right) => CompareVersionDirectory(left, right));
        if (directories.Length == 0)
            throw new DirectoryNotFoundException($"No {description} versions were found under: {parent}");
        return Path.GetFileName(directories[^1]);
    }

    private static int CompareVersionDirectory(string left, string right)
    {
        string leftName = Path.GetFileName(left);
        string rightName = Path.GetFileName(right);
        if (Version.TryParse(leftName, out Version? leftVersion) && Version.TryParse(rightName, out Version? rightVersion))
            return leftVersion.CompareTo(rightVersion);
        if (string.Equals(leftName, "latest", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (string.Equals(rightName, "latest", StringComparison.OrdinalIgnoreCase))
            return -1;
        return StringComparer.OrdinalIgnoreCase.Compare(leftName, rightName);
    }

    private static string ResolveWorkloadManifestPath(string dotnetRoot, string dotnetInfo, string workloadVersion)
    {
        Match pathMatch = Regex.Match(dotnetInfo, "Manifest Path:\\s*(?<path>.+WorkloadManifest\\.json)", RegexOptions.IgnoreCase);
        if (pathMatch.Success && File.Exists(pathMatch.Groups["path"].Value.Trim()))
            return Path.GetFullPath(pathMatch.Groups["path"].Value.Trim());

        string manifestsRoot = Path.Combine(dotnetRoot, "sdk-manifests");
        if (!Directory.Exists(manifestsRoot))
            throw new DirectoryNotFoundException($".NET workload manifest root is missing: {manifestsRoot}");
        string[] paths = Directory.GetFiles(manifestsRoot, "WorkloadManifest.json", SearchOption.AllDirectories);
        for (int i = 0; i < paths.Length; i++)
        {
            if (paths[i].Contains("microsoft.net.sdk.android", StringComparison.OrdinalIgnoreCase) &&
                paths[i].Contains(workloadVersion.Split('/')[0], StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(paths[i]);
            }
        }
        throw new FileNotFoundException($"Android workload manifest {workloadVersion} was not found under {manifestsRoot}.");
    }

    private static string ParseDotNetSdkVersion(string output)
    {
        Match match = Regex.Match(output, "\\bVersion:\\s*(?<version>10\\.[^\\s]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["version"].Value : throw new InvalidDataException("Unable to parse .NET SDK 10 version from dotnet --info.");
    }

    private static string ParseDotNetRuntimeVersion(string output)
    {
        Match match = Regex.Match(output, "Host:\\s*\\r?\\n\\s*Version:\\s*(?<version>[^\\s]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["version"].Value : "unknown";
    }

    private static string ParseAndroidWorkloadVersion(string output)
    {
        Match match = Regex.Match(output, "(?m)^\\s*android\\s+(?<version>[^\\s]+)", RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new InvalidDataException("The selected .NET SDK does not have the Android workload installed.");
        return match.Groups["version"].Value;
    }

    private static string ParseJavaVersion(string output)
    {
        Match match = s_javaVersionRegex.Match(output);
        return match.Success ? match.Groups["version"].Value : throw new InvalidDataException("Unable to parse java -version output.");
    }

    private static bool TryParseMajorVersion(string version, out int major)
    {
        string first = version.Split('.')[0];
        return int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out major);
    }

    private static string ParseBundletoolVersion(string path)
    {
        Match match = Regex.Match(Path.GetFileName(path), "bundletool(?:-all)?-(?<version>[0-9.]+)\\.jar", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["version"].Value : "unknown";
    }

    private static async Task<AndroidToolchainComponentLock> CreateComponentAsync(
        string id,
        string path,
        string version,
        ResolvedLocation location,
        CancellationToken cancellationToken) =>
        new(id, Path.GetFullPath(path), version, await HashFileAsync(path, cancellationToken).ConfigureAwait(false), location.Source, location.SourceRoot);

    private static async Task<List<AndroidSdkPackageLock>> CreateSdkPackagesAsync(
        string sdkRoot,
        string buildToolsVersion,
        string commandLineToolsVersion,
        int targetApi,
        AndroidToolchainImportManifest? importManifest,
        CancellationToken cancellationToken)
    {
        string buildToolsPath = Path.Combine(sdkRoot, "build-tools", buildToolsVersion);
        string platformPath = Path.Combine(sdkRoot, "platforms", "android-" + targetApi.ToString(CultureInfo.InvariantCulture));
        string platformToolsPath = Path.Combine(sdkRoot, "platform-tools");
        string commandLineToolsPath = Path.Combine(sdkRoot, "cmdline-tools", commandLineToolsVersion);
        return
        [
            new AndroidSdkPackageLock("build-tools", buildToolsVersion, buildToolsPath,
                await ResolvePackageHashAsync(importManifest, "sdk-build-tools", Path.Combine(buildToolsPath, OperatingSystem.IsWindows() ? "aapt2.exe" : "aapt2"), cancellationToken).ConfigureAwait(false)),
            new AndroidSdkPackageLock("platform", "android-" + targetApi.ToString(CultureInfo.InvariantCulture), platformPath,
                await ResolvePackageHashAsync(importManifest, "sdk-platform", Path.Combine(platformPath, "android.jar"), cancellationToken).ConfigureAwait(false)),
            new AndroidSdkPackageLock("platform-tools", ReadProperty(Path.Combine(platformToolsPath, "source.properties"), "Pkg.Revision"), platformToolsPath,
                await ResolvePackageHashAsync(importManifest, "sdk-platform-tools", Path.Combine(platformToolsPath, OperatingSystem.IsWindows() ? "adb.exe" : "adb"), cancellationToken).ConfigureAwait(false)),
            new AndroidSdkPackageLock("cmdline-tools", commandLineToolsVersion, commandLineToolsPath,
                await ResolvePackageHashAsync(importManifest, "sdk-command-line-tools", Path.Combine(commandLineToolsPath, "bin", OperatingSystem.IsWindows() ? "sdkmanager.bat" : "sdkmanager"), cancellationToken).ConfigureAwait(false)),
        ];
    }

    private static async Task<string> ResolvePackageHashAsync(
        AndroidToolchainImportManifest? manifest,
        string componentId,
        string representativeFile,
        CancellationToken cancellationToken)
    {
        if (manifest != null)
        {
            for (int i = 0; i < manifest.Components.Count; i++)
            {
                if (string.Equals(manifest.Components[i].Id, componentId, StringComparison.Ordinal))
                    return manifest.Components[i].Sha256;
            }
        }
        return await HashFileAsync(representativeFile, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<AndroidLicenseLock>> ReadLicensesAsync(string sdkRoot, CancellationToken cancellationToken)
    {
        string licenseRoot = Path.Combine(sdkRoot, "licenses");
        if (!Directory.Exists(licenseRoot))
            throw new InvalidDataException("Android SDK licenses are missing. XEngine will not accept licenses non-interactively.");
        string[] paths = Directory.GetFiles(licenseRoot, "*", SearchOption.TopDirectoryOnly);
        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
        if (paths.Length == 0)
            throw new InvalidDataException("Android SDK license directory is empty. XEngine will not fabricate license acceptance.");
        var result = new List<AndroidLicenseLock>(paths.Length);
        for (int i = 0; i < paths.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new AndroidLicenseLock(
                Path.GetFileName(paths[i]),
                await HashFileAsync(paths[i], cancellationToken).ConfigureAwait(false),
                true));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> CreateEnvironment(
        string dotnetRoot,
        string javaRoot,
        string sdkRoot,
        string ndkRoot)
    {
        string existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_ROOT"] = dotnetRoot,
            ["JAVA_HOME"] = javaRoot,
            ["ANDROID_HOME"] = sdkRoot,
            ["ANDROID_SDK_ROOT"] = sdkRoot,
            ["ANDROID_NDK_ROOT"] = ndkRoot,
            ["PATH"] = string.Join(Path.PathSeparator, new[]
            {
                dotnetRoot,
                Path.Combine(javaRoot, "bin"),
                Path.Combine(sdkRoot, "platform-tools"),
                existingPath,
            }),
        };
    }

    private static AndroidToolchainHostLock ResolveHost()
    {
        string operatingSystem = RuntimeInformation.OSDescription;
        string architecture = RuntimeInformation.OSArchitecture.ToString();
        bool supported = OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;
        return new AndroidToolchainHostLock(
            operatingSystem,
            Environment.OSVersion.VersionString,
            architecture,
            supported);
    }

    private static void ValidateOptions(AndroidToolchainResolveOptions options)
    {
        if (options.TargetApi < 24)
            throw new ArgumentOutOfRangeException(nameof(options.TargetApi), "Android target API must be at least 24.");
        if (!string.Equals(options.RuntimeIdentifier, "android-arm64", StringComparison.Ordinal) &&
            !string.Equals(options.RuntimeIdentifier, "android-x64", StringComparison.Ordinal) &&
            !string.Equals(options.RuntimeIdentifier, "android-arm", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unsupported Android runtime identifier: {options.RuntimeIdentifier}", nameof(options));
        }
        if (options.ToolTimeout <= TimeSpan.Zero || options.BuildTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Android toolchain probe timeouts must be positive.");
    }

    private static string ResolveTemporaryParent(string? configured)
    {
        string root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "XEngine", "AndroidToolchainProbes")
            : Path.GetFullPath(configured);
        Directory.CreateDirectory(root);
        return Path.TrimEndingDirectorySeparator(root);
    }

    private static void DeleteTemporaryDirectory(string path, string parent)
    {
        if (!Directory.Exists(path))
            return;
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        string relative = Path.GetRelativePath(normalizedParent, normalizedPath);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal) ||
            !Path.GetFileName(normalizedPath).StartsWith("android-toolchain-probe-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to delete unsafe Android toolchain probe directory: {path}");
        }
        Directory.Delete(normalizedPath, recursive: true);
    }

    private static string NormalizeDirectory(string path)
    {
        string candidate = Path.GetFullPath(path);
        if (File.Exists(candidate))
            candidate = Path.GetDirectoryName(candidate)!;
        return Path.TrimEndingDirectorySeparator(candidate);
    }

    private static string RequireFile(string root, string fileName)
    {
        string path = Path.Combine(root, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required Android toolchain file is missing: {path}", path);
        return Path.GetFullPath(path);
    }

    private static string ReadProperty(string path, string propertyName)
    {
        if (!File.Exists(path))
            return "unknown";
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            int equals = lines[i].IndexOf('=');
            if (equals >= 0 && string.Equals(lines[i][..equals].Trim(), propertyName, StringComparison.Ordinal))
                return lines[i][(equals + 1)..].Trim();
        }
        return "unknown";
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string JoinArguments(IReadOnlyList<string> arguments)
    {
        var result = new StringBuilder();
        for (int i = 0; i < arguments.Count; i++)
        {
            if (i > 0)
                result.Append(' ');
            string value = arguments[i];
            result.Append(value.Contains(' ', StringComparison.Ordinal) ? '"' + value + '"' : value);
        }
        return result.ToString();
    }

    private static string FirstLine(string value)
    {
        int newline = value.IndexOfAny(['\r', '\n']);
        return (newline < 0 ? value : value[..newline]).Trim();
    }

    private static string Tail(string value)
    {
        string normalized = value.Trim();
        return normalized.Length <= OutputTailLength ? normalized : normalized[^OutputTailLength..];
    }

    private sealed record ResolvedLocation(string Path, AndroidToolchainSourceKind Source, string SourceRoot);
}
