// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.Android;

internal enum AndroidToolchainImportSessionState
{
    Idle,
    Running,
    Succeeded,
    Cancelled,
    Failed,
}

internal readonly record struct AndroidToolchainImportSessionSnapshot(
    AndroidToolchainImportSessionState State,
    string Stage,
    string RelativePath,
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    string Error,
    AndroidToolchainImportResult? Result);

internal sealed class AndroidToolchainImportSession : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private AndroidToolchainImportSessionSnapshot _snapshot = new(
        AndroidToolchainImportSessionState.Idle,
        string.Empty,
        string.Empty,
        0,
        0,
        0,
        0,
        string.Empty,
        null);

    internal AndroidToolchainImportSessionSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return _snapshot;
        }
    }

    internal Task Completion
    {
        get
        {
            lock (_sync)
                return _task ?? Task.CompletedTask;
        }
    }

    internal bool Start(AndroidToolchainImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_sync)
        {
            if (_snapshot.State == AndroidToolchainImportSessionState.Running)
                return false;

            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _snapshot = new AndroidToolchainImportSessionSnapshot(
                AndroidToolchainImportSessionState.Running,
                "Scanning",
                string.Empty,
                0,
                0,
                0,
                0,
                string.Empty,
                null);
            CancellationToken cancellationToken = _cancellation.Token;
            _task = Task.Run(() => RunAsync(options, cancellationToken), CancellationToken.None);
            return true;
        }
    }

    internal void Cancel()
    {
        lock (_sync)
            _cancellation?.Cancel();
    }

    public void Dispose()
    {
        Cancel();
        lock (_sync)
        {
            if (_task?.IsCompleted != false)
                _cancellation?.Dispose();
        }
    }

    private async Task RunAsync(AndroidToolchainImportOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var progress = new InlineProgress<AndroidToolchainImportProgress>(UpdateProgress);
            AndroidToolchainImportResult result = await new AndroidToolchainBootstrapper()
                .ImportAsync(options, progress, cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
            {
                _snapshot = _snapshot with
                {
                    State = AndroidToolchainImportSessionState.Succeeded,
                    Stage = result.Reused ? "Verified existing cache" : "Import complete",
                    RelativePath = result.CachePath,
                    Error = string.Empty,
                    Result = result,
                };
            }
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
            {
                _snapshot = _snapshot with
                {
                    State = AndroidToolchainImportSessionState.Cancelled,
                    Stage = "Cancelled",
                    RelativePath = string.Empty,
                    Error = string.Empty,
                };
            }
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _snapshot = _snapshot with
                {
                    State = AndroidToolchainImportSessionState.Failed,
                    Stage = "Failed",
                    RelativePath = string.Empty,
                    Error = exception.Message,
                };
            }
        }
    }

    private void UpdateProgress(AndroidToolchainImportProgress progress)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                Stage = progress.Stage,
                RelativePath = progress.RelativePath,
                CompletedFiles = progress.CompletedFiles,
                TotalFiles = progress.TotalFiles,
                CompletedBytes = progress.CompletedBytes,
                TotalBytes = progress.TotalBytes,
            };
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
