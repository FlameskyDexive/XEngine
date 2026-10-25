// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.Android;

internal enum AndroidToolchainResolveSessionState
{
    Idle,
    Running,
    Succeeded,
    Cancelled,
    Failed,
}

internal readonly record struct AndroidToolchainResolveSessionSnapshot(
    AndroidToolchainResolveSessionState State,
    string Stage,
    string Detail,
    int Completed,
    int Total,
    string Error,
    AndroidToolchainSnapshot? Result);

internal sealed class AndroidToolchainResolveSession : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private AndroidToolchainResolveSessionSnapshot _snapshot = new(
        AndroidToolchainResolveSessionState.Idle,
        string.Empty,
        string.Empty,
        0,
        0,
        string.Empty,
        null);

    internal AndroidToolchainResolveSessionSnapshot Snapshot
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

    internal bool Start(AndroidToolchainResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_sync)
        {
            if (_snapshot.State == AndroidToolchainResolveSessionState.Running)
                return false;
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _snapshot = new AndroidToolchainResolveSessionSnapshot(
                AndroidToolchainResolveSessionState.Running,
                "Resolve",
                "Starting",
                0,
                1,
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

    private async Task RunAsync(AndroidToolchainResolveOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var progress = new InlineProgress<AndroidToolchainResolveProgress>(UpdateProgress);
            AndroidToolchainSnapshot result = await new AndroidToolchainResolver()
                .ResolveAsync(options, progress, cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
            {
                _snapshot = _snapshot with
                {
                    State = AndroidToolchainResolveSessionState.Succeeded,
                    Stage = "Verified",
                    Detail = result.Fingerprint,
                    Completed = _snapshot.Total,
                    Error = string.Empty,
                    Result = result,
                };
            }
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
                _snapshot = _snapshot with { State = AndroidToolchainResolveSessionState.Cancelled, Stage = "Cancelled", Detail = string.Empty };
        }
        catch (Exception exception)
        {
            lock (_sync)
                _snapshot = _snapshot with { State = AndroidToolchainResolveSessionState.Failed, Stage = "Failed", Error = exception.Message };
        }
    }

    private void UpdateProgress(AndroidToolchainResolveProgress value)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                Stage = value.Stage,
                Detail = value.Detail,
                Completed = value.Completed,
                Total = value.Total,
            };
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
