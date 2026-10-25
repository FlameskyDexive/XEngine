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

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>Reads, verifies, and atomically writes the project-relative OpenHarmony SDK Bundle lock.</summary>
internal static class HarmonyOSSdkBundleLockFile
{
    internal const string DefaultFileName = "openharmony-sdk-lock.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
    };

    internal static async Task WriteAsync(
        string path,
        HarmonyOSSdkBundleLock value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);

        HarmonyOSSdkBundleLock normalized = NormalizeAndValidate(value, expectedFingerprint: null);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            string json = JsonSerializer.Serialize(normalized, s_jsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            HarmonyOSSdkBundleLock persisted = await ReadAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(persisted.Fingerprint, normalized.Fingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("OpenHarmony SDK Bundle lock temporary write failed fingerprint verification.");

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    internal static async Task<HarmonyOSSdkBundleLock> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        HarmonyOSSdkBundleLock? value = JsonSerializer.Deserialize<HarmonyOSSdkBundleLock>(json, s_jsonOptions);
        if (value == null)
            throw new InvalidDataException("OpenHarmony SDK Bundle lock is empty.");

        return NormalizeAndValidate(value, value.Fingerprint);
    }

    internal static string ComputeFingerprint(HarmonyOSSdkBundleLock value)
    {
        ArgumentNullException.ThrowIfNull(value);
        HarmonyOSSdkBundleLock normalized = NormalizeAndValidate(value, expectedFingerprint: null, includeFingerprint: false);
        return ComputeFingerprintCore(normalized);
    }

    private static HarmonyOSSdkBundleLock NormalizeAndValidate(
        HarmonyOSSdkBundleLock value,
        string? expectedFingerprint,
        bool includeFingerprint = true)
    {
        if (value.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported OpenHarmony SDK Bundle lock schema version '{value.SchemaVersion}'.");

        string releaseId = RequireName(value.ReleaseId, nameof(value.ReleaseId));
        string packageVersion = RequireName(value.PackageVersion, nameof(value.PackageVersion));
        string bundleRelativePath = NormalizeRelativePath(value.BundleRelativePath, nameof(value.BundleRelativePath));
        string archiveSha256 = NormalizeSha256(value.ArchiveSha256, nameof(value.ArchiveSha256));
        string manifestSha256 = NormalizeSha256(value.ManifestSha256, nameof(value.ManifestSha256));
        string sourceKind = RequireName(value.SourceKind, nameof(value.SourceKind));

        if (value.Files == null || value.Files.Count == 0)
            throw new InvalidDataException("OpenHarmony SDK Bundle lock must contain at least one file.");
        if (value.Packages == null || value.Packages.Count == 0)
            throw new InvalidDataException("OpenHarmony SDK Bundle lock must contain at least one package.");

        var files = new List<HarmonyOSSdkBundleLockedFile>(value.Files.Count);
        for (int i = 0; i < value.Files.Count; i++)
        {
            HarmonyOSSdkBundleLockedFile file = value.Files[i] ?? throw new InvalidDataException("OpenHarmony SDK Bundle lock contains a null file entry.");
            if (file.Length < 0)
                throw new InvalidDataException($"OpenHarmony SDK Bundle lock file '{file.RelativePath}' has a negative length.");
            files.Add(new HarmonyOSSdkBundleLockedFile
            {
                RelativePath = NormalizeRelativePath(file.RelativePath, "Files.RelativePath"),
                Length = file.Length,
                Sha256 = NormalizeSha256(file.Sha256, "Files.Sha256"),
            });
        }
        files.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        EnsureDistinctFilePaths(files);

        var packages = new List<HarmonyOSSdkBundleLockedPackage>(value.Packages.Count);
        for (int i = 0; i < value.Packages.Count; i++)
        {
            HarmonyOSSdkBundleLockedPackage package = value.Packages[i] ?? throw new InvalidDataException("OpenHarmony SDK Bundle lock contains a null package entry.");
            packages.Add(new HarmonyOSSdkBundleLockedPackage
            {
                Id = RequireName(package.Id, "Packages.Id"),
                Version = RequireName(package.Version, "Packages.Version"),
                RelativePath = NormalizeRelativePath(package.RelativePath, "Packages.RelativePath"),
                Sha256 = NormalizeSha256(package.Sha256, "Packages.Sha256"),
            });
        }
        packages.Sort(static (left, right) =>
        {
            int id = StringComparer.Ordinal.Compare(left.Id, right.Id);
            return id != 0 ? id : StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath);
        });
        EnsureDistinctPackageIds(packages);

        var normalized = new HarmonyOSSdkBundleLock
        {
            SchemaVersion = value.SchemaVersion,
            ReleaseId = releaseId,
            PackageVersion = packageVersion,
            BundleRelativePath = bundleRelativePath,
            ArchiveSha256 = archiveSha256,
            ManifestSha256 = manifestSha256,
            SourceKind = sourceKind,
            SourceUri = string.IsNullOrWhiteSpace(value.SourceUri) ? null : value.SourceUri,
            Files = files,
            Packages = packages,
        };

        string fingerprint = ComputeFingerprintCore(normalized);
        if (expectedFingerprint != null && !string.Equals(NormalizeSha256(expectedFingerprint, nameof(value.Fingerprint)), fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("OpenHarmony SDK Bundle lock fingerprint does not match its contents.");

        return new HarmonyOSSdkBundleLock
        {
            SchemaVersion = normalized.SchemaVersion,
            ReleaseId = normalized.ReleaseId,
            PackageVersion = normalized.PackageVersion,
            BundleRelativePath = normalized.BundleRelativePath,
            ArchiveSha256 = normalized.ArchiveSha256,
            ManifestSha256 = normalized.ManifestSha256,
            SourceKind = normalized.SourceKind,
            SourceUri = normalized.SourceUri,
            Files = normalized.Files,
            Packages = normalized.Packages,
            Fingerprint = includeFingerprint ? fingerprint : string.Empty,
        };
    }

    private static string ComputeFingerprintCore(HarmonyOSSdkBundleLock normalized)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write(normalized.SchemaVersion);
            WriteString(writer, normalized.ReleaseId);
            WriteString(writer, normalized.PackageVersion);
            WriteString(writer, normalized.BundleRelativePath);
            WriteString(writer, normalized.ArchiveSha256);
            WriteString(writer, normalized.ManifestSha256);
            WriteString(writer, normalized.SourceKind);
            WriteString(writer, normalized.SourceUri ?? string.Empty);

            writer.Write(normalized.Files.Count);
            for (int i = 0; i < normalized.Files.Count; i++)
            {
                HarmonyOSSdkBundleLockedFile file = normalized.Files[i];
                WriteString(writer, file.RelativePath);
                writer.Write(file.Length);
                WriteString(writer, file.Sha256);
            }

            writer.Write(normalized.Packages.Count);
            for (int i = 0; i < normalized.Packages.Count; i++)
            {
                HarmonyOSSdkBundleLockedPackage package = normalized.Packages[i];
                WriteString(writer, package.Id);
                WriteString(writer, package.Version);
                WriteString(writer, package.RelativePath);
                WriteString(writer, package.Sha256);
            }
        }

        byte[] hash = SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        writer.Write(value.Length);
        writer.Write(value);
    }

    private static string RequireName(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"OpenHarmony SDK Bundle lock property '{name}' is required.");
        return value;
    }

    private static string NormalizeRelativePath(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"OpenHarmony SDK Bundle lock path '{name}' is required.");
        if (Path.IsPathFullyQualified(value) || value[0] == '/' || value[0] == '\\')
            throw new InvalidDataException($"OpenHarmony SDK Bundle lock path '{name}' must be project-relative.");

        string normalized = value.Replace('\\', '/');
        string[] segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length == 0)
            throw new InvalidDataException($"OpenHarmony SDK Bundle lock path '{name}' is invalid.");

        for (int i = 0; i < segments.Length; i++)
        {
            if (string.IsNullOrEmpty(segments[i]) || segments[i] == "." || segments[i] == "..")
                throw new InvalidDataException($"OpenHarmony SDK Bundle lock path '{name}' contains an unsafe segment.");
        }

        return string.Join('/', segments);
    }

    private static string NormalizeSha256(string? value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64)
            throw new InvalidDataException($"OpenHarmony SDK Bundle lock hash '{name}' must be a 64-character SHA-256 value.");

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
                throw new InvalidDataException($"OpenHarmony SDK Bundle lock hash '{name}' must use lowercase hexadecimal.");
        }

        return value;
    }

    private static void EnsureDistinctFilePaths(List<HarmonyOSSdkBundleLockedFile> files)
    {
        for (int i = 1; i < files.Count; i++)
        {
            if (string.Equals(files[i - 1].RelativePath, files[i].RelativePath, StringComparison.Ordinal))
                throw new InvalidDataException($"OpenHarmony SDK Bundle lock contains duplicate file '{files[i].RelativePath}'.");
        }
    }

    private static void EnsureDistinctPackageIds(List<HarmonyOSSdkBundleLockedPackage> packages)
    {
        for (int i = 1; i < packages.Count; i++)
        {
            if (string.Equals(packages[i - 1].Id, packages[i].Id, StringComparison.Ordinal))
                throw new InvalidDataException($"OpenHarmony SDK Bundle lock contains duplicate package '{packages[i].Id}'.");
        }
    }
}
