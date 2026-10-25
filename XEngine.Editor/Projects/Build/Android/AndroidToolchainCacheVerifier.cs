// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.Android;

internal static class AndroidToolchainCacheVerifier
{
    private const int HashBufferSize = 1024 * 1024;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    internal static async Task<AndroidToolchainImportManifest> VerifyAsync(
        string cachePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        string normalizedCachePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cachePath));
        string manifestPath = Path.Combine(normalizedCachePath, AndroidToolchainBootstrapper.ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Android toolchain import manifest is missing.", manifestPath);

        AndroidToolchainImportManifest manifest;
        try
        {
            string json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<AndroidToolchainImportManifest>(json, s_jsonOptions)
                ?? throw new InvalidDataException("Android toolchain import manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Android toolchain import manifest is invalid JSON.", exception);
        }

        ValidateManifestMetadata(normalizedCachePath, manifest);

        long verifiedBytes = 0;
        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < manifest.Files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AndroidToolchainImportedFile file = manifest.Files[i]
                ?? throw new InvalidDataException("Android toolchain import manifest contains a null file entry.");
            string relativePath = NormalizeRelativePath(file.RelativePath);
            if (string.IsNullOrWhiteSpace(relativePath) ||
                string.Equals(relativePath, AndroidToolchainBootstrapper.ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                !expectedPaths.Add(relativePath) ||
                file.Length < 0 ||
                !IsSha256(file.Sha256))
            {
                throw new InvalidDataException($"Android toolchain import manifest contains an invalid file entry: {file.RelativePath}");
            }

            string absolutePath = GetContainedPath(normalizedCachePath, relativePath);
            var info = new FileInfo(absolutePath);
            if (!info.Exists || info.Length != file.Length)
                throw new InvalidDataException($"Android toolchain cache file is missing or has changed size: {relativePath}");
            RejectReparsePoint(absolutePath);

            await using var stream = new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                HashBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            string actualHash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actualHash, file.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Android toolchain cache file failed SHA-256 verification: {relativePath}");
            verifiedBytes = checked(verifiedBytes + file.Length);
        }

        if (verifiedBytes != manifest.TotalBytes)
            throw new InvalidDataException("Android toolchain cache byte count does not match its import manifest.");
        RejectUnexpectedFiles(normalizedCachePath, expectedPaths);
        return manifest;
    }

    internal static void VerifyDeclaredFileSet(string cachePath, AndroidToolchainImportManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        ArgumentNullException.ThrowIfNull(manifest);
        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < manifest.Files.Count; i++)
            expectedPaths.Add(NormalizeRelativePath(manifest.Files[i].RelativePath));
        RejectUnexpectedFiles(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cachePath)), expectedPaths);
    }

    private static void ValidateManifestMetadata(string cachePath, AndroidToolchainImportManifest manifest)
    {
        if (manifest.SchemaVersion != AndroidToolchainBootstrapper.ManifestSchemaVersion ||
            manifest.Files == null || manifest.Files.Count == 0 ||
            manifest.Components == null || manifest.Components.Count == 0 ||
            manifest.IncludedComponents == null ||
            manifest.ExcludedUnityContent == null ||
            string.IsNullOrWhiteSpace(manifest.SeedId) ||
            !IsSha256(manifest.Fingerprint) ||
            manifest.TotalBytes < 0 ||
            !string.Equals(ComputeFileFingerprint(manifest.Files), manifest.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Android toolchain import manifest metadata failed integrity verification.");
        }

        string expectedDirectoryName = manifest.SeedId + "-" + manifest.Fingerprint;
        if (!string.Equals(Path.GetFileName(cachePath), expectedDirectoryName, PathComparison))
        {
            throw new InvalidDataException(
                "Android toolchain cache directory name does not match the manifest seed and fingerprint.");
        }
        if (manifest.Components.Count != manifest.IncludedComponents.Count)
            throw new InvalidDataException("Android toolchain component metadata is inconsistent.");

        var assignedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var componentIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < manifest.Components.Count; i++)
        {
            AndroidToolchainImportedComponent component = manifest.Components[i]
                ?? throw new InvalidDataException("Android toolchain manifest contains a null component.");
            string componentPath = NormalizeRelativePath(component.SourceRelativePath);
            if (string.IsNullOrWhiteSpace(component.Id) ||
                !componentIds.Add(component.Id) ||
                string.IsNullOrWhiteSpace(component.Version) ||
                string.IsNullOrWhiteSpace(componentPath) ||
                component.FileCount <= 0 || component.Bytes < 0 ||
                !IsSha256(component.Sha256) ||
                !string.Equals(componentPath, NormalizeRelativePath(manifest.IncludedComponents[i]), StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Android toolchain component metadata is invalid at index {i}.");
            }

            var componentFiles = new List<AndroidToolchainImportedFile>();
            long componentBytes = 0;
            for (int fileIndex = 0; fileIndex < manifest.Files.Count; fileIndex++)
            {
                AndroidToolchainImportedFile file = manifest.Files[fileIndex];
                string filePath = NormalizeRelativePath(file.RelativePath);
                if (!IsWithinComponent(filePath, componentPath))
                    continue;
                if (!assignedFiles.Add(filePath))
                    throw new InvalidDataException($"Android toolchain file belongs to multiple components: {filePath}");
                componentFiles.Add(file);
                componentBytes = checked(componentBytes + file.Length);
            }

            List<AndroidToolchainImportedFile> orderedComponentFiles = RestoreImportEnumerationOrder(componentFiles, componentPath);
            if (componentFiles.Count != component.FileCount || componentBytes != component.Bytes ||
                !string.Equals(ComputeFileFingerprint(orderedComponentFiles), component.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Android toolchain component failed integrity verification: {component.Id}");
            }
        }

        if (assignedFiles.Count != manifest.Files.Count)
            throw new InvalidDataException("Android toolchain manifest contains files not owned by a declared component.");
    }

    private static string ComputeFileFingerprint(List<AndroidToolchainImportedFile> files)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(AndroidToolchainBootstrapper.ManifestSchemaVersion);
            writer.Write(files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                AndroidToolchainImportedFile file = files[i];
                if (file == null || string.IsNullOrWhiteSpace(file.RelativePath) || !IsSha256(file.Sha256))
                    return string.Empty;
                writer.Write(NormalizeRelativePath(file.RelativePath));
                writer.Write(file.Length);
                writer.Write(Convert.FromHexString(file.Sha256));
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static List<AndroidToolchainImportedFile> RestoreImportEnumerationOrder(
        List<AndroidToolchainImportedFile> files,
        string componentPath)
    {
        var root = new ManifestDirectoryNode();
        for (int i = 0; i < files.Count; i++)
        {
            AndroidToolchainImportedFile file = files[i];
            string filePath = NormalizeRelativePath(file.RelativePath);
            string relative = string.Equals(filePath, componentPath, StringComparison.Ordinal)
                ? Path.GetFileName(filePath)
                : filePath[(componentPath.Length + 1)..];
            string[] segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            ManifestDirectoryNode current = root;
            for (int segmentIndex = 0; segmentIndex < segments.Length - 1; segmentIndex++)
            {
                if (!current.Directories.TryGetValue(segments[segmentIndex], out ManifestDirectoryNode? child))
                {
                    child = new ManifestDirectoryNode();
                    current.Directories.Add(segments[segmentIndex], child);
                }
                current = child;
            }
            current.Files.Add(file);
        }

        var result = new List<AndroidToolchainImportedFile>(files.Count);
        AppendDirectory(root, result);
        return result;
    }

    private static void AppendDirectory(ManifestDirectoryNode directory, List<AndroidToolchainImportedFile> result)
    {
        directory.Files.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(right.RelativePath, left.RelativePath));
        result.AddRange(directory.Files);

        string[] names = new string[directory.Directories.Count];
        directory.Directories.Keys.CopyTo(names, 0);
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Length; i++)
            AppendDirectory(directory.Directories[names[i]], result);
    }

    private static void RejectUnexpectedFiles(string cachePath, HashSet<string> expectedPaths)
    {
        var pending = new Stack<string>();
        pending.Push(cachePath);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            RejectReparsePoint(directory);
            string[] entries = Directory.GetFileSystemEntries(directory);
            for (int i = 0; i < entries.Length; i++)
            {
                string entry = entries[i];
                RejectReparsePoint(entry);
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }

                string relativePath = NormalizeRelativePath(Path.GetRelativePath(cachePath, entry));
                if (!string.Equals(relativePath, AndroidToolchainBootstrapper.ManifestFileName, StringComparison.OrdinalIgnoreCase) &&
                    !expectedPaths.Contains(relativePath))
                {
                    throw new InvalidDataException($"Android toolchain cache contains an undeclared file: {relativePath}");
                }
            }
        }
    }

    private static bool IsWithinComponent(string filePath, string componentPath) =>
        string.Equals(filePath, componentPath, StringComparison.Ordinal) ||
        filePath.StartsWith(componentPath + "/", StringComparison.Ordinal);

    private static bool IsSha256(string value)
    {
        if (value == null || value.Length != 64)
            return false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) ||
            string.Equals(relative, "..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Android toolchain manifest path escapes its cache: {relativePath}");
        }
        return path;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Android toolchain cache refuses reparse points: {path}");
    }

    private static string NormalizeRelativePath(string path) =>
        path?.Replace('\\', '/').TrimStart('/') ?? string.Empty;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class ManifestDirectoryNode
    {
        internal List<AndroidToolchainImportedFile> Files { get; } = [];
        internal Dictionary<string, ManifestDirectoryNode> Directories { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
