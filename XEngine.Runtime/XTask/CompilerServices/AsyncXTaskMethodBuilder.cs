// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace XEngine.Async;

// NOTE: AsyncXTaskMethodBuilder / AsyncXTaskMethodBuilder<T> are the compiler-driven builders wired
// to XTask / XTask<T> via [AsyncMethodBuilder]. They are structs with the standard 8-member shape
// (Create/Task/Start/SetStateMachine/SetResult/SetException/AwaitOnCompleted/AwaitUnsafeOnCompleted).
// On the synchronous path no runner is created and Task returns CompletedTask / FromResult /
// FromException with zero allocation. On the first suspension the state machine is copied into a
// pooled AsyncXTask<TStateMachine> runner.

/// <summary>Async method builder for <see cref="XTask"/>.</summary>
public struct AsyncXTaskMethodBuilder
{
    private IStateMachineRunnerPromise? _runnerPromise;
    private Exception? _ex;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AsyncXTaskMethodBuilder Create() => default;

    public XTask Task
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _runnerPromise != null ? _runnerPromise.Task
             : (_ex != null ? XTask.FromException(_ex) : XTask.CompletedTask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
        => stateMachine.MoveNext();

    public void SetResult()
    {
        if (_runnerPromise != null)
        {
            _runnerPromise.SetResult();
        }
    }

    public void SetException(Exception exception)
    {
        if (_runnerPromise != null)
        {
            _runnerPromise.SetException(exception);
        }
        else
        {
            _ex = exception;
        }
    }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : System.Runtime.CompilerServices.INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        if (_runnerPromise == null)
        {
            AsyncXTask<TStateMachine>.SetStateMachine(ref stateMachine, ref _runnerPromise);
        }
        awaiter.OnCompleted(_runnerPromise.MoveNext);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        if (_runnerPromise == null)
        {
            AsyncXTask<TStateMachine>.SetStateMachine(ref stateMachine, ref _runnerPromise);
        }
        awaiter.UnsafeOnCompleted(_runnerPromise.MoveNext);
    }
}

/// <summary>Async method builder for <see cref="XTask{T}"/>.</summary>
public struct AsyncXTaskMethodBuilder<T>
{
    private IStateMachineRunnerPromise<T>? _runnerPromise;
    private Exception? _ex;
    private T? _result;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AsyncXTaskMethodBuilder<T> Create() => default;

    public XTask<T> Task
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _runnerPromise != null ? _runnerPromise.Task
             : (_ex != null ? XTask<T>.FromException(_ex) : new XTask<T>(_result!));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
        => stateMachine.MoveNext();

    public void SetResult(T result)
    {
        // 'not' pattern would need C# 9+; this is readable enough.
        if (_runnerPromise != null)
        {
            _runnerPromise.SetResult(result);
        }
        else
        {
            _result = result;
        }
    }

    public void SetException(Exception exception)
    {
        if (_runnerPromise != null)
        {
            _runnerPromise.SetException(exception);
        }
        else
        {
            _ex = exception;
        }
    }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : System.Runtime.CompilerServices.INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        if (_runnerPromise == null)
        {
            AsyncXTask<TStateMachine, T>.SetStateMachine(ref stateMachine, ref _runnerPromise);
        }
        awaiter.OnCompleted(_runnerPromise.MoveNext);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        if (_runnerPromise == null)
        {
            AsyncXTask<TStateMachine, T>.SetStateMachine(ref stateMachine, ref _runnerPromise);
        }
        awaiter.UnsafeOnCompleted(_runnerPromise.MoveNext);
    }
}
