// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Async;

// Bridges between XTask and the BCL Task / ValueTask types. These are allocation-paying convenience
// adapters (Task is reference-typed, so any round-trip allocates) intended for interop at library
// boundaries; prefer staying in XTask within gameplay/frame-loop code.

public readonly partial struct XTask
{
    /// <summary>
    /// Convert to a <see cref="Task"/>. Allocates a <see cref="TaskCompletionSource{TResult}"/> that
    /// the XTask completes with its result/exception. Use only at Task interop boundaries.
    /// </summary>
    public Task AsTask()
    {
        IXTaskSource? s = source;
        if (s == null)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        XTask self = this;
        s.OnCompleted(static state =>
        {
            var t = (Tuple<XTask, TaskCompletionSource<bool>>)state!;
            try
            {
                t.Item1.source!.GetResult(t.Item1.token);
                t.Item2.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                t.Item2.TrySetCanceled();
            }
            catch (Exception ex)
            {
                t.Item2.TrySetException(ex);
            }
        }, Tuple.Create(self, tcs), token);
        return tcs.Task;
    }
}

/// <summary>Bridge extension methods between XTask and Task / ValueTask.</summary>
public static class XTaskBridgeExtensions
{
    /// <summary>Await an <see cref="XTask"/> as a <see cref="ValueTask"/>. Completes synchronously
    /// when the XTask is already done; otherwise registers a continuation that completes a TCS.</summary>
    public static ValueTask AsValueTask(this XTask task)
    {
        if (task.source == null)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            // Already completed? Observe the result inline and return a completed ValueTask.
            if (task.source.GetStatus(task.token).IsCompleted())
            {
                task.source.GetResult(task.token);
                return ValueTask.CompletedTask;
            }
        }
        catch (Exception ex)
        {
            return ValueTask.FromException(ex);
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        task.source.OnCompleted(static state =>
        {
            var t = (Tuple<XTask, TaskCompletionSource<bool>>)state!;
            try
            {
                t.Item1.source!.GetResult(t.Item1.token);
                t.Item2.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                t.Item2.TrySetCanceled();
            }
            catch (Exception ex)
            {
                t.Item2.TrySetException(ex);
            }
        }, Tuple.Create(task, tcs), task.token);
        return new ValueTask(tcs.Task);
    }

    /// <summary>Convert a <see cref="Task"/> to an <see cref="XTask"/>. Allocates on the interop boundary.</summary>
    public static XTask AsXTask(this Task task)
    {
        if (task == null) throw new ArgumentNullException(nameof(task));
        if (task.IsCompleted)
        {
            if (task.IsFaulted) return XTask.FromException(task.Exception!.GetBaseException());
            if (task.IsCanceled) return XTask.FromCanceled();
            return XTask.CompletedTask;
        }

        var tcs = AutoResetXTaskCompletionSource.Create();
        lock (s_pendingTaskBridges)
        {
            s_pendingTaskBridges.Add(tcs);
        }
        task.ContinueWith(static (t, state) =>
        {
            var x = (AutoResetXTaskCompletionSource)state!;
            lock (s_pendingTaskBridges)
            {
                s_pendingTaskBridges.Remove(x);
            }
            if (t.IsFaulted) x.TrySetException(t.Exception!.GetBaseException());
            else if (t.IsCanceled) x.TrySetCanceled();
            else x.TrySetResult();
        }, tcs, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return tcs.Task;
    }

    /// <summary>
    /// Bridge sources still waiting on an unfinished BCL Task. The Task roots the source, and a
    /// script awaiter's continuation lives in the source's core - without this registry the ALC stays
    /// pinned until the Task finishes (possibly never).
    /// </summary>
    private static readonly List<AutoResetXTaskCompletionSource> s_pendingTaskBridges = new();

    /// <summary>
    /// Hot-reload: detach script-bound continuations from every bridge source that is still waiting
    /// on a BCL Task. The Task itself cannot be reached, so the source's awaiter is dropped instead;
    /// a late Task completion lands on the detached source harmlessly.
    /// </summary>
    [XEngine.Runtime.OnAssemblyUnload]
    private static void DetachPendingTaskBridgesForReload()
    {
        lock (s_pendingTaskBridges)
        {
            foreach (var bridge in s_pendingTaskBridges)
                bridge.DetachForReload();
            s_pendingTaskBridges.Clear();
        }
    }

    /// <summary>Convert a <see cref="ValueTask"/> to an <see cref="XTask"/>.</summary>
    public static XTask AsXTask(this ValueTask task)
    {
        if (task.IsCompleted)
        {
            try
            {
                task.GetAwaiter().GetResult();
                return XTask.CompletedTask;
            }
            catch (OperationCanceledException)
            {
                return XTask.FromCanceled();
            }
            catch (Exception ex)
            {
                return XTask.FromException(ex);
            }
        }

        return task.AsTask().AsXTask();
    }
}
