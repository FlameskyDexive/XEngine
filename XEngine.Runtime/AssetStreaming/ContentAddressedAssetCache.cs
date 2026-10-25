// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Runtime.AssetStreaming;

public readonly record struct AssetCacheMaterialization(
    string FilePath,
    string Sha256,
    long Bytes,
    bool Reused);

/// <summary>Atomically materializes a packaged stream into a content-addressed cache file.</summary>
public static class ContentAddressedAssetCache
{
    public static async Task<AssetCacheMaterialization> MaterializeAsync(
        IAssetStreamProvider provider,
        string assetPath,
        string cacheDirectory,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        string normalizedPath = AssetStreamPath.Normalize(assetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        string expectedHash = NormalizeHash(expectedSha256);
        string cacheRoot = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(cacheRoot);
        string extension = Path.GetExtension(normalizedPath);
        string destination = Path.Combine(cacheRoot, expectedHash + extension);

        if (File.Exists(destination))
        {
            string existingHash = await HashFileAsync(destination, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existingHash, expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException($"Cached asset hash mismatch: {destination}");
            return new AssetCacheMaterialization(
                destination,
                existingHash,
                new FileInfo(destination).Length,
                Reused: true);
        }

        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using Stream input = await provider.OpenReadAsync(normalizedPath, cancellationToken)
                ?? throw new FileNotFoundException(
                    $"Asset '{normalizedPath}' is missing from {provider.Description}.");
            long bytes = 0;
            string actualHash;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                try
                {
                    while (true)
                    {
                        int read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            break;
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        bytes += read;
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                actualHash = Convert.ToHexString(hash.GetHashAndReset());
            }

            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Asset '{normalizedPath}' hash mismatch. Expected {expectedHash}, got {actualHash}.");
            }

            try
            {
                File.Move(temporary, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination))
            {
                string racedHash = await HashFileAsync(destination, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(racedHash, expectedHash, StringComparison.Ordinal))
                    throw new InvalidDataException($"Concurrent cached asset hash mismatch: {destination}");
                return new AssetCacheMaterialization(
                    destination,
                    racedHash,
                    new FileInfo(destination).Length,
                    Reused: true);
            }

            return new AssetCacheMaterialization(destination, actualHash, bytes, Reused: false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string NormalizeHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64)
            throw new ArgumentException("SHA-256 must contain 64 hexadecimal characters.", nameof(value));
        try
        {
            return Convert.ToHexString(Convert.FromHexString(value));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("SHA-256 contains invalid hexadecimal characters.", nameof(value), exception);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
