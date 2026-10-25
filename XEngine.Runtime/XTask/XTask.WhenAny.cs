// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

namespace XEngine.Async;

public readonly partial struct XTask
{
    /// <summary>An <see cref="XTask{T}"/> that completes with the index of the first task to complete (or fault).</summary>
    public static XTask<int> WhenAny(params XTask[] tasks)
    {
        if (tasks == null) throw new ArgumentNullException(nameof(tasks));
        if (tasks.Length == 0) throw new ArgumentException("WhenAny requires at least one task.", nameof(tasks));

        var promise = new WhenAnyPromise(tasks);
        return promise.Task;
    }

    /// <summary>An <see cref="XTask{T}"/> that completes with the index and result of the first task to complete.</summary>
    public static XTask<WhenAnyResult<T>> WhenAny<T>(params XTask<T>[] tasks)
    {
        if (tasks == null) throw new ArgumentNullException(nameof(tasks));
        if (tasks.Length == 0) throw new ArgumentException("WhenAny requires at least one task.", nameof(tasks));

        var promise = new WhenAnyPromise<T>(tasks);
        return promise.Task;
    }
}

/// <summary>The outcome of <see cref="XTask.WhenAny{T}"/>: which task index won and what it produced.</summary>
public readonly struct WhenAnyResult<T>
{
    public int Index { get; }
    public T Result { get; }

    public WhenAnyResult(int index, T result)
    {
        Index = index;
        Result = result;
    }

    public void Deconstruct(out int index, out T result)
    {
        index = Index;
        result = Result;
    }
}

internal sealed class WhenAnyPromise : IXTaskSource<int>, Internal.IChainCollectibleSource
{
    private readonly XTask[] _tasks;
    private int _winner;
    private XTaskCompletionSourceCore<int> _core;

    public WhenAnyPromise(XTask[] tasks)
    {
        _tasks = tasks;
        _winner = -1;

        for (int i = 0; i < tasks.Length; i++)
        {
            XTaskAwaiter awaiter = tasks[i].GetAwaiter();
            if (awaiter.IsCompleted)
            {
                TryWin(awaiter, i);
            }
            else
            {
                int index = i;
                awaiter.UnsafeOnCompleted(() => TryWin(_tasks[index].GetAwaiter(), index));
            }
        }
    }

    public XTask<int> Task => new(this, _core.Version);

    private void TryWin(XTaskAwaiter awaiter, int index)
    {
        // First completer wins; subsequent completions are no-ops (TrySetResult/TrySetException
        // return false once the core is settled). Observe the result so exceptions are observed.
        try
        {
            awaiter.GetResult();
            if (Interlocked.CompareExchange(ref _winner, index, -1) == -1)
            {
                _core.TrySetResult(index);
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.CompareExchange(ref _winner, index, -1) == -1)
            {
                _core.TrySetException(ex);
            }
        }
    }

    public XTaskStatus GetStatus(short token) => _core.GetStatus(token);
    public XTaskStatus UnsafeGetStatus() => _core.UnsafeGetStatus();
    public void OnCompleted(Action<object> continuation, object state, short token) => _core.OnCompleted(continuation, state, token);
    public int GetResult(short token) => _core.GetResult(token);

    bool Internal.IChainCollectibleSource.HasCollectibleAwaiter => _core.IsAwaiterCollectible;
}

internal sealed class WhenAnyPromise<T> : IXTaskSource<WhenAnyResult<T>>, Internal.IChainCollectibleSource
{
    private readonly XTask<T>[] _tasks;
    private int _winner;
    private XTaskCompletionSourceCore<WhenAnyResult<T>> _core;

    public WhenAnyPromise(XTask<T>[] tasks)
    {
        _tasks = tasks;
        _winner = -1;

        for (int i = 0; i < tasks.Length; i++)
        {
            XTaskAwaiter<T> awaiter = tasks[i].GetAwaiter();
            if (awaiter.IsCompleted)
            {
                TryWin(awaiter, i);
            }
            else
            {
                int index = i;
                awaiter.UnsafeOnCompleted(() => TryWin(_tasks[index].GetAwaiter(), index));
            }
        }
    }

    public XTask<WhenAnyResult<T>> Task => new(this, _core.Version);

    private void TryWin(XTaskAwaiter<T> awaiter, int index)
    {
        try
        {
            T result = awaiter.GetResult();
            if (Interlocked.CompareExchange(ref _winner, index, -1) == -1)
            {
                _core.TrySetResult(new WhenAnyResult<T>(index, result));
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.CompareExchange(ref _winner, index, -1) == -1)
            {
                _core.TrySetException(ex);
            }
        }
    }

    public XTaskStatus GetStatus(short token) => _core.GetStatus(token);
    public XTaskStatus UnsafeGetStatus() => _core.UnsafeGetStatus();
    public void OnCompleted(Action<object> continuation, object state, short token) => _core.OnCompleted(continuation, state, token);
    public WhenAnyResult<T> GetResult(short token) => _core.GetResult(token);

    bool Internal.IChainCollectibleSource.HasCollectibleAwaiter => _core.IsAwaiterCollectible;
}
