// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace XEngine.Async;

/// <summary>Extension helpers on <see cref="XTaskStatus"/>.</summary>
public static class XTaskStatusExtensions
{
    /// <summary>True for any terminal status (Succeeded, Faulted, or Canceled).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsCompleted(this XTaskStatus status) => status != XTaskStatus.Pending;
}

internal static class AwaiterActions
{
    /// <summary>
    /// Singleton <see cref="Action{T}"/> that invokes an <see cref="Action"/> boxed as object.
    /// Every awaiter hands the source this delegate plus the compiler-supplied MoveNext Action as
    /// its state, so adding a new awaitable never introduces a per-await closure.
    /// </summary>
    internal static readonly Action<object> InvokeContinuationDelegate = InvokeContinuation;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InvokeContinuation(object state) => ((Action)state).Invoke();
}

/// <summary>
/// A lightweight awaitable backed by an <see cref="IXTaskSource"/>. A <see langword="null"/>
/// <see cref="source"/> means the task completed synchronously, in which case awaiting it is zero
/// interface dispatch and zero allocation. This struct is the non-generic (void-result) variant;
/// see <see cref="XTask{T}"/> for the typed variant.
/// </summary>
[AsyncMethodBuilder(typeof(AsyncXTaskMethodBuilder))]
public readonly partial struct XTask
{
    internal readonly IXTaskSource? source; // null = synchronously completed
    internal readonly short token;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public XTask(IXTaskSource source, short token)
    {
        this.source = source;
        this.token = token;
    }

    /// <summary>Current status. Reads <see cref="XTaskStatus.Succeeded"/> when source is null.</summary>
    public XTaskStatus Status
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => source == null ? XTaskStatus.Succeeded : source.GetStatus(token);
    }

    /// <summary>True once the task reaches a terminal state.</summary>
    public bool IsCompleted
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => source == null || source.GetStatus(token).IsCompleted();
    }

    /// <summary>Get the awaiter used by <c>await</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public XTaskAwaiter GetAwaiter() => new(this);

    /// <summary>
    /// A pre-completed task. Equivalent to <c>default(XTask)</c> (null source) but named for clarity.
    /// </summary>
    public static XTask CompletedTask
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => default;
    }
}

/// <summary>Awaiter for <see cref="XTask"/>.</summary>
public readonly struct XTaskAwaiter : ICriticalNotifyCompletion
{
    private readonly XTask _task;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal XTaskAwaiter(XTask task) { _task = task; }

    public bool IsCompleted
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _task.IsCompleted;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GetResult()
    {
        if (_task.source == null) return;
        _task.source.GetResult(_task.token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UnsafeOnCompleted(Action continuation)
    {
        IXTaskSource? s = _task.source;
        if (s == null)
        {
            continuation();
            return;
        }
        s.OnCompleted(AwaiterActions.InvokeContinuationDelegate, continuation, _task.token);
    }
}

/// <summary>
/// A lightweight typed awaitable. When <see cref="source"/> is null the task completed
/// synchronously and <see cref="result"/> holds the value (the zero-alloc hot path).
/// </summary>
[AsyncMethodBuilder(typeof(AsyncXTaskMethodBuilder<>))]
public readonly partial struct XTask<T>
{
    internal readonly IXTaskSource<T>? source;
    internal readonly T? result;
    internal readonly short token;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public XTask(IXTaskSource<T> source, short token)
    {
        this.source = source;
        this.result = default;
        this.token = token;
    }

    /// <summary>Construct a synchronously-completed task holding <paramref name="result"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public XTask(T result)
    {
        source = null;
        this.result = result;
        token = 0;
    }

    public XTaskStatus Status
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => source == null ? XTaskStatus.Succeeded : source.GetStatus(token);
    }

    public bool IsCompleted
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => source == null || source.GetStatus(token).IsCompleted();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public XTaskAwaiter<T> GetAwaiter() => new(this);
}

/// <summary>Awaiter for <see cref="XTask{T}"/>.</summary>
public readonly struct XTaskAwaiter<T> : ICriticalNotifyCompletion
{
    private readonly XTask<T> _task;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal XTaskAwaiter(XTask<T> task) { _task = task; }

    public bool IsCompleted
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _task.IsCompleted;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T GetResult()
    {
        if (_task.source == null) return _task.result!;
        return _task.source.GetResult(_task.token);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UnsafeOnCompleted(Action continuation)
    {
        IXTaskSource<T>? s = _task.source;
        if (s == null)
        {
            continuation();
            return;
        }
        s.OnCompleted(AwaiterActions.InvokeContinuationDelegate, continuation, _task.token);
    }
}
