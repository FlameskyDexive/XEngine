// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// HttpClientDownloadBackend - the default IDownloadBackend for XEngine. Uses System.Net.Http.HttpClient
// (.NET built-in, cross-platform, no new dependency) to stream a URL to a temp file and atomically
// rename. Supports resume via Range headers when the server permits it.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// IDownloadBackend backed by HttpClient. One static shared client per process (recommended by the
/// HttpClient docs - reusing sockets avoids port exhaustion). Streams to a temp file and renames on
/// success so a partial download never overwrites a good cached file.
/// </summary>
public sealed class HttpClientDownloadBackend : IDownloadBackend
{
    private static readonly HttpClient s_client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.None, // bundles are already LZ4-compressed; don't double-decompress
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // per-request cancellation drives timeouts instead
    };

    /// <summary>Per-request timeout (default 30s). Reset on each byte received.</summary>
    public TimeSpan PerRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Buffer size for streaming to disk.</summary>
    public int BufferSize { get; set; } = 81920;

    public async Task DownloadAsync(string url, string localFilePath, IProgress<DownloadProgress>? progress, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(url)) throw new ArgumentNullException(nameof(url));
        if (string.IsNullOrEmpty(localFilePath)) throw new ArgumentNullException(nameof(localFilePath));

        string? dir = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tempPath = localFilePath + ".part";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(PerRequestTimeout);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // Resume support: if a .part exists, ask the server for the remainder.
        long resumeFrom = 0;
        if (File.Exists(tempPath)) resumeFrom = new FileInfo(tempPath).Length;
        if (resumeFrom > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);

        using var response = await s_client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Server says we already have it all; the .part IS the complete file.
            File.Move(tempPath, localFilePath, overwrite: true);
            return;
        }
        // If the server ignored our Range header (returned 200 instead of 206), restart from scratch.
        bool appending = response.StatusCode == HttpStatusCode.PartialContent && resumeFrom > 0;
        if (!appending)
        {
            resumeFrom = 0;
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? -1;
        if (appending && total >= 0) total += resumeFrom;

        await using var content = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        // The write handle is scoped to its own block so it is disposed BEFORE the atomic rename —
        // File.Move on Windows throws "being used by another process" while a FileShare.None handle
        // is open on the source.
        {
            await using var fs = new FileStream(tempPath, FileMode.Append, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            var buffer = new byte[BufferSize];
            long received = resumeFrom;
            int read;
            long lastReportTicks = 0;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, BufferSize), cts.Token).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                received += read;

                // Throttle progress reports to ~one per 100ms to avoid flooding the scheduler.
                long now = Environment.TickCount64;
                if (progress != null && now - lastReportTicks >= 100)
                {
                    progress.Report(new DownloadProgress(received, total));
                    lastReportTicks = now;
                }

                cts.CancelAfter(PerRequestTimeout); // reset the idle timeout on each read
            }

            progress?.Report(new DownloadProgress(received, total));
            await fs.FlushAsync(cts.Token).ConfigureAwait(false);
        }

        // Atomic rename: only visible as "done" once complete.
        File.Move(tempPath, localFilePath, overwrite: true);
    }
}
