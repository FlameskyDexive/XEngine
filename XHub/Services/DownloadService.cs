using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Platform;

namespace XHub.Services;

public enum OperationState
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Canceled,
    Installing,
    Done,
}

public sealed class OperationProgress
{
    public string Id { get; init; } = "";
    public OperationState State { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public double BytesPerSecond { get; init; }
    public string Error { get; init; } = "";
}

/// <summary>
/// Download queue (§3.5): max two parallel editors packages, per-op pause/resume/cancel/retry,
/// streaming to a partial file with HTTP Range resume, ETag/Last-Modified change detection,
/// SHA-256 verification after completion, and persistence in <c>operations.json</c> so
/// unfinished work survives restarts. Memory stays flat — the stream never buffers the file.
/// </summary>
public sealed class DownloadService : IDisposable
{
    private const int MaxParallel = 2;

    private readonly JsonStore<OperationsData> _store;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, DownloadOperation> _operations = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();
    private readonly SemaphoreSlim _slots = new(MaxParallel, MaxParallel);
    private readonly object _persistGate = new();

    public DownloadService() : this(AppPaths.OperationsFile, AppPaths.DownloadCache) { }

    public DownloadService(string operationsFile, string downloadCache)
    {
        _store = new JsonStore<OperationsData>(operationsFile);
        DownloadCache = downloadCache;
        Directory.CreateDirectory(DownloadCache);
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = Timeout.InfiniteTimeSpan, // handled per-read with cancellation
        };
        LoadUnfinished();
    }

    public string DownloadCache { get; }

    public IReadOnlyCollection<DownloadOperation> Operations
    {
        get
        {
            var snapshot = new List<DownloadOperation>(_operations.Values);
            snapshot.Sort((a, b) => b.UpdatedUtc.CompareTo(a.UpdatedUtc));
            return snapshot;
        }
    }

    public event Action<OperationProgress>? Progress;

    /// <summary>Resumes unfinished operations from operations.json (§5 未完成操作恢复).</summary>
    private void LoadUnfinished()
    {
        foreach (DownloadOperation op in _store.Load().Operations)
        {
            if (op.State is "downloading" or "installing")
                op.State = "queued"; // process died mid-flight → requeue
            _operations[op.Id] = op;
        }
    }

