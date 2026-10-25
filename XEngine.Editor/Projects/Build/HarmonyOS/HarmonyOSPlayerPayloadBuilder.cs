// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>Inputs for publishing a NativeAOT player solely from the imported Bundle feed.</summary>
internal sealed class HarmonyOSPlayerPublishRequest
{
    public string PlayerProjectPath { get; init; } = string.Empty;
    public string BundleRoot { get; init; } = string.Empty;
    public HarmonyOSSdkBundleLock? BundleLock { get; init; }
    public string BundleFingerprint { get; init; } = string.Empty;
    public string RuntimeIdentifier { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public string IntermediateDirectory { get; init; } = string.Empty;
    /// <summary>Path to the official OpenHarmony SDK root (passed to PublishAotCross as OpenHarmonySdkRoot).</summary>
    public string OpenHarmonySdkRoot { get; init; } = string.Empty;
    /// <summary>Target OpenHarmony API level (OpenHarmonyApiLevel).</summary>
    public int OpenHarmonyApiLevel { get; init; }
    /// <summary>Target OpenHarmony ABI, e.g. arm64-v8a (OpenHarmonyAbi).</summary>
    public string OpenHarmonyAbi { get; init; } = string.Empty;
}

/// <summary>One hashed file emitted by a NativeAOT player publish.</summary>
internal sealed record HarmonyOSPlayerPayloadFile(string RelativePath, long Length, string Sha256);

/// <summary>Verified output of a NativeAOT player publish.</summary>
internal sealed class HarmonyOSPlayerPayloadResult
{
    public string PlayerLibraryPath { get; init; } = string.Empty;
    public string PlayerBuildId { get; init; } = string.Empty;
    public IReadOnlyList<HarmonyOSPlayerPayloadFile> Files { get; init; } = [];
    public HarmonyOSProcessResult Process { get; init; } = new(0, 0, string.Empty, string.Empty, string.Empty, false, false);
}

/// <summary>Publishes a NativeAOT player with no source other than the locked Bundle feed.</summary>
internal sealed class HarmonyOSPlayerPayloadBuilder(IHarmonyOSProcessRunner? processRunner = null)
{
    private const string PlayerLibraryFileName = "libxengine_player.so";

    /// <summary>
    /// .NET runtime native helper libraries the NativeAOT player links dynamically. These are
    /// shipped beside libxengine_player.so into entry/libs/&lt;abi&gt;/ and are expected payload,
    /// not unexpected extras. libc++_shared.so is the C++ runtime the player links.
    /// </summary>
    private static readonly HashSet<string> s_allowedRuntimeNativeLibraries = new(StringComparer.Ordinal)
    {
        "libSystem.Globalization.Native.so",
        "libSystem.IO.Compression.Native.so",
        "libSystem.IO.Ports.Native.so",
        "libSystem.Native.so",
        "libSystem.Security.Cryptography.Native.OpenSsl.so",
        "libc++_shared.so",
        "libSystem.Net.Security.Native.so",
    };
    private static readonly string[] s_requiredPackageIds =
    [
        "OpenHarmony.NET.Runtime.NativeAot.arm64-v8a",
        "OpenHarmony.NET.Runtime.NativeAot.x86_64",
        "OpenHarmony.NET.PublishAotCross",
        "OpenHarmony.NDK.Bindings",
        "OpenHarmony.NET.Player.Sdk",
        "OpenHarmony.NET.HapTooling",
    ];

    private static readonly UTF8Encoding s_utf8NoBom = new(false);
    private readonly IHarmonyOSProcessRunner _processRunner = processRunner ?? new HarmonyOSProcessRunner();

