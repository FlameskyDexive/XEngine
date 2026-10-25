// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace XEngine.Async;

// NOTE: The state-machine runners are the heart of XTask's zero-allocation async plumbing. Each
// `async XTask` / `async XTask<T>` / `async XTaskVoid` method compiles to a struct state machine.
// On its FIRST suspension the builder copies that struct by value into a pooled runner
// (AsyncXTask<TStateMachine>, generic per method → dedicated pool). The runner caches its MoveNext
// delegate once in the ctor so no delegate is allocated per await; on GetResult the runner is
// returned to its pool. Synchronous completion never touches a runner — Task returns CompletedTask
// or FromException/FromResult with zero allocation.

/// <summary>Runner-side hooks the builder drives the state machine through (non-generic task).</summary>
internal interface IStateMachineRunnerPromise : IXTaskSource
{
    Action MoveNext { get; }
    XTask Task { get; }
    void SetResult();
    void SetException(Exception exception);
}

/// <summary>Runner-side hooks for typed tasks.</summary>
internal interface IStateMachineRunnerPromise<T> : IXTaskSource<T>
{
    Action MoveNext { get; }
    XTask<T> Task { get; }
    void SetResult(T result);
    void SetException(Exception exception);
}

/// <summary>Runner-side hooks for fire-and-forget XTaskVoid (no task is exposed).</summary>
internal interface IStateMachineRunnerVoidPromise
{
    Action MoveNext { get; }
    void SetResult();
    void SetException(Exception exception);
}

/// <summary>
/// Pooled runner that hosts one <typeparamref name="TStateMachine"/> and serves as the
/// <see cref="IXTaskSource"/> backing the resulting <see cref="XTask"/>. Generic per state-machine
/// type so each <c>async XTask</c> method owns a dedicated <see cref="TaskPool{T}"/>.
/// </summary>
internal sealed class AsyncXTask<TStateMachine> : IXTaskSource, IStateMachineRunnerPromise, ITaskPoolNode<AsyncXTask<TStateMachine>>
    where TStateMachine : IAsyncStateMachine
{
    private static TaskPool<AsyncXTask<TStateMachine>> s_pool;

    private TStateMachine _stateMachine;
    private XTaskCompletionSourceCore<AsyncUnit> _core;
    private AsyncXTask<TStateMachine>? _nextNode;
    private Action _moveNext;

    static AsyncXTask()
    {
        TaskPool.RegisterSizeGetter(typeof(AsyncXTask<TStateMachine>), () => s_pool.Size);
    }

    private AsyncXTask()
    {
        // Bind MoveNext once so AwaitUnsafeOnCompleted never allocates a delegate.
        _moveNext = Run;
    }

    public ref AsyncXTask<TStateMachine>? NextNode => ref _nextNode;

    public Action MoveNext => _moveNext;

    public XTask Task => new(this, _core.Version);

    /// <summary>Copy the state machine into a (possibly pooled) runner and publish it to the builder.</summary>
    public static void SetStateMachine(ref TStateMachine stateMachine, ref IStateMachineRunnerPromise? runnerPromiseField)
    {
        if (!s_pool.TryPop(out AsyncXTask<TStateMachine>? result))
        {
            result = new AsyncXTask<TStateMachine>();
        }
        // ORDER MATTERS: write the runner back into the builder field FIRST (via the ref), THEN copy
        // the state machine. The builder lives inside the state-machine struct, so this mutates the
        // original in place; the subsequent copy therefore inherits a non-null runnerPromise pointing
        // back at this runner. Reversing the order leaves the copy's builder.runnerPromise null, which
        // makes every later SetResult a silent no-op (the runner's core never completes).
        runnerPromiseField = result;
        result._stateMachine = stateMachine;
    }

    private void Run() => _stateMachine.MoveNext();

    // ---- IXTaskSource (delegate to core) ----

    public XTaskStatus GetStatus(short token) => _core.GetStatus(token);

    public XTaskStatus UnsafeGetStatus() => _core.UnsafeGetStatus();

    public void OnCompleted(Action<object> continuation, object state, short token)
        => _core.OnCompleted(continuation, state, token);

    public void GetResult(short token)
    {
        try
        {
            _core.GetResult(token);
        }
        finally
        {
            TryReturn();
        }
    }

    // ---- IStateMachineRunnerPromise ----

    public void SetResult() => _core.TrySetResult(AsyncUnit.Default);

    public void SetException(Exception exception) => _core.TrySetException(exception);

    private bool TryReturn()
    {
        _core.Reset();
        _stateMachine = default!;
        return s_pool.TryPush(this);
    }
}

