// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using XEngine.Async.Internal;

namespace XEngine.Async;

// Factory helpers and pre-baked terminal sources for XTask / XTask<T>.

public readonly partial struct XTask
{
    /// <summary>A synchronously-completed typed task holding <paramref name="value"/>. Zero-allocation
    /// (null-source fast path; <see cref="XTask{T}.XTask(T)"/>).</summary>
    public static XTask<T> FromResult<T>(T value) => new(value);

    /// <summary>A task that is already faulted with <paramref name="exception"/>.</summary>
    public static XTask FromException(Exception exception)
    {
        if (exception is OperationCanceledException oce) return FromCanceled(oce.CancellationToken);
        return new XTask(ExceptionXTaskSource.Create(exception), 0);
    }

    /// <summary>A typed task that is already faulted with <paramref name="exception"/>.</summary>
    public static XTask<T> FromException<T>(Exception exception)
    {
        if (exception is OperationCanceledException oce) return FromCanceled<T>(oce.CancellationToken);
        return new XTask<T>(ExceptionXTaskSource<T>.Create(exception), 0);
    }

    /// <summary>A task that is already canceled.</summary>
    public static XTask FromCanceled(CancellationToken cancellationToken = default)
        => new XTask(CanceledXTaskSource.Create(cancellationToken), 0);

    /// <summary>A typed task that is already canceled.</summary>
    public static XTask<T> FromCanceled<T>(CancellationToken cancellationToken = default)
        => new XTask<T>(CanceledXTaskSource<T>.Create(cancellationToken), 0);

    /// <summary>A task that never completes. Awaiting it leaks the awaiting state machine by design
    /// ("never" means never). Useful as a sentinel with <see cref="XTask.WhenAny"/> cancellation patterns.</summary>
    public static XTask Never => new XTask(NeverXTaskSource.Instance, 0);
}

public readonly partial struct XTask<T>
{
    /// <summary>A typed task that is already faulted with <paramref name="exception"/>.</summary>
    public static XTask<T> FromException(Exception exception)
    {
        if (exception is OperationCanceledException oce) return FromCanceled(oce.CancellationToken);
        return new XTask<T>(ExceptionXTaskSource<T>.Create(exception), 0);
    }

    /// <summary>A typed task that is already canceled.</summary>
    public static XTask<T> FromCanceled(CancellationToken cancellationToken = default)
        => new XTask<T>(CanceledXTaskSource<T>.Create(cancellationToken), 0);

    /// <summary>A typed task that never completes. See <see cref="XTask.Never"/>.</summary>
    public static XTask<T> Never => new XTask<T>(NeverXTaskSource<T>.Instance, 0);
}

// ---- Terminal sources (non-pooled; small fixed-state sources for From*) ----

internal sealed class ExceptionXTaskSource : IXTaskSource
{
    private readonly ExceptionDispatchInfo _info;

    private ExceptionXTaskSource(Exception exception) { _info = ExceptionDispatchInfo.Capture(exception); }

    public static ExceptionXTaskSource Create(Exception exception) => new(exception);

    public XTaskStatus GetStatus(short token) => XTaskStatus.Faulted;
    public XTaskStatus UnsafeGetStatus() => XTaskStatus.Faulted;
    public void OnCompleted(Action<object> continuation, object state, short token) => continuation(state);
    public void GetResult(short token) => _info.Throw();
}

internal sealed class ExceptionXTaskSource<T> : IXTaskSource<T>
{
    private readonly ExceptionDispatchInfo _info;

    private ExceptionXTaskSource(Exception exception) { _info = ExceptionDispatchInfo.Capture(exception); }

    public static ExceptionXTaskSource<T> Create(Exception exception) => new(exception);

    public XTaskStatus GetStatus(short token) => XTaskStatus.Faulted;
    public XTaskStatus UnsafeGetStatus() => XTaskStatus.Faulted;
    public void OnCompleted(Action<object> continuation, object state, short token) => continuation(state);
    public T GetResult(short token) { _info.Throw(); return default!; }
}

internal sealed class CanceledXTaskSource : IXTaskSource
{
    private readonly CancellationToken _cancellationToken;

    private CanceledXTaskSource(CancellationToken cancellationToken) { _cancellationToken = cancellationToken; }

    public static CanceledXTaskSource Create(CancellationToken cancellationToken) => new(cancellationToken);

    public XTaskStatus GetStatus(short token) => XTaskStatus.Canceled;
    public XTaskStatus UnsafeGetStatus() => XTaskStatus.Canceled;
    public void OnCompleted(Action<object> continuation, object state, short token) => continuation(state);
    public void GetResult(short token) => throw new OperationCanceledException(_cancellationToken);
}

internal sealed class CanceledXTaskSource<T> : IXTaskSource<T>
{
    private readonly CancellationToken _cancellationToken;

    private CanceledXTaskSource(CancellationToken cancellationToken) { _cancellationToken = cancellationToken; }

    public static CanceledXTaskSource<T> Create(CancellationToken cancellationToken) => new(cancellationToken);

    public XTaskStatus GetStatus(short token) => XTaskStatus.Canceled;
    public XTaskStatus UnsafeGetStatus() => XTaskStatus.Canceled;
    public void OnCompleted(Action<object> continuation, object state, short token) => continuation(state);
    public T GetResult(short token) => throw new OperationCanceledException(_cancellationToken);
}

internal sealed class NeverXTaskSource : IXTaskSource
{
    public static readonly NeverXTaskSource Instance = new();
    private NeverXTaskSource() { }

    public XTaskStatus GetStatus(short token) => XTaskStatus.Pending;
    public XTaskStatus UnsafeGetStatus() => XTaskStatus.Pending;
    public void OnCompleted(Action<object> continuation, object state, short token) { /* never completes by design */ }
    public void GetResult(short token) => throw Error.InvalidOperation(Error.NotYetCompleted);
}

internal sealed class NeverXTaskSource<T> : IXTaskSource<T>
{
    public static readonly NeverXTaskSource<T> Instance = new();
    private NeverXTaskSource() { }

    public XTaskStatus GetStatus(short token) => XTaskStatus.Pending;
    public XTaskStatus UnsafeGetStatus() => XTaskStatus.Pending;
    public void OnCompleted(Action<object> continuation, object state, short token) { /* never completes by design */ }
    public T GetResult(short token) => throw Error.InvalidOperation(Error.NotYetCompleted);
}
