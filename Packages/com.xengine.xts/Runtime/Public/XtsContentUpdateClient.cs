using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.XTS;

/// <summary>
/// Hot-update content downloader: fetches a signed content directory over
/// HTTP (manifest.json first, then every pinned file) into a staging
/// directory and hands it to <see cref="XtsUpdateService.ActivateCandidate"/>,
/// which verifies the signature and every file hash before the atomic
/// promotion. The native player library and the AOT binary are never touched
/// by content (plan §1) — only the files the manifest pins.
/// </summary>
public sealed class XtsContentUpdateClient
{
    private readonly XtsUpdateService _updateService;
    private readonly Func<string, Task<byte[]>> _fetch;

    public XtsContentUpdateClient(XtsUpdateService updateService)
        : this(updateService, DefaultFetchAsync)
    {
    }

    /// <summary>Test/transport seam: the fetcher downloads a URL's bytes.</summary>
    public XtsContentUpdateClient(XtsUpdateService updateService, Func<string, Task<byte[]>> fetcher)
    {
        _updateService = updateService;
        _fetch = fetcher;
    }

    /// <summary>
    /// Download the content published at <paramref name="contentUrl"/> (must
    /// serve manifest.json + pinned files) and activate it. Returns the
    /// activation problems; empty = installed. The staging directory is
    /// cleaned on failure.
    /// </summary>
    public async Task<List<string>> InstallFromAsync(string contentUrl, string expectedVersion, CancellationToken cancellation = default)
    {
        var errors = new List<string>();
        var staging = Path.Combine(Path.GetTempPath(), "xts-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);

            var manifestBytes = await FetchWithHashCheckAsync(
                JoinUrl(contentUrl, XtsContentManifest.ManifestFileName), null, cancellation).ConfigureAwait(false);
            var manifest = XtsContentManifest.Parse(Encoding.UTF8.GetString(manifestBytes));

            if (manifest.Schema != XtsContentManifest.CurrentSchema)
                errors.Add($"content manifest schema '{manifest.Schema}' is not {XtsContentManifest.CurrentSchema}");
            if (!string.IsNullOrEmpty(expectedVersion) && manifest.Version != expectedVersion)
                errors.Add($"content manifest version '{manifest.Version}' != expected '{expectedVersion}'");
            if (errors.Count > 0)
                return errors;

            // verify each file's hash AS TRANSPORTED (download integrity) and
            // stage it; the update service re-verifies from disk after staging
            foreach (var file in manifest.Files)
            {
                var url = JoinUrl(contentUrl, file.Path);
                byte[] bytes;
                try
                {
                    bytes = await FetchWithHashCheckAsync(url, file.Sha256, cancellation).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    errors.Add($"download failed for '{file.Path}': {e.Message}");
                    continue;
                }
                var target = Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }
            if (errors.Count > 0)
                return errors;

            File.WriteAllBytes(Path.Combine(staging, XtsContentManifest.ManifestFileName), manifestBytes);
            errors = _updateService.ActivateCandidate(staging, expectedVersion);
            return errors;
        }
        catch (Exception e)
        {
            errors.Insert(0, $"content download failed: {e.Message}");
            return errors;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }

    private async Task<byte[]> FetchWithHashCheckAsync(string url, string? sha256, CancellationToken cancellation)
    {
        var bytes = await _fetch(url).ConfigureAwait(false);
        if (sha256 != null)
        {
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"transport hash mismatch for {url} (expected {sha256}, got {actual})");
        }
        return bytes;
    }

    internal static string JoinUrl(string base64Url, string relative)
    {
        return base64Url.TrimEnd('/') + "/" + relative;
    }

    private static readonly Func<string, Task<byte[]>> DefaultFetchAsync = async url =>
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        return await http.GetByteArrayAsync(url).ConfigureAwait(false);
    };
}
