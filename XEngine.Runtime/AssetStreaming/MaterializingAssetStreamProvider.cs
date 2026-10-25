// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Async;

namespace XEngine.Runtime.AssetStreaming;

public readonly record struct PackagedAssetDescriptor(
    string Path,
    string Sha256,
    long Bytes,
    bool RequiresSeek);

/// <summary>
/// Makes selected non-seekable package streams seekable. Small payloads use verified memory;
/// larger payloads are atomically materialized into the content-addressed cache.
/// </summary>
public sealed class MaterializingAssetStreamProvider : IAssetStreamProvider
{
    private readonly IAssetStreamProvider _source;
    private readonly string _cacheDirectory;
    private readonly long _memoryThresholdBytes;
    private readonly bool _ownsSource;
    private readonly Dictionary<string, PackagedAssetDescriptor> _descriptors = new(StringComparer.Ordinal);
    private bool _disposed;

    public MaterializingAssetStreamProvider(
        IAssetStreamProvider source,
        string cacheDirectory,
        IReadOnlyList<PackagedAssetDescriptor> descriptors,
        long memoryThresholdBytes = 1024 * 1024,
        bool ownsSource = false)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(descriptors);
        if (memoryThresholdBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(memoryThresholdBytes));
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _memoryThresholdBytes = memoryThresholdBytes;
        _ownsSource = ownsSource;
        for (int i = 0; i < descriptors.Count; i++)
        {
            PackagedAssetDescriptor descriptor = descriptors[i];
            string path = AssetStreamPath.Normalize(descriptor.Path);
            if (descriptor.Bytes < 0)
                throw new ArgumentOutOfRangeException(nameof(descriptors), "Packaged asset size cannot be negative.");
            string hash = NormalizeHash(descriptor.Sha256);
            if (!_descriptors.TryAdd(path, descriptor with { Path = path, Sha256 = hash }))
                throw new ArgumentException($"Duplicate packaged asset descriptor: {path}", nameof(descriptors));
        }
    }

    public string Description => _source.Description;

    public Stream? OpenRead(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized = AssetStreamPath.Normalize(path);
        Stream? stream = _source.OpenRead(normalized);
        if (stream == null || stream.CanSeek ||
            !_descriptors.TryGetValue(normalized, out PackagedAssetDescriptor descriptor) ||
            !descriptor.RequiresSeek)
        {
            return stream;
        }

        if (descriptor.Bytes <= _memoryThresholdBytes)
            return MaterializeMemory(stream, descriptor);

        stream.Dispose();
#if XENGINE_WEB
        throw new InvalidOperationException(
            "Synchronous cache materialization is not allowed on WASM. Use OpenReadAsync.");
#else
        AssetCacheMaterialization materialized = ContentAddressedAssetCache.MaterializeAsync(
            _source,
            normalized,
            _cacheDirectory,
            descriptor.Sha256).GetAwaiter().GetResult();
        return new FileStream(
            materialized.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
#endif
    }

    public async XTask<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        string normalized = AssetStreamPath.Normalize(path);
        Stream? stream = await _source.OpenReadAsync(normalized, cancellationToken);
        if (stream == null || stream.CanSeek ||
            !_descriptors.TryGetValue(normalized, out PackagedAssetDescriptor descriptor) ||
            !descriptor.RequiresSeek)
        {
            return stream;
        }

        if (descriptor.Bytes <= _memoryThresholdBytes)
            return MaterializeMemory(stream, descriptor);

#if XENGINE_WEB
        return await MaterializeMemoryChunkedAsync(stream, descriptor, cancellationToken);
#else
        stream.Dispose();
        AssetCacheMaterialization materialized = await ContentAddressedAssetCache.MaterializeAsync(
            _source,
            normalized,
            _cacheDirectory,
            descriptor.Sha256,
            cancellationToken).ConfigureAwait(false);
        return new FileStream(
            materialized.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
#endif
    }

    public IReadOnlyList<string> GetFiles(string directory, string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _source.GetFiles(directory, extension);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsSource)
            _source.Dispose();
    }

    private static Stream MaterializeMemory(Stream source, PackagedAssetDescriptor descriptor)
    {
        using (source)
        {
            if (descriptor.Bytes > int.MaxValue)
                throw new InvalidDataException($"Packaged asset is too large for memory: {descriptor.Path}");
            var output = new MemoryStream(checked((int)descriptor.Bytes));
            try
            {
                source.CopyTo(output);
                if (output.Length != descriptor.Bytes)
                    throw new InvalidDataException($"Packaged asset size mismatch: {descriptor.Path}");
                string hash = Convert.ToHexString(SHA256.HashData(output.GetBuffer().AsSpan(0, (int)output.Length)));
                if (!string.Equals(hash, descriptor.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Packaged asset hash mismatch: {descriptor.Path}");
                output.Position = 0;
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
    }

#if XENGINE_WEB
    private static async XTask<Stream> MaterializeMemoryChunkedAsync(
        Stream source,
        PackagedAssetDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using (source)
        {
            if (descriptor.Bytes > int.MaxValue)
                throw new InvalidDataException($"Packaged asset is too large for memory: {descriptor.Path}");

            var output = new MemoryStream(checked((int)descriptor.Bytes));
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long budgetTicks = 4L * Stopwatch.Frequency / 1000L;
                long sliceStart = Stopwatch.GetTimestamp();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    hash.AppendData(buffer.AsSpan(0, read));
                    output.Write(buffer, 0, read);
                    if (Stopwatch.GetTimestamp() - sliceStart >= budgetTicks)
                    {
                        await XTask.Yield();
                        sliceStart = Stopwatch.GetTimestamp();
                    }
                }

                if (output.Length != descriptor.Bytes)
                    throw new InvalidDataException($"Packaged asset size mismatch: {descriptor.Path}");
                string actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actual, descriptor.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Packaged asset hash mismatch: {descriptor.Path}");
                output.Position = 0;
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
#endif

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
}
