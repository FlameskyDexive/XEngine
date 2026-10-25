// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// ResourceDownloader - drives a queue of bundle downloads with bounded concurrency, retry, and
// progress events. Mirrors YooAsset's ResourceDownloaderOperation. The downloader writes each
// completed bundle to a SandboxBundleStore (verifying CRC/hash before promoting temp -> final);
// failed downloads accumulate in a failed list and the operation fails with the first error.
//
// The download itself is delegated to an IDownloadBackend (HttpClient by default), so the queue/
// event/verify logic here is transport-agnostic and testable with a fake backend.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Runtime.AssetBundles;

/// <summary>One item in a download queue.</summary>
public sealed class DownloadItem
{
    public BundleInfo Bundle;
    public string FileName; // on-disk name relative to the store root
}

/// <summary>Event args for download-file-started.</summary>
public sealed class DownloadFileStartedEventArgs : EventArgs
{
    public string FileName;
    public long FileSize;
    public DownloadFileStartedEventArgs(string fileName, long fileSize) { FileName = fileName; FileSize = fileSize; }
}

/// <summary>Event args for download-progress-changed.</summary>
public sealed class DownloadProgressChangedEventArgs : EventArgs
{
    public long TotalDownloadedBytes;
    public long TotalBytesToDownload;
    public int DownloadedCount;
    public int TotalCount;
    public float Progress => TotalBytesToDownload > 0 ? TotalDownloadedBytes / (float)TotalBytesToDownload : 0f;
    public DownloadProgressChangedEventArgs(long downloaded, long total, int doneCount, int totalCount)
    { TotalDownloadedBytes = downloaded; TotalBytesToDownload = total; DownloadedCount = doneCount; TotalCount = totalCount; }
}

/// <summary>Event args for a failed download.</summary>
public sealed class DownloadErrorEventArgs : EventArgs
{
    public string FileName;
    public string Error;
    public DownloadErrorEventArgs(string fileName, string error) { FileName = fileName; Error = error; }
}

/// <summary>Result a download task enqueues back to the main-thread drain loop.</summary>
internal readonly struct DownloadResult
{
    public readonly DownloadItem Item;
    public readonly bool Succeeded;
    public readonly string? Error;
    public DownloadResult(DownloadItem item, bool succeeded, string? error) { Item = item; Succeeded = succeeded; Error = error; }
}

/// <summary>
/// A bounded-concurrency bundle downloader. Add items via the constructor, StartDownload to enqueue,
/// then pump AsyncOperationSystem (XAssets.Tick) to advance. Emits progress/start/error/completed
/// events. Pausing stops launching new downloads (in-flight ones finish); cancel aborts them.
/// </summary>
public sealed class ResourceDownloader : AsyncOperationBase
{
    private const int MaxConcurrency = 32;

    private readonly IDownloadBackend _backend;
    private readonly IRemoteService _remote;
    private readonly SandboxBundleStore _store;
    private readonly List<DownloadItem> _pending = new();
    private readonly List<DownloadItem> _completed = new();
    private readonly List<(DownloadItem item, string error)> _failed = new();
    private readonly int _maxConcurrency;

    // Download tasks report completion here (thread-safe); InternalUpdate drains it on the main
    // thread and updates _completed/_failed/_activeCount. This avoids cross-thread mutation of the
    // plain List<> fields above.
    private readonly System.Collections.Concurrent.ConcurrentQueue<DownloadResult> _results = new();
    private int _activeCount; // number of in-flight tasks (Interlocked-mutated by both threads)
    private bool _paused;
    private CancellationTokenSource? _cts;

    /// <summary>Total bytes across all items (the progress denominator).</summary>
    public long TotalDownloadBytes => TotalDownloadBytesField;

    public IReadOnlyList<DownloadItem> Pending => _pending;
    public IReadOnlyList<DownloadItem> Completed => _completed;
    public IReadOnlyList<(DownloadItem item, string error)> Failed => _failed;
    public int FailedCount => _failed.Count;

    public event EventHandler<DownloadFileStartedEventArgs>? DownloadFileStarted;
    public event EventHandler<DownloadProgressChangedEventArgs>? DownloadProgressChanged;
    public event EventHandler<DownloadErrorEventArgs>? DownloadError;
    public event EventHandler? DownloadCompleted;

