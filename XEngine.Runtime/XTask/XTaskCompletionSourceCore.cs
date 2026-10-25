// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using XEngine.Async.Internal;

namespace XEngine.Async;

/// <summary>
/// Shared static state for <see cref="XTaskCompletionSourceCore{TResult}"/>: the continuation
/// sentinel used to resolve the <see cref="XTaskCompletionSourceCore{TResult}.OnCompleted"/> vs
/// <see cref="XTaskCompletionSourceCore{TResult}.TrySetResult"/> race. The sentinel is never
/// invoked; it is only ever compared by reference.
/// </summary>
internal static class XTaskCompletionSourceCoreShared
{
    internal static readonly Action<object> s_sentinel = SentinelInvoke;

    private static void SentinelInvoke(object _)
        => throw Error.InvalidOperation(Error.InvokeSentinel);
}

/// <summary>
/// The shared completion-core logic embedded by value in every single-use XTask source. A struct so
/// it adds no allocation to its host. Implements a single-continuation contract with a version
/// token, a <c>completedCount</c> latch, and deferred auto-reset (the host resets it after
/// <see cref="GetResult"/> so the same instance can be handed back to a new awaiter).
///
/// Status is derived on demand from <c>(continuation, completedCount, error)</c> rather than stored,
/// which is one less field to reset and one less race window.
/// </summary>
public struct XTaskCompletionSourceCore<TResult>
{
    private TResult result; // unconstrained; for value types holds the value directly (no Nullable<>)
    private object? error; // ExceptionHolder | OperationCanceledException | null
    private short version; // bumped on Reset; stale awaiters see a version mismatch
    private bool hasUnhandledError;
    private int completedCount; // 0 = pending; Interlocked.Increment to 1 == winner
    private Action<object>? continuation;
    private object? continuationState;

    /// <summary>Current version token; captured into XTask at construction and validated per call.</summary>
    public short Version => version;

    /// <summary>
    /// True while a pending awaiter's continuation or state object belongs to a collectible
    /// (hot-reloadable) assembly. Such a source must be dropped from the frame loop when the
    /// script AssemblyLoadContext unloads or it pins the whole context. Delegate detection goes
    /// through <see cref="Internal.CollectibleBinding"/> so pooled engine runners instantiated over
    /// user state machines are recognized.
    /// </summary>
    public bool IsAwaiterCollectible
    {
        get
        {
            Action<object>? pendingContinuation = continuation;
            if (pendingContinuation != null && Internal.CollectibleBinding.IsBound(pendingContinuation))
            {
                return true;
            }
            if (Internal.CollectibleBinding.IsBound(continuationState))
            {
                return true;
            }
            // Combinator middlemen: WhenAll/WhenAny register an ENGINE closure here while the user
            // continuation sits on the combinator's own core. Follow the chain so the frame loop
            // drops the whole Delay→WhenAll→user-awaiter chain instead of just direct bindings.
            return Internal.CollectibleBinding.IsChainBound(pendingContinuation, continuationState);
        }
    }

    /// <summary>
    /// Reset the core for reuse: publishes any previously unobserved error, bumps the version (so
    /// stale awaiters holding an old token fail fast), and clears all state.
    /// </summary>
    public void Reset()
    {
        ReportUnhandledError();
        unchecked { version += 1; }
        completedCount = 0;
        result = default;
        error = null;
        hasUnhandledError = false;
        continuation = null;
        continuationState = null;
    }

    private void ReportUnhandledError()
    {
        if (hasUnhandledError)
        {
            object? e = error;
            error = null;
            hasUnhandledError = false;
            if (e is ExceptionHolder holder)
            {
                XTaskScheduler.PublishUnobservedTaskException(holder.GetException());
            }
            else if (e is Exception oce)
            {
                XTaskScheduler.PublishUnobservedTaskException(oce);
            }
        }
    }

    public TResult GetResult(short token)
    {
        ValidateToken(token);
        if (completedCount == 0)
        {
            throw Error.InvalidOperation(Error.NotYetCompleted);
        }

        object? e = error;
        if (e != null)
        {
            hasUnhandledError = false;
            if (e is ExceptionHolder holder)
            {
                holder.Throw();
            }
            else // OperationCanceledException
            {
                throw (Exception)e;
            }
        }

        return result;
    }

    public XTaskStatus GetStatus(short token)
    {
        ValidateToken(token);
        return DeriveStatus();
    }

    public XTaskStatus UnsafeGetStatus() => DeriveStatus();

    private XTaskStatus DeriveStatus()
    {
        return (continuation == null || completedCount == 0) ? XTaskStatus.Pending
             : (error == null) ? XTaskStatus.Succeeded
             : (error is OperationCanceledException) ? XTaskStatus.Canceled
             : XTaskStatus.Faulted;
    }

    public void OnCompleted(Action<object> continuation, object state, short token)
    {
        ValidateToken(token);
        if (continuation == null)
        {
            throw Error.ArgumentNull(nameof(continuation));
        }

        object? oldContinuation = this.continuation;
        if (oldContinuation == null)
        {
            continuationState = state;
            oldContinuation = Interlocked.CompareExchange(ref this.continuation, continuation, null);
        }

        if (oldContinuation != null)
        {
            // Either (a) TrySet* already swapped in s_sentinel -> we are late: run the continuation now,
            // or (b) a real second continuation was registered -> user error (awaited twice).
            if (!ReferenceEquals(oldContinuation, XTaskCompletionSourceCoreShared.s_sentinel))
            {
                throw Error.InvalidOperation(Error.AlreadyContinuationRegistered);
            }

            continuation(state);
        }
    }

    public bool TrySetResult(TResult result)
    {
        if (Interlocked.Increment(ref completedCount) == 1)
        {
            this.result = result;
            InvokeContinuation();
            return true; // won the completion race
        }
        return false;
    }

    public bool TrySetException(Exception exception)
    {
        if (Interlocked.Increment(ref completedCount) == 1)
        {
            if (exception is OperationCanceledException oce)
            {
                error = oce;
            }
            else
            {
                error = new ExceptionHolder(System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception));
            }
            hasUnhandledError = true;
            InvokeContinuation();
            return true; // won the completion race
        }
        return false;
    }

    public bool TrySetCanceled(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref completedCount) == 1)
        {
            error = new OperationCanceledException(cancellationToken);
            hasUnhandledError = true;
            InvokeContinuation();
            return true; // won the completion race
        }
        return false;
    }

    private void InvokeContinuation()
    {
        // If a continuation was already registered (via OnCompleted) it runs inline; otherwise swap
        // in the sentinel so any late-registering OnCompleted sees "already completed" and runs inline.
        Action<object>? c = continuation;
        if (c != null || (c = Interlocked.CompareExchange(ref continuation, XTaskCompletionSourceCoreShared.s_sentinel, null)) != null)
        {
            c!(continuationState);
        }
    }

    private void ValidateToken(short token)
    {
        if (token != version)
        {
            throw Error.InvalidOperation(Error.VersionNotMatched);
        }
    }
}
