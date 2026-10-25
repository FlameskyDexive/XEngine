// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>
/// Imports an immutable OpenHarmony SDK Bundle. The archive is fully validated before any
/// project directory is created, then extracted into a private staging sibling and published once.
/// </summary>
internal sealed class HarmonyOSSdkBundleImporter
{
    private const string ManifestEntryName = "release-manifest.json";
    private const int ManifestSchemaVersion = 1;
    private const int CopyBufferSize = 1024 * 1024;
    private const string PackageValidationTempDirectoryName = "XEngine/HarmonyOSBundleValidation";

    private static readonly string[] s_requiredPackageIds =
    [
        "OpenHarmony.NET.Runtime.NativeAot.arm64-v8a",
        "OpenHarmony.NET.Runtime.NativeAot.x86_64",
        "OpenHarmony.NET.PublishAotCross",
        "OpenHarmony.NDK.Bindings",
        "OpenHarmony.NET.Player.Sdk",
        "OpenHarmony.NET.HapTooling",
    ];

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal async Task<HarmonyOSSdkBundleImportResult> ImportAsync(
        HarmonyOSSdkBundleImportRequest request,
        IProgress<HarmonyOSSdkBundleImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string projectRoot = NormalizeProjectRoot(request.ProjectRoot);
        string archivePath = NormalizeArchivePath(request.ArchivePath);
        string bundleRelativeRoot = NormalizeRelativePath(request.BundleRelativeRoot, "BundleRelativeRoot");
        string sourceKind = RequireName(request.SourceKind, "SourceKind");
        string archiveSha256 = await ComputeFileSha256Async(archivePath, cancellationToken).ConfigureAwait(false);

        ArchiveIndex archive = await ReadArchiveIndexAsync(archivePath, cancellationToken).ConfigureAwait(false);
        ReleaseManifest manifest = DeserializeManifest(archive.ManifestBytes);
        ValidateManifest(manifest, archive);
        await ValidateArchiveContentsAsync(archivePath, archive, manifest, progress, cancellationToken).ConfigureAwait(false);

        string bundleParent = GetContainedPath(projectRoot, bundleRelativeRoot);
        string releaseRoot = GetContainedPath(bundleParent, manifest.ReleaseId);
        string lockPath = GetContainedPath(bundleParent, HarmonyOSSdkBundleLockFile.DefaultFileName);
        string importLockPath = GetContainedPath(projectRoot, "Library/OpenHarmonySdk/.import.lock");
        string manifestSha256 = ComputeSha256(archive.ManifestBytes);

        HarmonyOSSdkBundleLock expectedLock = StampFingerprint(CreateLock(
            bundleRelativeRoot,
            manifest,
            archiveSha256,
            manifestSha256,
            sourceKind,
            request.SourceUri));

        await using FileStream importLock = await AcquireImportLockAsync(importLockPath, cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(releaseRoot))
        {
            HarmonyOSSdkBundleLock existing = await VerifyExistingReleaseAsync(
                releaseRoot,
                lockPath,
                expectedLock,
                cancellationToken).ConfigureAwait(false);
            return new HarmonyOSSdkBundleImportResult
            {
                ImportedRoot = releaseRoot,
                LockPath = lockPath,
                Reused = true,
                Lock = existing,
            };
        }

        Directory.CreateDirectory(bundleParent);
        string stagingRoot = Path.Combine(
            bundleParent,
            "." + manifest.ReleaseId + "." + Guid.NewGuid().ToString("N") + ".staging");
        EnsureContainedPath(bundleParent, stagingRoot);
        Directory.CreateDirectory(stagingRoot);
        bool published = false;
        try
        {
            await ExtractAndVerifyAsync(
                archivePath,
                archive,
                manifest,
                stagingRoot,
                progress,
                cancellationToken).ConfigureAwait(false);

            string finalArchiveSha256 = await ComputeFileSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(finalArchiveSha256, archiveSha256, StringComparison.Ordinal))
                throw BundleError(HarmonyOSBundleErrorCode.HashMismatch, "The OpenHarmony SDK Bundle archive changed during import.");

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(releaseRoot))
                throw BundleError(HarmonyOSBundleErrorCode.ReleaseConflict, "The OpenHarmony SDK release appeared while it was being imported.");

