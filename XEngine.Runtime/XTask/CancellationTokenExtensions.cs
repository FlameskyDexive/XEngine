// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

namespace XEngine.Async;

/// <summary>Extension methods on <see cref="CancellationToken"/> and <see cref="CancellationTokenSource"/>
/// for XTask integration and allocation-conscious registration.</summary>
public static class CancellationTokenExtensions
{
    /// <summary>
    /// Register a callback on the token without capturing the current <see cref="ExecutionContext"/>.
    /// Capturing (the default) allocates and flows async-local state into the callback, which is
    /// unnecessary on the engine's frame-loop paths and costs per-registration allocation.
    /// </summary>
    public static CancellationTokenRegistration RegisterWithoutCaptureExecutionContext(
        this CancellationToken cancellationToken, Action<object?> callback, object? state)
    {
        bool restoreFlow = false;
        try
        {
            if (!ExecutionContext.IsFlowSuppressed())
            {
                ExecutionContext.SuppressFlow();
                restoreFlow = true;
            }

            return cancellationToken.Register(callback, state);
        }
        finally
        {
            if (restoreFlow)
            {
                ExecutionContext.RestoreFlow();
            }
        }
    }

    /// <summary>True if the token is null/default (no source) and therefore cannot be canceled.</summary>
    public static bool IsNull(this CancellationToken cancellationToken) => !cancellationToken.CanBeCanceled;

    /// <summary>An <see cref="XTask"/> that completes when the token is canceled. If the token is already
    /// canceled, the returned task is already completed.</summary>
    public static XTask WaitUntilCanceled(this CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return XTask.Never; // a non-cancellable token never cancels.
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return XTask.CompletedTask;
        }

        var promise = new CancellationTokenPromise(cancellationToken);
        return promise.Task;
    }
}

internal sealed class CancellationTokenPromise : IXTaskSource
{
    private readonly CancellationTokenRegistration _registration;
    private readonly AutoResetXTaskCompletionSource _tcs;

    public CancellationTokenPromise(CancellationToken cancellationToken)
    {
        _tcs = AutoResetXTaskCompletionSource.Create();
        _registration = cancellationToken.RegisterWithoutCaptureExecutionContext(
            static state => ((CancellationTokenPromise)state!)._tcs.TrySetResult(), this);
    }

    public XTask Task => _tcs.Task;

    public XTaskStatus GetStatus(short token) => _tcs.GetStatus(token);
    public XTaskStatus UnsafeGetStatus() => _tcs.UnsafeGetStatus();
    public void OnCompleted(Action<object> continuation, object state, short token)
        => _tcs.OnCompleted(continuation, state, token);

    public void GetResult(short token)
    {
        try
        {
            _tcs.GetResult(token);
        }
        finally
        {
            _registration.Dispose();
        }
    }
}
