// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// Download layer interfaces. The host supplies an IRemoteService (which knows the CDN base URLs for
// each file) and an IDownloadBackend (which performs the HTTP fetch). XEngine ships an
// HttpClient-based backend; tests can substitute a fake. Mirrors YooAsset's IRemoteService /
// IDownloadURLPolicy / download-backend split.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Provides the candidate download URL(s) for a bundle file. A host with multiple CDNs can return
/// several; the downloader tries them in order on failure.
/// </summary>
public interface IRemoteService
{
    /// <summary>Return one or more absolute URLs for the given file name (e.g. manifest .bytes, bundle .bundle).</summary>
    IReadOnlyList<string> GetRemoteUrls(string fileName);
}

/// <summary>Default RemoteService: a single CDN base URL. The host can subclass for multi-CDN failover.</summary>
public sealed class DefaultRemoteService : IRemoteService
{
    private readonly string _mainUrl;
    private readonly string? _fallbackUrl;

    /// <param name="mainUrl">Base URL, no trailing slash (e.g. "https://cdn.example.com/xengine").</param>
    /// <param name="fallbackUrl">Optional fallback base URL.</param>
    public DefaultRemoteService(string mainUrl, string? fallbackUrl = null)
    {
        _mainUrl = mainUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(mainUrl));
        _fallbackUrl = fallbackUrl?.TrimEnd('/');
    }

    public IReadOnlyList<string> GetRemoteUrls(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return Array.Empty<string>();
        var list = new List<string>(2) { $"{_mainUrl}/{fileName}" };
        if (_fallbackUrl != null) list.Add($"{_fallbackUrl}/{fileName}");
        return list;
    }
}

/// <summary>
/// The HTTP fetch primitive. XEngine ships HttpClientDownloadBackend; tests inject a fake that
/// yields canned bytes. The backend reports progress and honors cancellation.
/// </summary>
public interface IDownloadBackend
{
    /// <summary>Download <paramref name="url"/> to <paramref name="localFilePath"/> (atomic temp-file
    /// + rename). Reports byte progress via <paramref name="progress"/>. Throws on HTTP/IO failure.</summary>
    Task DownloadAsync(string url, string localFilePath, IProgress<DownloadProgress>? progress, CancellationToken cancellation);
}

/// <summary>Progress report from a single file download.</summary>
public readonly struct DownloadProgress
{
    public readonly long BytesReceived;
    public readonly long TotalBytes; // -1 if unknown (no Content-Length)
    public DownloadProgress(long bytesReceived, long totalBytes) { BytesReceived = bytesReceived; TotalBytes = totalBytes; }
}