    public ResourceDownloader(IDownloadBackend backend, IRemoteService remote, SandboxBundleStore store, IEnumerable<DownloadItem> items, int maxConcurrency = 8)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _maxConcurrency = Math.Clamp(maxConcurrency, 1, MaxConcurrency);
        foreach (var item in items)
        {
            _pending.Add(item);
            TotalDownloadBytesField += item.Bundle.FileSize;
        }
    }

    /// <summary>Merge another downloader's pending items into this one (dedup by file name).</summary>
    public void Combine(ResourceDownloader other)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in _pending) seen.Add(i.FileName);
        foreach (var item in other._pending)
        {
            if (seen.Add(item.FileName))
            {
                _pending.Add(item);
                TotalDownloadBytesField += item.Bundle.FileSize;
            }
        }
    }
    // Backing field for TotalDownloadBytes (init-only by construction; Combine is the only mutator).
    private long TotalDownloadBytesField;

    public void PauseDownload() => _paused = true;
    public void ResumeDownload() => _paused = false;

    public void CancelDownload()
    {
        _cts?.Cancel();
        SetError("Download canceled.");
    }

    protected override void InternalStart()
    {
        _cts = new CancellationTokenSource();
        if (_pending.Count == 0)
        {
            SetResult();
            return;
        }
    }

    protected override void InternalUpdate()
    {
        if (IsDone) return;

        // Drain completed-task results on the main thread (thread-safe queue).
        while (_results.TryDequeue(out var r))
        {
            if (r.Succeeded)
            {
                _completed.Add(r.Item);
            }
            else
            {
                _failed.Add((r.Item, r.Error ?? "unknown download error"));
                DownloadError?.Invoke(this, new DownloadErrorEventArgs(r.Item.FileName, r.Error ?? "unknown download error"));
            }
            System.Threading.Interlocked.Decrement(ref _activeCount);
        }

        // Launch new downloads up to the concurrency limit (unless paused).
        while (!_paused && _activeCount < _maxConcurrency && _pending.Count > 0 && _cts?.IsCancellationRequested == false)
        {
            var item = _pending[0];
            _pending.RemoveAt(0);
            System.Threading.Interlocked.Increment(ref _activeCount);
            _ = DownloadOneAsync(item);
        }

        // Update aggregate progress.
        DownloadProgressChanged?.Invoke(this, new DownloadProgressChangedEventArgs(DownloadedBytes, TotalDownloadBytes, _completed.Count, _completed.Count + _pending.Count + _activeCount));
        Progress = TotalDownloadBytes > 0 ? DownloadedBytes / (float)TotalDownloadBytes : 0f;

        // Done when nothing is pending or in flight.
        if (_pending.Count == 0 && _activeCount == 0)
        {
            if (_failed.Count > 0)
                SetError($"Failed to download {_failed.Count} bundle(s); first: {_failed[0].error}");
            else
                SetResult();
        }
    }

    private async Task DownloadOneAsync(DownloadItem item)
    {
        var urls = _remote.GetRemoteUrls(item.FileName);
        Exception? lastError = null;

        foreach (var url in urls)
        {
            try
            {
                DownloadFileStarted?.Invoke(this, new DownloadFileStartedEventArgs(item.FileName, item.Bundle.FileSize));
                var finalPath = _store.GetCacheFilePath(item.Bundle.BundleGuid) ?? Path.Combine(_store.RootPath, item.FileName);
                long reportedBefore = 0;
                await _backend.DownloadAsync(url, finalPath, new Progress<DownloadProgress>(p =>
                {
                    long delta = p.BytesReceived - reportedBefore;
                    reportedBefore = p.BytesReceived;
                    if (delta > 0) System.Threading.Interlocked.Add(ref UnsafeDownloadedBytesAddend, delta);
                }), _cts!.Token).ConfigureAwait(false);

                // Verify + promote into the store (CRC/hash check, temp->final). If verification fails
                // the store throws and we treat it as a download error to retry from the next URL.
                await _store.VerifyAndPromoteAsync(item.Bundle, finalPath, _cts.Token).ConfigureAwait(false);

                _results.Enqueue(new DownloadResult(item, succeeded: true, error: null));
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastError = ex; /* try next URL */ }
        }

        // All URLs failed.
        _results.Enqueue(new DownloadResult(item, succeeded: false, error: lastError?.Message ?? "unknown download error"));
    }

    // Backing field for the cross-thread bytes counter; DownloadedBytes reads it via Interlocked.
    private long UnsafeDownloadedBytesAddend;
    /// <summary>Running total of bytes received so far (thread-safe).</summary>
    public long DownloadedBytes { get => System.Threading.Interlocked.Read(ref UnsafeDownloadedBytesAddend); }

    protected override void InternalDispose()
    {
        DownloadCompleted?.Invoke(this, EventArgs.Empty);
        _cts?.Dispose();
        _cts = null;
    }
}
