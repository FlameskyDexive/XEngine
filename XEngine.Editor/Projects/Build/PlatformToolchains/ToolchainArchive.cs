// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Runtime.AssetBundles;

namespace XEngine.Editor.Projects.Build.PlatformToolchains;

/// <summary>Acquisition primitives shared by every platform toolchain component: multi-source
/// resumable download, SHA-256 verification, and guarded zip/tar.gz extraction. The HTTP transport
/// reuses the runtime's <see cref="HttpClientDownloadBackend"/> (Range resume + .part staging) so
/// toolchain archives and asset bundles share one battle-tested downloader.</summary>
public static class ToolchainArchive
{
    /// <summary>Download <paramref name="url"/> to <paramref name="targetFile"/>, verifying a
    /// pinned <paramref name="expectedSha256"/> when present. Returns the artifact's actual hex
    /// sha256. Throws <see cref="ToolchainArchiveException"/> on integrity mismatch or transport
    /// failure (all sources are tried by the caller; this method is single-source).</summary>
    public static async Task<string> DownloadAsync(
        string url, string targetFile, string? expectedSha256,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellation = default)
    {
        var backend = new HttpClientDownloadBackend { PerRequestTimeout = TimeSpan.FromSeconds(60) };
        await backend.DownloadAsync(url, targetFile, progress, cancellation).ConfigureAwait(false);

        string actual = await ComputeSha256Async(targetFile, cancellation).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(expectedSha256)
            && !string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolchainArchiveException(
                $"Integrity mismatch for '{url}': expected sha256 {expectedSha256}, got {actual}.");
        }
        return actual;
    }

    public static async Task<string> ComputeSha256Async(string file, CancellationToken cancellation = default)
    {
        await using FileStream stream = File.OpenRead(file);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Extract a zip into <paramref name="destinationDir"/>, stripping
    /// <paramref name="stripRootSegments"/> leading path segments from every entry. Progress is
    /// reported in uncompressed bytes written. Traversal entries throw; a clean partial extract is
    /// left in the destination (callers extract into staging and publish atomically).</summary>
    public static async Task ExtractZipAsync(
        string archiveFile, string destinationDir, int stripRootSegments = 0,
        IProgress<long>? progress = null, CancellationToken cancellation = default)
    {
        await using FileStream input = File.OpenRead(archiveFile);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);

        // Directory entries are declared either by a trailing slash OR implicitly by being the
        // prefix of another entry (several vendor zips — Temurin, Android cmdline-tools — store
        // directory markers as zero-length entries WITHOUT the trailing slash, which would
        // otherwise hit File.Create on a directory path).
        var relativeNames = new string[archive.Entries.Count];
        var directories = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            string relative = NormalizeEntryName(archive.Entries[i].FullName, stripRootSegments);
            relativeNames[i] = relative;
            if (relative.Length == 0) continue;
            int slash;
            string prefix = relative;
            while ((slash = prefix.LastIndexOf('/')) > 0)
            {
                prefix = prefix[..slash];
                directories.Add(prefix);
            }
        }

        long written = 0;
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            ZipArchiveEntry entry = archive.Entries[i];
            cancellation.ThrowIfCancellationRequested();
            string relative = relativeNames[i];
            if (relative.Length == 0) continue;
            string destinationPath = SafeCombine(destinationDir, relative);

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || directories.Contains(relative))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using Stream source = entry.Open();
            await using Stream target = File.Create(destinationPath);
            await source.CopyToAsync(target, 81920, cancellation).ConfigureAwait(false);
            written += entry.Length;
            progress?.Report(written);
        }
    }

    /// <summary>Extract a .tar.gz (the ohos-sdk layout). Same contract as
    /// <see cref="ExtractZipAsync"/>; non-regular entry types (symlinks/hardlinks) are rejected —
    /// SDK archives must be materializable on Windows without link semantics.</summary>
    public static async Task ExtractTarGzAsync(
        string archiveFile, string destinationDir, int stripRootSegments = 0,
        IProgress<long>? progress = null, CancellationToken cancellation = default)
    {
        await using FileStream input = File.OpenRead(archiveFile);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
        await using var reader = new TarReader(gzip, leaveOpen: true);

        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellation).ConfigureAwait(false)) is not null)
        {
            cancellation.ThrowIfCancellationRequested();
            string relative = NormalizeEntryName(entry.Name, stripRootSegments);
            if (relative.Length == 0) continue;
            string destinationPath = SafeCombine(destinationDir, relative);

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destinationPath);
                    break;
                case TarEntryType.RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    await using (Stream target = File.Create(destinationPath))
                    {
                        if (entry.DataStream is not null)
                            await entry.DataStream.CopyToAsync(target, 81920, cancellation).ConfigureAwait(false);
                    }
                    break;
                default:
                    throw new ToolchainArchiveException(
                        $"tar entry '{entry.Name}' has unsupported type '{entry.EntryType}' (links are rejected).");
            }
            progress?.Report(1);
        }
    }

    public static bool IsTarGz(string fileName)
        => fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
           || fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
           // URL-derived names may keep only the final ".gz" of "….tar.gz".
           || fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeEntryName(string entryName, int stripRootSegments)
    {
        string normalized = entryName.Replace('\\', '/');
        // Windows zip writers sometimes prefix "./".
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        for (int i = 0; i < stripRootSegments; i++)
        {
            int slash = normalized.IndexOf('/');
            if (slash < 0) return ""; // entry IS a root segment being stripped
            normalized = normalized[(slash + 1)..];
        }
        return normalized.TrimEnd('/');
    }

    private static string SafeCombine(string destinationDir, string relative)
    {
        string destinationRoot = Path.GetFullPath(destinationDir);
        string destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, relative));
        if (!destinationPath.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ToolchainArchiveException($"archive entry '{relative}' escapes the extraction directory.");
        return destinationPath;
    }
}

public sealed class ToolchainArchiveException : Exception
{
    public ToolchainArchiveException(string message) : base(message) { }
}