            Directory.Move(stagingRoot, releaseRoot);
            published = true;
            await HarmonyOSSdkBundleLockFile.WriteAsync(lockPath, expectedLock, cancellationToken).ConfigureAwait(false);

            return new HarmonyOSSdkBundleImportResult
            {
                ImportedRoot = releaseRoot,
                LockPath = lockPath,
                Reused = false,
                Lock = expectedLock,
            };
        }
        catch
        {
            if (published)
                DeleteKnownDirectory(releaseRoot, bundleParent);
            else
                DeleteKnownDirectory(stagingRoot, bundleParent);
            throw;
        }
    }

    private static string NormalizeProjectRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("ProjectRoot is required.", nameof(value));
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("ProjectRoot is not a valid path.", nameof(value), exception);
        }
    }

    private static string NormalizeArchivePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw BundleError(HarmonyOSBundleErrorCode.InvalidArchive, "The OpenHarmony SDK Bundle archive path is required.");
        string path;
        try
        {
            path = Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw BundleError(HarmonyOSBundleErrorCode.InvalidArchive, "The OpenHarmony SDK Bundle archive path is invalid.", exception);
        }

        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw BundleError(HarmonyOSBundleErrorCode.InvalidArchive, $"The OpenHarmony SDK Bundle archive does not exist: {path}");
        return path;
    }

    private static async Task<ArchiveIndex> ReadArchiveIndexAsync(string archivePath, CancellationToken cancellationToken)
    {
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(archivePath);
            var entries = new List<ArchiveEntry>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            byte[]? manifestBytes = null;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = ValidateEntryName(entry.FullName);
                if (!names.Add(name))
                    throw BundleError(HarmonyOSBundleErrorCode.InvalidArchive, $"The Bundle contains a duplicate ZIP entry: {name}");
                if (string.Equals(name, ManifestEntryName, StringComparison.Ordinal))
                {
                    await using Stream stream = entry.Open();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                    manifestBytes = buffer.ToArray();
                }
                else
                {
                    entries.Add(new ArchiveEntry(name, entry.Length));
                }
            }

            if (manifestBytes == null)
                throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, "The Bundle is missing release-manifest.json.");
            return new ArchiveIndex(entries, manifestBytes);
        }
        catch (HarmonyOSBundleException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw BundleError(HarmonyOSBundleErrorCode.InvalidArchive, "The OpenHarmony SDK Bundle ZIP archive is invalid.", exception);
        }
    }

    private static ReleaseManifest DeserializeManifest(byte[] bytes)
    {
        try
        {
            ReleaseManifest? manifest = JsonSerializer.Deserialize<ReleaseManifest>(bytes, s_jsonOptions);
            return manifest ?? throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, "The Bundle manifest is empty.");
        }
        catch (HarmonyOSBundleException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, "The Bundle manifest is not valid JSON.", exception);
        }
    }

    private static void ValidateManifest(ReleaseManifest manifest, ArchiveIndex archive)
    {
        if (manifest.SchemaVersion != ManifestSchemaVersion)
            throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, $"Unsupported Bundle manifest schema version '{manifest.SchemaVersion}'.");
        string releaseId = RequireName(manifest.ReleaseId, "ReleaseId");
        _ = HarmonyOSBundleCapabilities.ValidateDeclared(manifest.Capabilities);
        if (!IsSafePathSegment(releaseId))
            throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, "Bundle ReleaseId must be one safe path segment.");
        RequireName(manifest.PackageVersion, "PackageVersion");
        if (manifest.Files == null || manifest.Files.Count == 0)
            throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, "The Bundle manifest does not declare any files.");
        if (manifest.Packages == null)
            throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, "The Bundle manifest package list is missing.");

        var declaredFiles = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < manifest.Files.Count; i++)
        {
            ManifestFile file = manifest.Files[i] ?? throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, "The Bundle manifest contains a null file entry.");
            string path = ValidateEntryName(file.Path);
            if (string.Equals(path, ManifestEntryName, StringComparison.Ordinal))
                throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, "release-manifest.json cannot declare itself.");
            if (file.Length < 0 || !IsSha256(file.Sha256))
                throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, $"The Bundle manifest file metadata is invalid: {path}");
            if (!declaredFiles.TryAdd(path, file))
                throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, $"The Bundle manifest declares a duplicate file: {path}");
            if (!IsAllowedBundleFile(path))
                throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, $"The Bundle manifest declares a file outside the Bundle contract: {path}");
        }

        var actualFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            ArchiveEntry entry = archive.Entries[i];
            actualFiles.Add(entry.Name);
            if (!declaredFiles.ContainsKey(entry.Name))
                throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, $"The Bundle contains an undeclared entry: {entry.Name}");
        }
        if (actualFiles.Count != declaredFiles.Count)
            throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, "The Bundle manifest file set does not exactly match the ZIP entries.");

        RequireDeclaredFile(declaredFiles, "SHA256SUMS");
        RequireDeclaredFile(declaredFiles, "sbom.spdx.json");
        RequireDeclaredFile(declaredFiles, "schemas/sdk-catalog.json");
        RequireDeclaredPrefix(declaredFiles, "templates/stage-player/");
        RequireDeclaredPrefix(declaredFiles, "licenses/");

        var packages = new Dictionary<string, ManifestPackage>(StringComparer.Ordinal);
        var packagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < manifest.Packages.Count; i++)
        {
            ManifestPackage package = manifest.Packages[i] ?? throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, "The Bundle manifest contains a null package entry.");
            string id = RequireName(package.Id, "Packages.Id");
            string path = ValidateEntryName(package.Path);
            string expectedPath = "feed/" + id + "." + manifest.PackageVersion + ".nupkg";
            if (!string.Equals(path, expectedPath, StringComparison.Ordinal) ||
                !string.Equals(package.Version, manifest.PackageVersion, StringComparison.Ordinal) ||
                !IsSha256(package.Sha256))
                throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"The Bundle package contract is invalid: {id}");
            if (!packages.TryAdd(id, package) ||
                !packagePaths.Add(path) ||
                !declaredFiles.TryGetValue(path, out ManifestFile? file) ||
                !string.Equals(file.Sha256, package.Sha256, StringComparison.Ordinal))
                throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"The Bundle package metadata is inconsistent: {id}");
        }

        for (int i = 0; i < s_requiredPackageIds.Length; i++)
        {
            if (!packages.ContainsKey(s_requiredPackageIds[i]))
                throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"The Bundle is missing required package: {s_requiredPackageIds[i]}");
        }
        if (packages.Count != s_requiredPackageIds.Length)
            throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, "The Bundle contains an unsupported package ID.");
        foreach (string filePath in declaredFiles.Keys)
        {
            if (filePath.StartsWith("feed/", StringComparison.Ordinal) && !packagePaths.Contains(filePath))
                throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"The Bundle feed contains a package not declared by the package contract: {filePath}");
        }
    }

    private static async Task ValidateArchiveContentsAsync(
        string archivePath,
        ArchiveIndex archive,
        ReleaseManifest manifest,
        IProgress<HarmonyOSSdkBundleImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var expected = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < manifest.Files.Count; i++)
            expected.Add(ValidateEntryName(manifest.Files[i].Path), manifest.Files[i]);
        var packages = new Dictionary<string, ManifestPackage>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < manifest.Packages.Count; i++)
        {
            ManifestPackage package = manifest.Packages[i];
            packages.Add(ValidateEntryName(package.Path), package);
        }

        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        long totalBytes = 0;
        for (int i = 0; i < archive.Entries.Count; i++)
            totalBytes += archive.Entries[i].Length;
        long completedBytes = 0;
        int completedFiles = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string name = ValidateEntryName(entry.FullName);
            if (string.Equals(name, ManifestEntryName, StringComparison.Ordinal))
                continue;
            ManifestFile file = expected[name];
            if (entry.Length != file.Length)
                throw BundleError(HarmonyOSBundleErrorCode.HashMismatch, $"Bundle entry length differs from its manifest: {name}");
            await using Stream stream = entry.Open();
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            string actualHash = Convert.ToHexString(hash).ToLowerInvariant();
            if (!string.Equals(actualHash, file.Sha256, StringComparison.Ordinal))
                throw BundleError(HarmonyOSBundleErrorCode.HashMismatch, $"Bundle entry hash differs from its manifest: {name}");
            if (packages.TryGetValue(name, out ManifestPackage? package))
                await ValidateNupkgArchiveEntryAsync(entry, package, cancellationToken).ConfigureAwait(false);
            completedBytes += entry.Length;
            completedFiles++;
            progress?.Report(new HarmonyOSSdkBundleImportProgress("Verifying", name, completedFiles, archive.Entries.Count, completedBytes, totalBytes));
        }
    }

    private static HarmonyOSSdkBundleLock CreateLock(
        string bundleRelativeRoot,
        ReleaseManifest manifest,
        string archiveSha256,
        string manifestSha256,
        string sourceKind,
        string? sourceUri)
    {
        var files = new List<HarmonyOSSdkBundleLockedFile>(manifest.Files.Count);
        for (int i = 0; i < manifest.Files.Count; i++)
        {
            ManifestFile file = manifest.Files[i];
            files.Add(new HarmonyOSSdkBundleLockedFile
            {
                RelativePath = ValidateEntryName(file.Path),
                Length = file.Length,
                Sha256 = file.Sha256,
            });
        }
        files.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));

        var packages = new List<HarmonyOSSdkBundleLockedPackage>(manifest.Packages.Count);
        for (int i = 0; i < manifest.Packages.Count; i++)
        {
            ManifestPackage package = manifest.Packages[i];
            packages.Add(new HarmonyOSSdkBundleLockedPackage
            {
                Id = package.Id,
                Version = package.Version,
                RelativePath = ValidateEntryName(package.Path),
                Sha256 = package.Sha256,
            });
        }
        packages.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));

        return new HarmonyOSSdkBundleLock
        {
            ReleaseId = manifest.ReleaseId,
            PackageVersion = manifest.PackageVersion,
            BundleRelativePath = bundleRelativeRoot + "/" + manifest.ReleaseId,
            ArchiveSha256 = archiveSha256,
            ManifestSha256 = manifestSha256,
            SourceKind = sourceKind,
            SourceUri = sourceUri,
            Files = files,
            Packages = packages,
        };
    }

    private static async Task ExtractAndVerifyAsync(
        string archivePath,
        ArchiveIndex archive,
        ReleaseManifest manifest,
        string stagingRoot,
        IProgress<HarmonyOSSdkBundleImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var expected = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < manifest.Files.Count; i++)
            expected.Add(ValidateEntryName(manifest.Files[i].Path), manifest.Files[i]);

        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        long totalBytes = 0;
        for (int i = 0; i < archive.Entries.Count; i++)
            totalBytes += archive.Entries[i].Length;
        long completedBytes = 0;
        int completedFiles = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = ValidateEntryName(entry.FullName);
            string destination = GetContainedPath(stagingRoot, name);
            string? parent = Path.GetDirectoryName(destination);
            if (parent != null)
                Directory.CreateDirectory(parent);
            await using (Stream source = entry.Open())
            await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            ManifestFile? expectedFile = string.Equals(name, ManifestEntryName, StringComparison.Ordinal) ? null : expected[name];
            FileInfo info = new(destination);
            if (expectedFile != null && info.Length != expectedFile.Length)
                throw BundleError(HarmonyOSBundleErrorCode.HashMismatch, $"Extracted Bundle file length differs: {name}");
            if (expectedFile != null)
            {
                string hash = await ComputeFileSha256Async(destination, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, expectedFile.Sha256, StringComparison.Ordinal))
                    throw BundleError(HarmonyOSBundleErrorCode.HashMismatch, $"Extracted Bundle file hash differs: {name}");
            }

            completedBytes += info.Length;
            completedFiles++;
            progress?.Report(new HarmonyOSSdkBundleImportProgress("Extracting", name, completedFiles, archive.Entries.Count + 1, completedBytes, totalBytes));
        }
    }

    private static async Task<HarmonyOSSdkBundleLock> VerifyExistingReleaseAsync(
        string releaseRoot,
        string lockPath,
        HarmonyOSSdkBundleLock expected,
        CancellationToken cancellationToken)
    {
        HarmonyOSSdkBundleLock existing;
        try
        {
            existing = await HarmonyOSSdkBundleLockFile.ReadAsync(lockPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw BundleError(HarmonyOSBundleErrorCode.ReleaseConflict, "The existing OpenHarmony SDK release has no valid lock file.", exception);
        }

        if (!string.Equals(existing.BundleRelativePath, expected.BundleRelativePath, StringComparison.Ordinal) ||
            !string.Equals(existing.ArchiveSha256, expected.ArchiveSha256, StringComparison.Ordinal) ||
            !string.Equals(existing.ManifestSha256, expected.ManifestSha256, StringComparison.Ordinal) ||
            !LocksDescribeSameBundle(existing, expected))
            throw BundleError(HarmonyOSBundleErrorCode.ReleaseConflict, "The existing OpenHarmony SDK release does not match the selected archive.");

        for (int i = 0; i < existing.Files.Count; i++)
        {
            HarmonyOSSdkBundleLockedFile file = existing.Files[i];
            string path = GetContainedPath(releaseRoot, file.RelativePath);
            if (!File.Exists(path))
                throw BundleError(HarmonyOSBundleErrorCode.ReleaseConflict, $"The existing Bundle is missing: {file.RelativePath}");
            FileInfo info = new(path);
            if (info.Length != file.Length || !string.Equals(await ComputeFileSha256Async(path, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.Ordinal))
                throw BundleError(HarmonyOSBundleErrorCode.ReleaseConflict, $"The existing Bundle file failed verification: {file.RelativePath}");
        }

        string manifestPath = GetContainedPath(releaseRoot, ManifestEntryName);
        if (!File.Exists(manifestPath) || !string.Equals(await ComputeFileSha256Async(manifestPath, cancellationToken).ConfigureAwait(false), expected.ManifestSha256, StringComparison.Ordinal))
            throw BundleError(HarmonyOSBundleErrorCode.ReleaseConflict, "The existing Bundle manifest failed verification.");
        return existing;
    }

    private static bool LocksDescribeSameBundle(HarmonyOSSdkBundleLock left, HarmonyOSSdkBundleLock right)
    {
        if (!string.Equals(left.ReleaseId, right.ReleaseId, StringComparison.Ordinal) ||
            !string.Equals(left.PackageVersion, right.PackageVersion, StringComparison.Ordinal) ||
            left.Files.Count != right.Files.Count ||
            left.Packages.Count != right.Packages.Count)
            return false;

        for (int i = 0; i < left.Files.Count; i++)
        {
            HarmonyOSSdkBundleLockedFile leftFile = left.Files[i];
            HarmonyOSSdkBundleLockedFile rightFile = right.Files[i];
            if (!string.Equals(leftFile.RelativePath, rightFile.RelativePath, StringComparison.Ordinal) ||
                leftFile.Length != rightFile.Length ||
                !string.Equals(leftFile.Sha256, rightFile.Sha256, StringComparison.Ordinal))
                return false;
        }

        for (int i = 0; i < left.Packages.Count; i++)
        {
            HarmonyOSSdkBundleLockedPackage leftPackage = left.Packages[i];
            HarmonyOSSdkBundleLockedPackage rightPackage = right.Packages[i];
            if (!string.Equals(leftPackage.Id, rightPackage.Id, StringComparison.Ordinal) ||
                !string.Equals(leftPackage.Version, rightPackage.Version, StringComparison.Ordinal) ||
                !string.Equals(leftPackage.RelativePath, rightPackage.RelativePath, StringComparison.Ordinal) ||
                !string.Equals(leftPackage.Sha256, rightPackage.Sha256, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<FileStream> AcquireImportLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(lockPath)!;
        Directory.CreateDirectory(directory);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ValidateNupkgArchiveEntryAsync(
        ZipArchiveEntry entry,
        ManifestPackage expected,
        CancellationToken cancellationToken)
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), PackageValidationTempDirectoryName);
        Directory.CreateDirectory(temporaryDirectory);
        string temporaryPath = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".nupkg");
        try
        {
            await using (Stream source = entry.Open())
            await using (var target = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            ValidateNupkgIdentity(temporaryPath, expected);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void ValidateNupkgIdentity(string path, ManifestPackage expected)
    {
        try
        {
            using ZipArchive package = ZipFile.OpenRead(path);
            ZipArchiveEntry? nuspec = null;
            foreach (ZipArchiveEntry entry in package.Entries)
            {
                if (!entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (nuspec != null)
                    throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"Package contains multiple nuspec files: {expected.Id}");
                nuspec = entry;
            }
            if (nuspec == null)
                throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"Package is missing its nuspec identity: {expected.Id}");

            using Stream stream = nuspec.Open();
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            var document = new XmlDocument
            {
                XmlResolver = null,
            };
            document.Load(reader);
            XmlElement? packageRoot = document.DocumentElement;
            if (packageRoot == null || !string.Equals(packageRoot.LocalName, "package", StringComparison.Ordinal))
                throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"Package nuspec root is invalid: {expected.Id}");

            XmlElement? metadata = null;
            foreach (XmlNode child in packageRoot.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element || !string.Equals(child.LocalName, "metadata", StringComparison.Ordinal))
                    continue;
                if (metadata != null)
                    throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"Package nuspec has multiple metadata nodes: {expected.Id}");
                metadata = (XmlElement)child;
            }

            if (metadata == null)
                throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"Package nuspec metadata is missing: {expected.Id}");

            XmlNodeList identityNodes = document.GetElementsByTagName("*");
            for (int i = 0; i < identityNodes.Count; i++)
            {
                XmlNode node = identityNodes[i]!;
                if (string.Equals(node.LocalName, "metadata", StringComparison.Ordinal) && !ReferenceEquals(node.ParentNode, packageRoot))
                    throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"Package nuspec contains nested metadata: {expected.Id}");
                if ((string.Equals(node.LocalName, "id", StringComparison.Ordinal) ||
                     string.Equals(node.LocalName, "version", StringComparison.Ordinal)) &&
                    !ReferenceEquals(node.ParentNode, metadata))
                    throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"Package nuspec contains nested identity fields: {expected.Id}");
            }

            string? id = null;
            string? version = null;
            foreach (XmlNode child in metadata.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element)
                    continue;
                if (string.Equals(child.LocalName, "id", StringComparison.Ordinal))
                {
                    if (id != null)
                        throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"Package nuspec has multiple id nodes: {expected.Id}");
                    id = child.InnerText.Trim();
                }
                else if (string.Equals(child.LocalName, "version", StringComparison.Ordinal))
                {
                    if (version != null)
                        throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"Package nuspec has multiple version nodes: {expected.Id}");
                    version = child.InnerText.Trim();
                }
            }
            if (!string.Equals(id, expected.Id, StringComparison.Ordinal) || !string.Equals(version, expected.Version, StringComparison.Ordinal))
                throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"Package nuspec identity does not match the Bundle manifest: {expected.Id}");
        }
        catch (HarmonyOSBundleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or XmlException)
        {
            throw BundleError(HarmonyOSBundleErrorCode.PackageContractMismatch, $"Package is not a valid NuGet archive: {expected.Id}", exception);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A unique temporary file cannot affect the project import state.
        }
    }

    private static HarmonyOSSdkBundleLock StampFingerprint(HarmonyOSSdkBundleLock value) => new()
    {
        SchemaVersion = value.SchemaVersion,
        ReleaseId = value.ReleaseId,
        PackageVersion = value.PackageVersion,
        BundleRelativePath = value.BundleRelativePath,
        ArchiveSha256 = value.ArchiveSha256,
        ManifestSha256 = value.ManifestSha256,
        SourceKind = value.SourceKind,
        SourceUri = value.SourceUri,
        Files = value.Files,
        Packages = value.Packages,
        Fingerprint = HarmonyOSSdkBundleLockFile.ComputeFingerprint(value),
    };

    private static string ComputeSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ValidateEntryName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\\') >= 0 || value.IndexOf(':') >= 0)
            throw BundleError(HarmonyOSBundleErrorCode.ZipTraversal, "Bundle entry names must use safe forward-slash paths.");
        if (value[0] == '/' || value.EndsWith("/", StringComparison.Ordinal))
            throw BundleError(HarmonyOSBundleErrorCode.ZipTraversal, $"Bundle entry path is rooted or a directory: {value}");
        string[] segments = value.Split('/', StringSplitOptions.None);
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0 || segments[i] == "." || segments[i] == "..")
                throw BundleError(HarmonyOSBundleErrorCode.ZipTraversal, $"Bundle entry path contains an unsafe segment: {value}");
            if (segments[i].EndsWith(" ", StringComparison.Ordinal) || segments[i].EndsWith(".", StringComparison.Ordinal) || IsWindowsReservedName(segments[i]))
                throw BundleError(HarmonyOSBundleErrorCode.ZipTraversal, $"Bundle entry path contains a Windows-ambiguous segment: {value}");
            for (int j = 0; j < segments[i].Length; j++)
            {
                if (segments[i][j] < ' ')
                    throw BundleError(HarmonyOSBundleErrorCode.ZipTraversal, $"Bundle entry path contains a control character: {value}");
            }
        }
        return value;
    }

    private static bool IsAllowedBundleFile(string path) =>
        string.Equals(path, "SHA256SUMS", StringComparison.Ordinal) ||
        string.Equals(path, "sbom.spdx.json", StringComparison.Ordinal) ||
        path.StartsWith("feed/", StringComparison.Ordinal) ||
        path.StartsWith("schemas/", StringComparison.Ordinal) ||
        path.StartsWith("templates/stage-player/", StringComparison.Ordinal) ||
        path.StartsWith("licenses/", StringComparison.Ordinal);

    private static bool IsWindowsReservedName(string segment)
    {
        string stem = segment;
        int dot = stem.IndexOf('.');
        if (dot >= 0)
            stem = stem[..dot];
        if (string.Equals(stem, "CON", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stem, "PRN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stem, "AUX", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stem, "NUL", StringComparison.OrdinalIgnoreCase))
            return true;
        return stem.Length == 4 &&
               ((stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                stem[3] >= '1' && stem[3] <= '9');
    }

    private static string NormalizeRelativePath(string value, string name)
    {
        try
        {
            return ValidateEntryName(value);
        }
        catch (HarmonyOSBundleException exception)
        {
            throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, $"{name} is not a safe relative path.", exception);
        }
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(root, normalized));
        EnsureContainedPath(root, full);
        return full;
    }

    private static void EnsureContainedPath(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw BundleError(HarmonyOSBundleErrorCode.ZipTraversal, $"Bundle path escapes its destination: {path}");
    }

    private static bool IsSafePathSegment(string value) =>
        value.IndexOfAny(['/', '\\', ':']) < 0 && value != "." && value != ".." && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string RequireName(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, $"Bundle manifest property '{name}' is required.");
        return value;
    }

    private static bool IsSha256(string? value)
    {
        if (value == null || value.Length != 64)
            return false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
                return false;
        }
        return true;
    }

    private static void RequireDeclaredFile(Dictionary<string, ManifestFile> files, string path)
    {
        if (!files.ContainsKey(path))
            throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, $"The Bundle is missing required file: {path}");
    }

    private static void RequireDeclaredPrefix(Dictionary<string, ManifestFile> files, string prefix)
    {
        foreach (string path in files.Keys)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
                return;
        }
        throw BundleError(HarmonyOSBundleErrorCode.ManifestInvalid, $"The Bundle is missing required directory content: {prefix}");
    }

    private static HarmonyOSBundleException BundleError(HarmonyOSBundleErrorCode code, string message, Exception? inner = null) =>
        new(code, message, inner);

    private static void DeleteKnownDirectory(string path, string parent)
    {
        if (!Directory.Exists(path))
            return;
        EnsureContainedPath(parent, path);
        Directory.Delete(path, recursive: true);
    }

    private sealed record ArchiveEntry(string Name, long Length);

    private sealed record ArchiveIndex(List<ArchiveEntry> Entries, byte[] ManifestBytes);

    private sealed class ReleaseManifest
    {
        public int SchemaVersion { get; init; }
        public string ReleaseId { get; init; } = string.Empty;
        public string PackageVersion { get; init; } = string.Empty;
        public List<string>? Capabilities { get; init; }
        public List<ManifestFile> Files { get; init; } = [];
        public List<ManifestPackage> Packages { get; init; } = [];
    }

    private sealed class ManifestFile
    {
        public string Path { get; init; } = string.Empty;
        public long Length { get; init; }
        public string Sha256 { get; init; } = string.Empty;
    }

    private sealed class ManifestPackage
    {
        public string Id { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
    }
}