    internal async Task<HarmonyOSPlayerPayloadResult> PublishAsync(
        HarmonyOSPlayerPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        string feedDirectory = await ValidateRequestAsync(request, cancellationToken).ConfigureAwait(false);
        string outputDirectory = Path.GetFullPath(request.OutputDirectory);
        if (Directory.Exists(outputDirectory) && HasEntries(outputDirectory))
            throw new IOException($"HarmonyOS player output directory must be empty: {outputDirectory}");

        string intermediateDirectory = Path.GetFullPath(request.IntermediateDirectory);
        Directory.CreateDirectory(intermediateDirectory);
        string configPath = Path.Combine(intermediateDirectory, "nuget.locked.config");
        string intermediateRoot = Path.Combine(intermediateDirectory, "obj") + Path.DirectorySeparatorChar;
        string buildOutputRoot = Path.Combine(intermediateDirectory, "bin") + Path.DirectorySeparatorChar;
        // Isolate the NuGet package cache per build. NuGet keys its cache on package id + version, so a
        // repacked provider package whose version did not change is silently served from the global cache
        // (~/.nuget/packages), ignoring the freshly validated local feed. Pinning globalPackagesFolder to a
        // per-build directory under the shared intermediate root forces NuGet to extract from the local feed
        // every time, and the subsequent BuildHap/VerifyHap invocations (Restore=false) reuse these exact
        // extracted payloads via the same BaseIntermediateOutputPath. This mirrors how Blazor.Hybrid avoids
        // the cache entirely — it never restores at all because its packaging step has no .csproj.
        string packagesDirectory = Path.Combine(intermediateDirectory, "packages") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(packagesDirectory);
        await WriteNuGetConfigAsync(configPath, feedDirectory, packagesDirectory, cancellationToken).ConfigureAwait(false);

        HarmonyOSProcessResult process = await _processRunner.RunAsync(new HarmonyOSProcessCommand
        {
            ExecutablePath = "dotnet",
            Arguments =
            [
                "publish",
                Path.GetFullPath(request.PlayerProjectPath),
                "--configuration", "Release",
                "--runtime", request.RuntimeIdentifier,
                "--self-contained", "true",
                "--configfile", configPath,
                "--source", feedDirectory,
                "--property:BaseIntermediateOutputPath=" + intermediateRoot,
                "--property:BaseOutputPath=" + buildOutputRoot,
                // PublishAotCross's Crosscompile.targets resolves the official OpenHarmony SDK,
                // configures clang/sysroot, and derives the ABI from these properties.
                "--property:OpenHarmonySdkRoot=" + request.OpenHarmonySdkRoot,
                "--property:OpenHarmonyApiLevel=" + request.OpenHarmonyApiLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--property:OpenHarmonyAbi=" + request.OpenHarmonyAbi,
                // Force workstation GC: Server GC's multi-heap t_join synchronization asserts on
                // OpenHarmony's pthread/futex semantics. The csproj also sets this but the explicit
                // publish property guarantees it overrides Directory.Build.props.
                "--property:ServerGarbageCollection=false",
                // Allow restore: the ILCompiler and .NET runtime packs are transitive dependencies
                // that must be resolved from nuget.org. The locked config now includes both the
                // OHOS feed and nuget.org as sources.
                "--output", outputDirectory,
            ],
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(request.PlayerProjectPath))!,
        }, cancellationToken).ConfigureAwait(false);
        if (process.TimedOut)
            throw new TimeoutException("HarmonyOS NativeAOT player publish timed out.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException("HarmonyOS NativeAOT player publish failed: " + process.CombinedOutput);

        return await InspectPayloadAsync(outputDirectory, request.RuntimeIdentifier, process, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ValidateRequestAsync(
        HarmonyOSPlayerPublishRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PlayerProjectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BundleRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BundleFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RuntimeIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IntermediateDirectory);
        ArgumentNullException.ThrowIfNull(request.BundleLock);
        if (!File.Exists(request.PlayerProjectPath))
            throw new FileNotFoundException("HarmonyOS player project is missing.", request.PlayerProjectPath);
        if (!string.Equals(request.BundleFingerprint, request.BundleLock.Fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("HarmonyOS player publish Bundle fingerprint does not match the supplied lock.");
        if (request.RuntimeIdentifier is not "linux-musl-arm64" and not "linux-musl-x64")
            throw new ArgumentOutOfRangeException(nameof(request.RuntimeIdentifier), "Unsupported HarmonyOS runtime identifier.");

        string bundleRoot = Path.GetFullPath(request.BundleRoot);
        string feedDirectory = Path.Combine(bundleRoot, "feed");
        if (!Directory.Exists(feedDirectory))
            throw new DirectoryNotFoundException($"HarmonyOS Bundle feed is missing: {feedDirectory}");
        if (request.BundleLock.Packages.Count != s_requiredPackageIds.Length)
            throw new InvalidDataException("HarmonyOS Bundle lock does not contain the complete player package contract.");

        var filesByPath = new Dictionary<string, HarmonyOSSdkBundleLockedFile>(StringComparer.Ordinal);
        for (int i = 0; i < request.BundleLock.Files.Count; i++)
        {
            HarmonyOSSdkBundleLockedFile file = request.BundleLock.Files[i];
            if (!filesByPath.TryAdd(file.RelativePath, file))
                throw new InvalidDataException($"HarmonyOS Bundle lock duplicates '{file.RelativePath}'.");
        }

        var expectedPackageIds = new HashSet<string>(s_requiredPackageIds, StringComparer.Ordinal);
        var expectedFeedPaths = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < request.BundleLock.Packages.Count; i++)
        {
            HarmonyOSSdkBundleLockedPackage package = request.BundleLock.Packages[i];
            if (!expectedPackageIds.Remove(package.Id) ||
                !string.Equals(package.Version, request.BundleLock.PackageVersion, StringComparison.Ordinal) ||
                !package.RelativePath.StartsWith("feed/", StringComparison.Ordinal) ||
                !package.RelativePath.EndsWith(".nupkg", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"HarmonyOS Bundle lock package contract is invalid: {package.Id}");
            }
            if (!filesByPath.TryGetValue(package.RelativePath, out HarmonyOSSdkBundleLockedFile? file) ||
                !string.Equals(file.Sha256, package.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"HarmonyOS Bundle package is not matched by a locked file: {package.Id}");
            }
            if (!expectedFeedPaths.Add(package.RelativePath))
                throw new InvalidDataException($"HarmonyOS Bundle lock duplicates feed package path: {package.RelativePath}");

            string path = GetContainedPath(bundleRoot, package.RelativePath);
            await VerifyLockedFileAsync(path, file, cancellationToken).ConfigureAwait(false);
        }
        if (expectedPackageIds.Count != 0)
            throw new InvalidDataException("HarmonyOS Bundle lock is missing a required player package.");

        string[] actualFiles = Directory.GetFiles(feedDirectory, "*", SearchOption.AllDirectories);
        for (int i = 0; i < actualFiles.Length; i++)
        {
            string relativePath = "feed/" + Path.GetRelativePath(feedDirectory, actualFiles[i]).Replace('\\', '/');
            if (!expectedFeedPaths.Contains(relativePath))
                throw new InvalidDataException($"HarmonyOS Bundle feed contains an unlocked file: {relativePath}");
        }
        return feedDirectory;
    }

    private static async Task<HarmonyOSPlayerPayloadResult> InspectPayloadAsync(
        string outputDirectory,
        string runtimeIdentifier,
        HarmonyOSProcessResult process,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(outputDirectory))
            throw new InvalidDataException("HarmonyOS NativeAOT player publish produced no output directory.");
        string[] paths = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories);
        Array.Sort(paths, StringComparer.Ordinal);
        if (paths.Length == 0)
            throw new InvalidDataException("HarmonyOS NativeAOT player publish produced no files.");

        // NativeAOT publishes the shared library directly into the --output root (PublishDir),
        // not under runtimes/<rid>/native/. The runtime native helpers land there too.
        string expectedLibraryPath = Path.Combine(outputDirectory, PlayerLibraryFileName);
        var files = new List<HarmonyOSPlayerPayloadFile>(paths.Length);
        int playerLibraryCount = 0;
        for (int i = 0; i < paths.Length; i++)
        {
            string relativePath = Path.GetRelativePath(outputDirectory, paths[i]).Replace('\\', '/');
            if (paths[i].EndsWith(".so", StringComparison.Ordinal))
            {
                string fileName = Path.GetFileName(paths[i]);
                if (string.Equals(fileName, PlayerLibraryFileName, PathComparison))
                {
                    playerLibraryCount++;
                }
                else if (!s_allowedRuntimeNativeLibraries.Contains(fileName))
                {
                    throw new InvalidDataException($"HarmonyOS player payload contains an unexpected native library: {relativePath}");
                }
            }
            FileInfo info = new(paths[i]);
            files.Add(new HarmonyOSPlayerPayloadFile(
                relativePath,
                info.Length,
                await ComputeSha256Async(paths[i], cancellationToken).ConfigureAwait(false)));
        }
        if (playerLibraryCount != 1 || !File.Exists(expectedLibraryPath))
            throw new InvalidDataException("HarmonyOS player payload must contain exactly one ABI-matching libxengine_player.so.");

        string playerBuildId = HarmonyOSElfBuildIdReader.ReadRequired(
            await File.ReadAllBytesAsync(expectedLibraryPath, cancellationToken).ConfigureAwait(false),
            "libxengine_player.so");

        return new HarmonyOSPlayerPayloadResult
        {
            PlayerLibraryPath = expectedLibraryPath,
            PlayerBuildId = playerBuildId,
            Files = files,
            Process = process,
        };
    }

