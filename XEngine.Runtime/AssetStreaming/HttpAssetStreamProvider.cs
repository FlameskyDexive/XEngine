// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Async;

namespace XEngine.Runtime.AssetStreaming;

/// <summary>
/// Browser (and CDN) asset source. <see cref="HttpClient"/> is fetch-backed on <c>browser-wasm</c>.
/// <see cref="OpenRead"/> always throws — HTTP must not block the wasm main / rAF thread.
/// WeChat in-package files use <see cref="MiniGamePackageFileProvider"/>; subpackages and CDN may
/// still use this provider (never for runtime wasm).
/// </summary>
public sealed class HttpAssetStreamProvider : IAssetStreamProvider
{
    private readonly HttpClient _http;
    private readonly string _urlPrefix;
    private readonly string[] _catalog;
    private readonly bool _ownsClient;
    private bool _disposed;

    public HttpAssetStreamProvider(
        HttpClient httpClient,
        string urlPrefix = "",
        IReadOnlyList<string>? catalog = null,
        bool ownsClient = false)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _urlPrefix = NormalizePrefix(urlPrefix);
        _ownsClient = ownsClient;
        if (catalog == null || catalog.Count == 0)
        {
            _catalog = [];
        }
        else
        {
            _catalog = new string[catalog.Count];
            for (int i = 0; i < catalog.Count; i++)
                _catalog[i] = AssetStreamPath.Normalize(catalog[i]);
            Array.Sort(_catalog, StringComparer.Ordinal);
        }
    }

    public string Description => _http.BaseAddress is { } origin
        ? origin.AbsoluteUri + _urlPrefix
        : "http:" + _urlPrefix;

    public Stream? OpenRead(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = AssetStreamPath.Normalize(path);
        throw new InvalidOperationException(
            "HTTP asset provider cannot open streams synchronously. Use OpenReadAsync (fetch).");
    }

    public async XTask<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        string relative = CombineUrl(_urlPrefix, AssetStreamPath.Normalize(path));
        using var request = new HttpRequestMessage(HttpMethod.Get, relative);
        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new MemoryStream(bytes, writable: false);
    }

    public IReadOnlyList<string> GetFiles(string directory, string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalizedDirectory = AssetStreamPath.Normalize(directory, allowEmpty: true);
        string normalizedExtension = AssetStreamPath.NormalizeExtension(extension);
        string prefix = normalizedDirectory.Length == 0 ? string.Empty : normalizedDirectory + "/";
        var result = new List<string>();
        for (int i = 0; i < _catalog.Length; i++)
        {
            string path = _catalog[i];
            if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
                !path.EndsWith(normalizedExtension, StringComparison.Ordinal))
            {
                continue;
            }
            string remainder = path[prefix.Length..];
            if (!remainder.Contains('/'))
                result.Add(path);
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsClient)
            _http.Dispose();
    }

    private static string NormalizePrefix(string urlPrefix)
    {
        if (string.IsNullOrEmpty(urlPrefix))
            return string.Empty;
        string trimmed = urlPrefix.Replace('\\', '/').Trim('/');
        return trimmed.Length == 0 ? string.Empty : trimmed + "/";
    }

    private static string CombineUrl(string prefix, string path) => prefix.Length == 0 ? path : prefix + path;
}