/// <summary>Pooled runner for typed <c>async XTask&lt;T&gt;</c> methods.</summary>
internal sealed class AsyncXTask<TStateMachine, T> : IXTaskSource<T>, IStateMachineRunnerPromise<T>, ITaskPoolNode<AsyncXTask<TStateMachine, T>>
    where TStateMachine : IAsyncStateMachine
{
    private static TaskPool<AsyncXTask<TStateMachine, T>> s_pool;

    private TStateMachine _stateMachine;
    private XTaskCompletionSourceCore<T> _core;
    private AsyncXTask<TStateMachine, T>? _nextNode;
    private Action _moveNext;

    static AsyncXTask()
    {
        TaskPool.RegisterSizeGetter(typeof(AsyncXTask<TStateMachine, T>), () => s_pool.Size);
    }

    private AsyncXTask()
    {
        _moveNext = Run;
    }

    public ref AsyncXTask<TStateMachine, T>? NextNode => ref _nextNode;

    public Action MoveNext => _moveNext;

    public XTask<T> Task => new(this, _core.Version);

    public static void SetStateMachine(ref TStateMachine stateMachine, ref IStateMachineRunnerPromise<T>? runnerPromiseField)
    {
        if (!s_pool.TryPop(out AsyncXTask<TStateMachine, T>? result))
        {
            result = new AsyncXTask<TStateMachine, T>();
        }
        // See AsyncXTask<TStateMachine>.SetStateMachine: assign the ref BEFORE copying the state
        // machine so the copy inherits a non-null runnerPromise.
        runnerPromiseField = result;
        result._stateMachine = stateMachine;
    }

    private void Run() => _stateMachine.MoveNext();

    public XTaskStatus GetStatus(short token) => _core.GetStatus(token);

    public XTaskStatus UnsafeGetStatus() => _core.UnsafeGetStatus();

    public void OnCompleted(Action<object> continuation, object state, short token)
        => _core.OnCompleted(continuation, state, token);

    public T GetResult(short token)
    {
        try
        {
            return _core.GetResult(token);
        }
        finally
        {
            TryReturn();
        }
    }

    public void SetResult(T result) => _core.TrySetResult(result);

    public void SetException(Exception exception) => _core.TrySetException(exception);

    private bool TryReturn()
    {
        _core.Reset();
        _stateMachine = default!;
        return s_pool.TryPush(this);
    }
}

/// <summary>Pooled runner for fire-and-forget <c>async XTaskVoid</c> methods.</summary>
internal sealed class AsyncXTaskVoid<TStateMachine> : IStateMachineRunnerVoidPromise, ITaskPoolNode<AsyncXTaskVoid<TStateMachine>>
    where TStateMachine : IAsyncStateMachine
{
    private static TaskPool<AsyncXTaskVoid<TStateMachine>> s_pool;

    private TStateMachine _stateMachine;
    private AsyncXTaskVoid<TStateMachine>? _nextNode;
    private Action _moveNext;

    static AsyncXTaskVoid()
    {
        TaskPool.RegisterSizeGetter(typeof(AsyncXTaskVoid<TStateMachine>), () => s_pool.Size);
    }

    private AsyncXTaskVoid()
    {
        _moveNext = Run;
    }

    public ref AsyncXTaskVoid<TStateMachine>? NextNode => ref _nextNode;

    public Action MoveNext => _moveNext;

    public static void SetStateMachine(ref TStateMachine stateMachine, ref IStateMachineRunnerVoidPromise? runnerPromiseField)
    {
        if (!s_pool.TryPop(out AsyncXTaskVoid<TStateMachine>? result))
        {
            result = new AsyncXTaskVoid<TStateMachine>();
        }
        // See AsyncXTask<TStateMachine>.SetStateMachine: assign the ref BEFORE copying the state
        // machine so the copy inherits a non-null runner reference.
        runnerPromiseField = result;
        result._stateMachine = stateMachine;
    }

    private void Run() => _stateMachine.MoveNext();

    public void SetResult() => TryReturn();

    public void SetException(Exception exception)
    {
        // Fire-and-forget: unhandled exceptions go to the unobserved-exception sink, not the caller.
        XTaskScheduler.PublishUnobservedTaskException(exception);
        TryReturn();
    }

    private void TryReturn()
    {
        _stateMachine = default!;
        s_pool.TryPush(this);
    }
}