    private static async Task WriteNuGetConfigAsync(
        string path,
        string feedDirectory,
        string packagesDirectory,
        CancellationToken cancellationToken)
    {
        // The NuGet config adds the OHOS locked feed as a source alongside the machine-default
        // sources. We do NOT use <clear/> so that transitive packages (Microsoft.DotNet.ILCompiler,
        // Microsoft.NETCore.App.Runtime) resolve from the global NuGet cache / nuget.org.
        // We also do NOT override globalPackagesFolder, so the global cache is reused (this is
        // critical: the ILCompiler is ~300MB and should not be re-downloaded per build).
        // The --source argument on the publish command line ensures the OHOS feed is tried first.
        string xml = "<configuration>\n  <packageSources>\n    <add key=\"xengine-locked-harmonyos\" value=\"" +
            Xml(feedDirectory) + "\" />\n  </packageSources>\n</configuration>\n";
        await File.WriteAllTextAsync(path, xml, s_utf8NoBom, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyLockedFileAsync(
        string path,
        HarmonyOSSdkBundleLockedFile file,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length != file.Length ||
            !string.Equals(await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"HarmonyOS Bundle locked file verification failed: {file.RelativePath}");
        }
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath[0] == '/' || relativePath.Contains('\\') || Path.IsPathFullyQualified(relativePath))
            throw new InvalidDataException($"HarmonyOS Bundle path is invalid: {relativePath}");
        string[] segments = relativePath.Split('/', StringSplitOptions.None);
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0 || segments[i] == "." || segments[i] == "..")
                throw new InvalidDataException($"HarmonyOS Bundle path is invalid: {relativePath}");
        }
        string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, PathComparison))
            throw new InvalidDataException($"HarmonyOS Bundle path escapes Bundle root: {relativePath}");
        return path;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static bool HasEntries(string path)
    {
        using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return entries.MoveNext();
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
}
