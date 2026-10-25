// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

namespace XEngine.Async;

// WhenAll / WhenAny combinators. The promise objects are NOT pooled (they embed the result array and
// the task array for the duration of the fan-in), which matches UniTask's design. Synchronous-fast-
// path tasks are consumed immediately without registering a continuation; only pending tasks
// register a one-shot callback. A single Interlocked latch decides completion (all-done for
// WhenAll, first-done for WhenAny) and is idempotent under concurrent task completion.

public readonly partial struct XTask
{
    /// <summary>An <see cref="XTask"/> that completes when every task in <paramref name="tasks"/> has completed.</summary>
    public static XTask WhenAll(params XTask[] tasks)
    {
        if (tasks == null) throw new ArgumentNullException(nameof(tasks));
        if (tasks.Length == 0) return XTask.CompletedTask;

        var promise = new WhenAllPromise(tasks);
        return promise.Task;
    }

    /// <summary>An <see cref="XTask{T}"/> that completes with every result when all of <paramref name="tasks"/> complete.</summary>
    public static XTask<T[]> WhenAll<T>(params XTask<T>[] tasks)
    {
        if (tasks == null) throw new ArgumentNullException(nameof(tasks));
        if (tasks.Length == 0) return XTask.FromResult(Array.Empty<T>());

        var promise = new WhenAllPromise<T>(tasks);
        return promise.Task;
    }
}

internal sealed class WhenAllPromise : MoveNextSource, Internal.IChainCollectibleSource
{
    private readonly XTask[] _tasks;
    private int _remaining;

    public WhenAllPromise(XTask[] tasks)
    {
        _tasks = tasks;
        _remaining = tasks.Length;

        for (int i = 0; i < tasks.Length; i++)
        {
            // Snapshot the awaiter; XTaskAwaiter is a struct so this is stack-only.
            XTaskAwaiter awaiter = tasks[i].GetAwaiter();
            if (awaiter.IsCompleted)
            {
                TryConsume(awaiter);
            }
            else
            {
                int index = i;
                awaiter.UnsafeOnCompleted(() => TryConsume(_tasks[index].GetAwaiter()));
            }
        }
    }

    private void TryConsume(XTaskAwaiter awaiter)
    {
        try
        {
            awaiter.GetResult();
        }
        catch (Exception ex)
        {
            core.TrySetException(ex);
            return;
        }

        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            core.TrySetResult(AsyncUnit.Default);
        }
    }

    bool Internal.IChainCollectibleSource.HasCollectibleAwaiter => core.IsAwaiterCollectible;
}

internal sealed class WhenAllPromise<T> : IXTaskSource<T[]>, Internal.IChainCollectibleSource
{
    private readonly XTask<T>[] _tasks;
    private readonly T[] _results;
    private int _remaining;
    private XTaskCompletionSourceCore<T[]> _core;

    public WhenAllPromise(XTask<T>[] tasks)
    {
        _tasks = tasks;
        _results = new T[tasks.Length];
        _remaining = tasks.Length;

        for (int i = 0; i < tasks.Length; i++)
        {
            XTaskAwaiter<T> awaiter = tasks[i].GetAwaiter();
            if (awaiter.IsCompleted)
            {
                TryConsume(awaiter, i);
            }
            else
            {
                int index = i;
                awaiter.UnsafeOnCompleted(() => TryConsume(_tasks[index].GetAwaiter(), index));
            }
        }
    }

    public XTask<T[]> Task => new(this, _core.Version);

    private void TryConsume(XTaskAwaiter<T> awaiter, int index)
    {
        try
        {
            _results[index] = awaiter.GetResult();
        }
        catch (Exception ex)
        {
            _core.TrySetException(ex);
            return;
        }

        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            _core.TrySetResult(_results);
        }
    }

    public XTaskStatus GetStatus(short token) => _core.GetStatus(token);
    public XTaskStatus UnsafeGetStatus() => _core.UnsafeGetStatus();
    public void OnCompleted(Action<object> continuation, object state, short token) => _core.OnCompleted(continuation, state, token);
    public T[] GetResult(short token) => _core.GetResult(token);

    bool Internal.IChainCollectibleSource.HasCollectibleAwaiter => _core.IsAwaiterCollectible;
}