    /// <summary>Enqueues a download. Re-enqueueing the same version/url resumes the existing op.</summary>
    public DownloadOperation Enqueue(string url, long size, string sha256, string canonicalVersion, string displayVersion)
    {
        DownloadOperation? existing = null;
        foreach (DownloadOperation candidate in _operations.Values)
        {
            if (candidate.CanonicalVersion == canonicalVersion && candidate.Url == url)
            {
                existing = candidate;
                break;
            }
        }
        if (existing != null && existing.State is "completed" or "downloading" or "queued" or "paused" or "installing")
            return existing;

        string? tempPath = existing?.TempPath;
        if (string.IsNullOrEmpty(tempPath))
            tempPath = Path.Combine(DownloadCache, $"{Guid.NewGuid():N}.part");

        var op = new DownloadOperation
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            Kind = "download",
            State = "queued",
            CanonicalVersion = canonicalVersion,
            DisplayVersion = displayVersion,
            Rid = ReleaseFeedService.CurrentRid,
            Url = url,
            BytesTotal = size,
            ExpectedSha256 = sha256,
            TempPath = tempPath,
            UpdatedUtc = DateTime.UtcNow,
        };
        _operations[op.Id] = op;
        Persist();
        _ = RunAsync(op);
        return op;
    }

    public void Pause(string id)
    {
        if (_operations.TryGetValue(id, out DownloadOperation? op) && op.State == "downloading")
        {
            op.State = "paused";
            _cancellations[id]?.Cancel();
            Persist();
            Emit(op);
        }
    }

    public void Resume(string id)
    {
        if (_operations.TryGetValue(id, out DownloadOperation? op) && op.State == "paused")
        {
            op.State = "queued";
            Persist();
            Emit(op);
            _ = RunAsync(op);
        }
    }

    public void Cancel(string id)
    {
        if (!_operations.TryGetValue(id, out DownloadOperation? op)) return;
        op.State = "canceled";
        _cancellations[id]?.Cancel();
        TryDeletePartial(op);
        Persist();
        Emit(op);
    }

    /// <summary>Retry a failed download from where it stopped (Range resume).</summary>
    public void Retry(string id)
    {
        if (!_operations.TryGetValue(id, out DownloadOperation? op) || op.State != "failed") return;
        op.State = "queued";
        op.Error = "";
        Persist();
        Emit(op);
        _ = RunAsync(op);
    }

    /// <summary>Called by the installer after a successful commit: terminal state + persisted.</summary>
    public void MarkInstalled(string id)
    {
        if (!_operations.TryGetValue(id, out DownloadOperation? op)) return;
        op.State = "done";
        op.UpdatedUtc = DateTime.UtcNow;
        Persist();
        Emit(op);
    }

    public void Remove(string id)
    {
        if (!_operations.TryGetValue(id, out DownloadOperation? op)) return;
        _cancellations[id]?.Cancel();
        if (op.State is "queued" or "paused" or "failed" or "canceled" or "done")
        {
            TryDeletePartial(op);
            _operations.TryRemove(id, out _);
            Persist();
        }
    }

    private async Task RunAsync(DownloadOperation op)
    {
        await _slots.WaitAsync();
        try
        {
            if (op.State != "queued") return;
            await DownloadAsync(op);
        }
        catch (OperationCanceledException)
        {
            // pause/cancel — state already set by the caller
        }
        catch (Exception ex)
        {
            op.State = "failed";
            op.Error = ex.Message;
            op.UpdatedUtc = DateTime.UtcNow;
            Persist();
            Emit(op);
            HubLog.Warning($"Download {op.DisplayVersion} failed: {ex.Message}");
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task DownloadAsync(DownloadOperation op)
    {
        op.State = "downloading";
        op.UpdatedUtc = DateTime.UtcNow;
        Persist();
        Emit(op);

        var cts = new CancellationTokenSource();
        _cancellations[op.Id] = cts;

        using var request = new HttpRequestMessage(HttpMethod.Get, op.Url);
        long resumeFrom = File.Exists(op.TempPath) ? new FileInfo(op.TempPath).Length : 0;
        if (resumeFrom > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);

        using HttpResponseMessage response = await _http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cts.Token);

        // 200 = server ignored Range (or fresh start); 206 = partial content (resume).
        if (resumeFrom > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            TryDeletePartial(op);
            op.BytesDone = 0;
            resumeFrom = 0;
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");

        // Change detection (§5): a different etag invalidates the partial data; the final
        // SHA-256 gate is the hard guarantee against any corruption slipping through.
        string etag = response.Headers.ETag?.Tag ?? "";
        string lastModified = response.Content.Headers.LastModified?.ToString("R") ?? "";
        if (resumeFrom > 0 && op.ETag.Length > 0 && etag.Length > 0 && etag != op.ETag)
        {
            TryDeletePartial(op);
            op.BytesDone = 0;
        }
        op.ETag = etag;
        op.LastModified = lastModified;
        if (op.BytesTotal == 0)
            op.BytesTotal = response.Content.Headers.ContentLength ?? 0;

        await using Stream source = await response.Content.ReadAsStreamAsync(cts.Token);
        string actual;
        // The write stream holds FileShare.None, so it is disposed before the hash pass reads.
        await using (var target = new FileStream(op.TempPath,
            resumeFrom > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true))
        {
            var buffer = new byte[1024 * 64];
            var sw = Stopwatch.StartNew();
            long lastEmitBytes = 0;
            var lastEmit = TimeSpan.Zero;
            while (true)
            {
                int read = await source.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                op.BytesDone += read;

                if (sw.Elapsed - lastEmit > TimeSpan.FromMilliseconds(200))
                {
                    double rate = sw.Elapsed.TotalSeconds > 0.2
                        ? (op.BytesDone - lastEmitBytes) / (sw.Elapsed - lastEmit).TotalSeconds
                        : 0;
                    Emit(op, rate);
                    lastEmit = sw.Elapsed;
                    lastEmitBytes = op.BytesDone;
                }
                if (op.State == "paused" || op.State == "canceled")
                    throw new OperationCanceledException();
            }
        }

        // Completed download → verify.
        op.UpdatedUtc = DateTime.UtcNow;
        actual = await ComputeSha256Async(op.TempPath, cts.Token);
        if (op.ExpectedSha256.Length > 0 &&
            !string.Equals(actual, op.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeletePartial(op); // hash failure: refuse to keep the corrupt payload
            op.State = "failed";
            op.Error = "download.error.hashMismatch";
            Persist();
            Emit(op);
            HubLog.Warning($"Hash mismatch for {op.DisplayVersion}: {actual} != {op.ExpectedSha256}");
            return;
        }
        op.State = "completed";
        Persist();
        Emit(op);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void TryDeletePartial(DownloadOperation op)
    {
        try { if (File.Exists(op.TempPath)) File.Delete(op.TempPath); }
        catch (Exception ex) { HubLog.Warning($"Partial delete failed: {ex.Message}"); }
    }

    private void Emit(DownloadOperation op, double rate = 0) =>
        Progress?.Invoke(new OperationProgress
        {
            Id = op.Id,
            State = MapState(op.State),
            BytesDone = op.BytesDone,
            BytesTotal = op.BytesTotal,
            BytesPerSecond = rate,
            Error = op.Error,
        });

    private static OperationState MapState(string state) => state switch
    {
        "downloading" => OperationState.Downloading,
        "paused" => OperationState.Paused,
        "completed" => OperationState.Completed,
        "failed" => OperationState.Failed,
        "canceled" => OperationState.Canceled,
        "installing" => OperationState.Installing,
        "done" => OperationState.Done,
        _ => OperationState.Queued,
    };

    private void Persist()
    {
        lock (_persistGate)
        {
            try
            {
                var data = new OperationsData { Operations = new List<DownloadOperation>(_operations.Values) };
                _store.Save(data);
            }
            catch (Exception ex)
            {
                HubLog.Warning($"operations.json write failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        foreach (CancellationTokenSource cts in _cancellations.Values)
            cts.Dispose();
    }
}
