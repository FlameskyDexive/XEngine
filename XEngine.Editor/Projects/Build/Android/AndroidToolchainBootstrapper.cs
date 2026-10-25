// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.Android;

internal sealed class AndroidToolchainImportOptions
{
    public string SourceRoot { get; init; } = AndroidToolchainBootstrapper.DefaultUnitySourceRoot;
    public string DestinationRoot { get; init; } = AndroidToolchainBootstrapper.GetDefaultDestinationRoot();
    public string? SeedId { get; init; }
    public string? BuildToolsVersion { get; init; }
    public int? PlatformApi { get; init; }
    public string? CommandLineToolsVersion { get; init; }
    public bool IncludeGradleLibraries { get; init; }
}

internal readonly record struct AndroidToolchainImportProgress(
    string Stage,
    string RelativePath,
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes);

internal sealed class AndroidToolchainImportedFile
{
    public string RelativePath { get; init; } = string.Empty;
    public long Length { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public long LastWriteTimeUtcTicks { get; init; }
}

internal sealed class AndroidToolchainImportedComponent
{
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string SourceRelativePath { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public long Bytes { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}

internal sealed class AndroidToolchainImportManifest
{
    public int SchemaVersion { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public string SeedId { get; init; } = string.Empty;
    public string SourceRoot { get; init; } = string.Empty;
    public DateTimeOffset ImportedAtUtc { get; init; }
    public long TotalBytes { get; init; }
    public int MaximumPathLength { get; init; }
    public List<string> IncludedComponents { get; init; } = [];
    public List<string> ExcludedUnityContent { get; init; } = [];
    public List<AndroidToolchainImportedComponent> Components { get; init; } = [];
    public List<AndroidToolchainImportedFile> Files { get; init; } = [];
}

internal sealed class AndroidToolchainImportResult
{
    public string CachePath { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public bool Reused { get; init; }
    public AndroidToolchainImportManifest Manifest { get; init; } = new();
}

internal sealed class AndroidToolchainBootstrapper
{
    internal const int ManifestSchemaVersion = 2;
    internal const string ManifestFileName = "unity-toolchain-import.json";
    internal const string DefaultUnitySourceRoot =
        @"F:\Unity\Unity2022.3.62f2_DS_20251014_Full\Editor\Data\PlaybackEngines\AndroidPlayer";

    private const int CopyBufferSize = 1024 * 1024;
    private const int WindowsMaximumPathLength = 32760;

    private static readonly string[] s_excludedUnityContent =
    [
        "AndroidPlayerBuildProgram*",
        "Unity.Android.*",
        "UnityEditor.Android.Extensions*",
        "Bee/",
        "Variations/",
        "Source/",
        "Apk/",
        "Tools/GradleTemplates/",
        "Tools/VisualStudioGradleTemplates/",
        "UnityProGuardTemplate.txt",
    ];

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
    };

    internal static string GetDefaultDestinationRoot()
    {
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
            throw new InvalidOperationException("LOCALAPPDATA is unavailable; configure an Android toolchain cache directory.");
        return Path.Combine(localApplicationData, "XEngine", "AndroidToolchains");
    }

    internal async Task<AndroidToolchainImportResult> ImportAsync(
        AndroidToolchainImportOptions options,
        IProgress<AndroidToolchainImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        string sourceRoot = NormalizeRoot(options.SourceRoot, nameof(options.SourceRoot));
        string destinationRoot = NormalizeRoot(options.DestinationRoot, nameof(options.DestinationRoot));
        ValidateRoots(sourceRoot, destinationRoot);
        EnsureOutsideGitWorktree(destinationRoot);

        ImportSelection selection = ResolveSelection(sourceRoot, options);
        List<PendingFile> pendingFiles = EnumerateSelectedFiles(sourceRoot, selection);
        long totalBytes = SumLengths(pendingFiles);
        await HashSourceFilesAsync(pendingFiles, totalBytes, progress, cancellationToken).ConfigureAwait(false);

        string fingerprint = ComputeFingerprint(pendingFiles);
        string seedId = SanitizePathSegment(options.SeedId ?? DeriveSeedId(sourceRoot));
        string cachePath = Path.Combine(destinationRoot, seedId + "-" + fingerprint);
        string manifestPath = Path.Combine(cachePath, ManifestFileName);
        int maximumPathLength = ValidateDestinationPaths(cachePath, pendingFiles);
        AndroidToolchainImportManifest? existing = await TryReadVerifiedManifestAsync(
            manifestPath,
            sourceRoot,
            seedId,
            fingerprint,
            maximumPathLength,
            selection,
            cancellationToken).ConfigureAwait(false);
        if (existing != null)
        {
            return new AndroidToolchainImportResult
            {
                CachePath = cachePath,
                ManifestPath = manifestPath,
                Reused = true,
                Manifest = existing,
            };
        }
        if (Directory.Exists(cachePath))
        {
            throw new IOException(
                $"Android toolchain cache already exists but failed manifest or file integrity verification: {cachePath}");
        }

        ValidateDestinationCapacity(destinationRoot, totalBytes);
        Directory.CreateDirectory(destinationRoot);
        string stagingPath = Path.Combine(destinationRoot, "." + seedId + "-" + Guid.NewGuid().ToString("N") + ".staging");
        Directory.CreateDirectory(stagingPath);
        try
        {
            await CopyAndVerifyAsync(
                sourceRoot,
                stagingPath,
                pendingFiles,
                totalBytes,
                progress,
                cancellationToken).ConfigureAwait(false);

            AndroidToolchainImportManifest manifest = CreateManifest(
                sourceRoot,
                seedId,
                fingerprint,
                selection,
                pendingFiles,
                totalBytes,
                maximumPathLength);
            string stagingManifestPath = Path.Combine(stagingPath, ManifestFileName);
            string json = JsonSerializer.Serialize(manifest, s_jsonOptions);
            await File.WriteAllTextAsync(stagingManifestPath, json, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(cachePath))
            {
                AndroidToolchainImportManifest? raced = await TryReadVerifiedManifestAsync(
                    manifestPath,
                    sourceRoot,
                    seedId,
                    fingerprint,
                    maximumPathLength,
                    selection,
                    cancellationToken).ConfigureAwait(false);
                if (raced == null)
                {
                    throw new IOException(
                        $"Android toolchain cache already exists but failed manifest or file integrity verification: {cachePath}");
                }
                DeleteStagingDirectory(stagingPath, destinationRoot);
                return new AndroidToolchainImportResult
                {
                    CachePath = cachePath,
                    ManifestPath = manifestPath,
                    Reused = true,
                    Manifest = raced,
                };
            }

            await PublishStagingDirectoryAsync(stagingPath, cachePath, cancellationToken).ConfigureAwait(false);
            return new AndroidToolchainImportResult
            {
                CachePath = cachePath,
                ManifestPath = manifestPath,
                Reused = false,
                Manifest = manifest,
            };
        }
        catch
        {
            DeleteStagingDirectory(stagingPath, destinationRoot);
            throw;
        }
    }

    private static ImportSelection ResolveSelection(string sourceRoot, AndroidToolchainImportOptions options)
    {
        RequireDirectory(sourceRoot, "OpenJDK");
        RequireDirectory(sourceRoot, "NDK");
        RequireDirectory(sourceRoot, "SDK/platform-tools");

        string buildToolsVersion = ResolveVersionDirectory(
            Path.Combine(sourceRoot, "SDK", "build-tools"),
            options.BuildToolsVersion,
            static value => TryParseVersion(value, out Version? version) ? version : null,
            "Android build-tools");
        string platformVersion = ResolveVersionDirectory(
            Path.Combine(sourceRoot, "SDK", "platforms"),
            options.PlatformApi.HasValue ? "android-" + options.PlatformApi.Value.ToString(CultureInfo.InvariantCulture) : null,
            static value => value.StartsWith("android-", StringComparison.Ordinal) &&
                            int.TryParse(value.AsSpan("android-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int api)
                ? new Version(api, 0)
                : null,
            "Android SDK platform");
        string commandLineToolsVersion = ResolveVersionDirectory(
            Path.Combine(sourceRoot, "SDK", "cmdline-tools"),
            options.CommandLineToolsVersion,
            static value => TryParseVersion(value, out Version? version) ? version : null,
            "Android command-line tools");
        string bundletoolRelativePath = ResolveBundletool(sourceRoot);

        var components = new List<SelectedComponent>
        {
            new("openjdk", ReadQuotedProperty(Path.Combine(sourceRoot, "OpenJDK", "release"), "JAVA_VERSION"), "OpenJDK"),
            new("ndk", ReadProperty(Path.Combine(sourceRoot, "NDK", "source.properties"), "Pkg.Revision"), "NDK"),
            new("sdk-build-tools", buildToolsVersion, "SDK/build-tools/" + buildToolsVersion),
            new("sdk-platform", platformVersion, "SDK/platforms/" + platformVersion),
            new("sdk-platform-tools", ReadProperty(Path.Combine(sourceRoot, "SDK", "platform-tools", "source.properties"), "Pkg.Revision"), "SDK/platform-tools"),
            new("sdk-command-line-tools", commandLineToolsVersion, "SDK/cmdline-tools/" + commandLineToolsVersion),
            new("bundletool", ParseBundletoolVersion(bundletoolRelativePath), bundletoolRelativePath, isFile: true),
        };

        string licensesPath = Path.Combine(sourceRoot, "SDK", "licenses");
        if (Directory.Exists(licensesPath))
            components.Add(new SelectedComponent("sdk-licenses", "local-accepted-state", "SDK/licenses"));

        if (options.IncludeGradleLibraries)
        {
            RequireDirectory(sourceRoot, "Tools/gradle/lib");
            RequireDirectory(sourceRoot, "Tools/gradle/plugins");
            components.Add(new SelectedComponent("gradle-libraries", DetectGradleVersion(sourceRoot), "Tools/gradle/lib"));
            components.Add(new SelectedComponent("gradle-plugins", DetectGradleVersion(sourceRoot), "Tools/gradle/plugins"));
        }

        return new ImportSelection(components);
    }

    private static List<PendingFile> EnumerateSelectedFiles(string sourceRoot, ImportSelection selection)
    {
        var files = new List<PendingFile>();
        var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < selection.Components.Count; i++)
        {
            SelectedComponent component = selection.Components[i];
            string componentPath = GetContainedPath(sourceRoot, component.SourceRelativePath);
            RejectReparsePoint(componentPath);
            int firstFileIndex = files.Count;
            long componentBytes = 0;
            if (component.IsFile)
            {
                AddFile(componentPath, component.SourceRelativePath);
            }
            else
            {
                EnumerateDirectory(componentPath, component.SourceRelativePath, AddFile);
            }

            component.FileCount = files.Count - firstFileIndex;
            component.Bytes = componentBytes;
            if (component.FileCount == 0)
                throw new InvalidDataException($"Android toolchain component is empty: {component.SourceRelativePath}");

            void AddFile(string absolutePath, string relativePath)
            {
                string normalizedRelativePath = NormalizeRelativePath(relativePath);
                if (!relativePaths.Add(normalizedRelativePath))
                    throw new InvalidDataException($"Android toolchain import contains a duplicate path: {normalizedRelativePath}");
                var info = new FileInfo(absolutePath);
                var file = new PendingFile(absolutePath, normalizedRelativePath, info.Length, info.LastWriteTimeUtc.Ticks);
                files.Add(file);
                component.Files.Add(file);
                componentBytes += info.Length;
            }
        }

        files.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        return files;
    }

    private static void EnumerateDirectory(
        string absoluteRoot,
        string relativeRoot,
        Action<string, string> addFile)
    {
        var pending = new Stack<(string Absolute, string Relative)>();
        pending.Push((absoluteRoot, NormalizeRelativePath(relativeRoot)));
        while (pending.Count > 0)
        {
            (string absolute, string relative) = pending.Pop();
            string[] entries = Directory.GetFileSystemEntries(absolute);
            Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
            for (int i = entries.Length - 1; i >= 0; i--)
            {
                string entry = entries[i];
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Android toolchain import refuses reparse points: {entry}");

                string childRelative = string.IsNullOrEmpty(relative)
                    ? Path.GetFileName(entry)
                    : relative + "/" + Path.GetFileName(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push((entry, childRelative));
                else
                    addFile(entry, childRelative);
            }
        }
    }

    private static async Task HashSourceFilesAsync(
        List<PendingFile> files,
        long totalBytes,
        IProgress<AndroidToolchainImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        long completedBytes = 0;
        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PendingFile file = files[i];
            await using var stream = new FileStream(
                file.AbsolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            file.Sha256 = Convert.ToHexString(hash);
            completedBytes += file.Length;
            progress?.Report(new AndroidToolchainImportProgress(
                "Hashing",
                file.RelativePath,
                i + 1,
                files.Count,
                completedBytes,
                totalBytes));
        }
    }

    private static async Task CopyAndVerifyAsync(
        string sourceRoot,
        string stagingPath,
        List<PendingFile> files,
        long totalBytes,
        IProgress<AndroidToolchainImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long completedBytes = 0;
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PendingFile file = files[i];
                string sourcePath = GetContainedPath(sourceRoot, file.RelativePath);
                string destinationPath = GetContainedPath(stagingPath, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var source = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var destination = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    while (true)
                    {
                        int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                            break;
                        hash.AppendData(buffer, 0, read);
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                string copiedHash = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(copiedHash, file.Sha256, StringComparison.Ordinal))
                    throw new IOException($"Android toolchain source changed while copying: {file.RelativePath}");
                File.SetLastWriteTimeUtc(destinationPath, new DateTime(file.LastWriteTimeUtcTicks, DateTimeKind.Utc));
                completedBytes += file.Length;
                progress?.Report(new AndroidToolchainImportProgress(
                    "Copying",
                    file.RelativePath,
                    i + 1,
                    files.Count,
                    completedBytes,
                    totalBytes));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static AndroidToolchainImportManifest CreateManifest(
        string sourceRoot,
        string seedId,
        string fingerprint,
        ImportSelection selection,
        List<PendingFile> files,
        long totalBytes,
        int maximumPathLength)
    {
        var components = new List<AndroidToolchainImportedComponent>(selection.Components.Count);
        for (int i = 0; i < selection.Components.Count; i++)
        {
            SelectedComponent component = selection.Components[i];
            components.Add(new AndroidToolchainImportedComponent
            {
                Id = component.Id,
                Version = component.Version,
                SourceRelativePath = component.SourceRelativePath,
                FileCount = component.FileCount,
                Bytes = component.Bytes,
                Sha256 = ComputeFingerprint(component.Files),
            });
        }

        var importedFiles = new List<AndroidToolchainImportedFile>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            PendingFile file = files[i];
            importedFiles.Add(new AndroidToolchainImportedFile
            {
                RelativePath = file.RelativePath,
                Length = file.Length,
                Sha256 = file.Sha256,
                LastWriteTimeUtcTicks = file.LastWriteTimeUtcTicks,
            });
        }

        return new AndroidToolchainImportManifest
        {
            SchemaVersion = ManifestSchemaVersion,
            Fingerprint = fingerprint,
            SeedId = seedId,
            SourceRoot = sourceRoot,
            ImportedAtUtc = DateTimeOffset.UtcNow,
            TotalBytes = totalBytes,
            MaximumPathLength = maximumPathLength,
            IncludedComponents = GetIncludedComponents(selection),
            ExcludedUnityContent = [.. s_excludedUnityContent],
            Components = components,
            Files = importedFiles,
        };
    }

    private static async Task<AndroidToolchainImportManifest?> TryReadVerifiedManifestAsync(
        string manifestPath,
        string sourceRoot,
        string seedId,
        string fingerprint,
        int maximumPathLength,
        ImportSelection selection,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
            return null;
        try
        {
            string json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            AndroidToolchainImportManifest? manifest =
                JsonSerializer.Deserialize<AndroidToolchainImportManifest>(json, s_jsonOptions);
            if (manifest == null || !MatchesExpectedManifestMetadata(
                    manifest,
                    sourceRoot,
                    seedId,
                    fingerprint,
                    maximumPathLength,
                    selection))
            {
                return null;
            }

            string cachePath = Path.GetDirectoryName(manifestPath)!;
            long verifiedBytes = 0;
            var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < manifest.Files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AndroidToolchainImportedFile expected = manifest.Files[i];
                if (expected == null ||
                    string.IsNullOrWhiteSpace(expected.RelativePath) ||
                    !expectedPaths.Add(expected.RelativePath) ||
                    string.Equals(expected.RelativePath, ManifestFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                string filePath = GetContainedPath(cachePath, expected.RelativePath);
                var info = new FileInfo(filePath);
                if (!info.Exists || info.Length != expected.Length)
                    return null;

                await using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                string actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(actualHash, expected.Sha256, StringComparison.Ordinal))
                    return null;
                verifiedBytes = checked(verifiedBytes + expected.Length);
            }
            if (manifest.Files.Count == 0 || verifiedBytes != manifest.TotalBytes ||
                ContainsUnexpectedCacheFiles(cachePath, expectedPaths))
            {
                return null;
            }
            return manifest;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool MatchesExpectedManifestMetadata(
        AndroidToolchainImportManifest manifest,
        string sourceRoot,
        string seedId,
        string fingerprint,
        int maximumPathLength,
        ImportSelection selection)
    {
        if (manifest.Components == null ||
            manifest.IncludedComponents == null ||
            manifest.ExcludedUnityContent == null ||
            manifest.Files == null ||
            manifest.SchemaVersion != ManifestSchemaVersion ||
            !string.Equals(manifest.Fingerprint, fingerprint, StringComparison.Ordinal) ||
            !string.Equals(manifest.SourceRoot, sourceRoot, PathComparison) ||
            !string.Equals(manifest.SeedId, seedId, StringComparison.Ordinal) ||
            manifest.MaximumPathLength != maximumPathLength ||
            manifest.Components.Count != selection.Components.Count ||
            manifest.IncludedComponents.Count != selection.Components.Count ||
            manifest.ExcludedUnityContent.Count != s_excludedUnityContent.Length ||
            !string.Equals(ComputeImportedFileFingerprint(manifest.Files), fingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = 0; i < selection.Components.Count; i++)
        {
            SelectedComponent expected = selection.Components[i];
            AndroidToolchainImportedComponent actual = manifest.Components[i];
            if (actual == null ||
                !string.Equals(actual.Id, expected.Id, StringComparison.Ordinal) ||
                !string.Equals(actual.Version, expected.Version, StringComparison.Ordinal) ||
                !string.Equals(actual.SourceRelativePath, expected.SourceRelativePath, StringComparison.Ordinal) ||
                actual.FileCount != expected.FileCount ||
                actual.Bytes != expected.Bytes ||
                !string.Equals(actual.Sha256, ComputeFingerprint(expected.Files), StringComparison.Ordinal) ||
                !string.Equals(manifest.IncludedComponents[i], expected.SourceRelativePath, StringComparison.Ordinal))
            {
                return false;
            }
        }

        for (int i = 0; i < s_excludedUnityContent.Length; i++)
        {
            if (!string.Equals(manifest.ExcludedUnityContent[i], s_excludedUnityContent[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static string ComputeImportedFileFingerprint(List<AndroidToolchainImportedFile> files)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ManifestSchemaVersion);
            writer.Write(files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                AndroidToolchainImportedFile file = files[i];
                if (file == null || string.IsNullOrWhiteSpace(file.RelativePath) ||
                    string.IsNullOrWhiteSpace(file.Sha256))
                {
                    return string.Empty;
                }
                writer.Write(file.RelativePath);
                writer.Write(file.Length);
                try
                {
                    writer.Write(Convert.FromHexString(file.Sha256));
                }
                catch (FormatException)
                {
                    return string.Empty;
                }
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static bool ContainsUnexpectedCacheFiles(string cachePath, HashSet<string> expectedPaths)
    {
        bool unexpected = false;
        EnumerateDirectory(cachePath, string.Empty, (_, relativePath) =>
        {
            string normalized = NormalizeRelativePath(relativePath);
            if (!string.Equals(normalized, ManifestFileName, StringComparison.OrdinalIgnoreCase) &&
                !expectedPaths.Contains(normalized))
            {
                unexpected = true;
            }
        });
        return unexpected;
    }

    private static List<string> GetIncludedComponents(ImportSelection selection)
    {
        var result = new List<string>(selection.Components.Count);
        for (int i = 0; i < selection.Components.Count; i++)
            result.Add(selection.Components[i].SourceRelativePath);
        return result;
    }

    private static string ComputeFingerprint(List<PendingFile> files)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ManifestSchemaVersion);
            writer.Write(files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                PendingFile file = files[i];
                writer.Write(file.RelativePath);
                writer.Write(file.Length);
                writer.Write(Convert.FromHexString(file.Sha256));
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static long SumLengths(List<PendingFile> files)
    {
        long result = 0;
        for (int i = 0; i < files.Count; i++)
            result = checked(result + files[i].Length);
        return result;
    }

    private static void ValidateDestinationCapacity(string destinationRoot, long totalBytes)
    {
        string? root = Path.GetPathRoot(destinationRoot);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException($"Cannot determine the volume for Android toolchain cache: {destinationRoot}");

        var drive = new DriveInfo(root);
        if (!drive.IsReady)
            throw new IOException($"Android toolchain cache volume is not ready: {root}");
        if (drive.AvailableFreeSpace < totalBytes)
        {
            throw new IOException(
                $"Android toolchain import requires {totalBytes} bytes, but only " +
                $"{drive.AvailableFreeSpace} bytes are available on {drive.Name}.");
        }
    }

    private static int ValidateDestinationPaths(string cachePath, List<PendingFile> files)
    {
        int maximum = Path.Combine(cachePath, ManifestFileName).Length;
        for (int i = 0; i < files.Count; i++)
        {
            string path = Path.Combine(
                cachePath,
                files[i].RelativePath.Replace('/', Path.DirectorySeparatorChar));
            maximum = Math.Max(maximum, path.Length);
        }

        if (OperatingSystem.IsWindows() && maximum > WindowsMaximumPathLength)
        {
            throw new PathTooLongException(
                $"Android toolchain cache would contain a {maximum}-character path; " +
                $"choose a shorter cache root (maximum {WindowsMaximumPathLength}).");
        }
        return maximum;
    }

    private static async Task PublishStagingDirectoryAsync(
        string stagingPath,
        string cachePath,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 12;
        Exception? lastException = null;
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(stagingPath, cachePath);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException &&
                !Directory.Exists(cachePath))
            {
                lastException = exception;
                if (attempt + 1 == maximumAttempts)
                    break;
                int delayMilliseconds = Math.Min(2000, 100 * (attempt + 1));
                await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException(
            $"Failed to atomically publish Android toolchain cache after {maximumAttempts} attempts: {cachePath}",
            lastException);
    }

    private static string ResolveVersionDirectory(
        string root,
        string? requested,
        Func<string, Version?> parseVersion,
        string description)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"{description} directory is missing: {root}");
        if (!string.IsNullOrWhiteSpace(requested))
        {
            string requestedPath = GetContainedPath(root, requested);
            if (!Directory.Exists(requestedPath))
                throw new DirectoryNotFoundException($"Requested {description} '{requested}' is missing from {root}.");
            return requested;
        }

        string? bestName = null;
        Version? bestVersion = null;
        string[] directories = Directory.GetDirectories(root);
        for (int i = 0; i < directories.Length; i++)
        {
            string name = Path.GetFileName(directories[i]);
            Version? version = parseVersion(name);
            if (version == null)
                continue;
            if (bestVersion == null || version > bestVersion)
            {
                bestVersion = version;
                bestName = name;
            }
        }

        return bestName ?? throw new InvalidDataException($"No versioned {description} directory was found in {root}.");
    }

    private static string ResolveBundletool(string sourceRoot)
    {
        string toolsRoot = Path.Combine(sourceRoot, "Tools");
        if (!Directory.Exists(toolsRoot))
            throw new DirectoryNotFoundException($"Unity Android tools directory is missing: {toolsRoot}");
        string[] candidates = Directory.GetFiles(toolsRoot, "bundletool-all-*.jar", SearchOption.TopDirectoryOnly);
        if (candidates.Length == 0)
            throw new FileNotFoundException($"bundletool-all-*.jar is missing from {toolsRoot}.");

        string? bestPath = null;
        Version? bestVersion = null;
        for (int i = 0; i < candidates.Length; i++)
        {
            string fileName = Path.GetFileNameWithoutExtension(candidates[i]);
            string value = fileName["bundletool-all-".Length..];
            if (!TryParseVersion(value, out Version? version))
                continue;
            if (bestVersion == null || version > bestVersion)
            {
                bestVersion = version;
                bestPath = candidates[i];
            }
        }
        if (bestPath == null)
            throw new InvalidDataException($"No versioned bundletool jar was found in {toolsRoot}.");
        return NormalizeRelativePath(Path.GetRelativePath(sourceRoot, bestPath));
    }

    private static string DetectGradleVersion(string sourceRoot)
    {
        string libRoot = Path.Combine(sourceRoot, "Tools", "gradle", "lib");
        string[] candidates = Directory.GetFiles(libRoot, "gradle-core-*.jar", SearchOption.TopDirectoryOnly);
        Version? bestVersion = null;
        string? best = null;
        for (int i = 0; i < candidates.Length; i++)
        {
            string fileName = Path.GetFileNameWithoutExtension(candidates[i]);
            string value = fileName["gradle-core-".Length..];
            if (!TryParseVersion(value, out Version? version))
                continue;
            if (bestVersion == null || version > bestVersion)
            {
                bestVersion = version;
                best = value;
            }
        }
        return best ?? "unknown";
    }

    private static string ParseBundletoolVersion(string relativePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(relativePath);
        return fileName.StartsWith("bundletool-all-", StringComparison.Ordinal)
            ? fileName["bundletool-all-".Length..]
            : "unknown";
    }

    private static bool TryParseVersion(string value, out Version? version)
    {
        bool parsed = Version.TryParse(value, out Version? result);
        version = result;
        return parsed;
    }

    private static string ReadProperty(string path, string propertyName)
    {
        if (!File.Exists(path))
            return "unknown";
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            int equals = lines[i].IndexOf('=');
            if (equals < 0)
                continue;
            if (string.Equals(lines[i][..equals].Trim(), propertyName, StringComparison.Ordinal))
                return lines[i][(equals + 1)..].Trim();
        }
        return "unknown";
    }

    private static string ReadQuotedProperty(string path, string propertyName) =>
        ReadProperty(path, propertyName).Trim('"');

    private static void RequireDirectory(string sourceRoot, string relativePath)
    {
        string path = GetContainedPath(sourceRoot, relativePath);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Required Unity Android toolchain directory is missing: {path}");
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Android toolchain import refuses reparse points: {path}");
    }

    private static string NormalizeRoot(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void ValidateRoots(string sourceRoot, string destinationRoot)
    {
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Unity AndroidPlayer source directory is missing: {sourceRoot}");
        if (string.Equals(sourceRoot, destinationRoot, PathComparison) || IsWithin(destinationRoot, sourceRoot))
        {
            throw new InvalidOperationException(
                "Android toolchain destination must not equal or be inside the Unity AndroidPlayer source directory.");
        }
    }

    private static void EnsureOutsideGitWorktree(string destinationRoot)
    {
        var directory = new DirectoryInfo(destinationRoot);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                throw new InvalidOperationException(
                    $"Android toolchain cache must not be created inside a Git worktree: {destinationRoot}");
            }
            directory = directory.Parent;
        }
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        string result = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(result, root))
            throw new InvalidDataException($"Android toolchain path escapes its root: {relativePath}");
        return result;
    }

    private static bool IsWithin(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string DeriveSeedId(string sourceRoot)
    {
        DirectoryInfo? current = new DirectoryInfo(sourceRoot);
        for (int i = 0; i < 6 && current != null; i++, current = current.Parent)
        {
            if (current.Name.StartsWith("Unity", StringComparison.OrdinalIgnoreCase))
                return "unity-" + current.Name["Unity".Length..];
        }
        return "unity-androidplayer";
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Android toolchain seed id cannot be empty.", nameof(value));
        char[] invalid = Path.GetInvalidFileNameChars();
        var result = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
            result.Append(Array.IndexOf(invalid, value[i]) >= 0 ? '_' : value[i]);
        return result.ToString();
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void DeleteStagingDirectory(string stagingPath, string destinationRoot)
    {
        if (!Directory.Exists(stagingPath))
            return;
        string normalizedStaging = NormalizeRoot(stagingPath, nameof(stagingPath));
        if (!IsWithin(normalizedStaging, destinationRoot) ||
            !Path.GetFileName(normalizedStaging).EndsWith(".staging", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to delete unsafe Android toolchain staging path: {stagingPath}");
        }
        Directory.Delete(normalizedStaging, recursive: true);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class PendingFile(
        string absolutePath,
        string relativePath,
        long length,
        long lastWriteTimeUtcTicks)
    {
        internal string AbsolutePath { get; } = absolutePath;
        internal string RelativePath { get; } = relativePath;
        internal long Length { get; } = length;
        internal long LastWriteTimeUtcTicks { get; } = lastWriteTimeUtcTicks;
        internal string Sha256 { get; set; } = string.Empty;
    }

    private sealed class SelectedComponent(
        string id,
        string version,
        string sourceRelativePath,
        bool isFile = false)
    {
        internal string Id { get; } = id;
        internal string Version { get; } = version;
        internal string SourceRelativePath { get; } = NormalizeRelativePath(sourceRelativePath);
        internal bool IsFile { get; } = isFile;
        internal int FileCount { get; set; }
        internal long Bytes { get; set; }
        internal List<PendingFile> Files { get; } = [];
    }

    private sealed class ImportSelection(List<SelectedComponent> components)
    {
        internal List<SelectedComponent> Components { get; } = components;
    }
}
