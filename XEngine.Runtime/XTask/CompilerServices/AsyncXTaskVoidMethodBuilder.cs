// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace XEngine.Async;

// NOTE: AsyncXTaskVoidMethodBuilder is the compiler-driven builder for `async XTaskVoid`. It mirrors
// AsyncXTaskMethodBuilder but its Task is default(XTaskVoid) (never awaited) and unhandled
// exceptions go to XTaskScheduler.UnobservedTaskException rather than the caller.

/// <summary>Async method builder for <see cref="XTaskVoid"/>.</summary>
public struct AsyncXTaskVoidMethodBuilder
{
    private IStateMachineRunnerVoidPromise? _runner;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AsyncXTaskVoidMethodBuilder Create() => default;

    public XTaskVoid Task
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
        => stateMachine.MoveNext();

    public void SetResult()
    {
        if (_runner != null)
        {
            _runner.SetResult();
        }
    }

    public void SetException(Exception exception)
    {
        if (_runner != null)
        {
            _runner.SetException(exception);
        }
        else
        {
            // Sync-completing fire-and-forget method threw: route to the unobserved sink directly.
            XTaskScheduler.PublishUnobservedTaskException(exception);
        }
    }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : System.Runtime.CompilerServices.INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        if (_runner == null)
        {
            AsyncXTaskVoid<TStateMachine>.SetStateMachine(ref stateMachine, ref _runner);
        }
        awaiter.OnCompleted(_runner.MoveNext);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        if (_runner == null)
        {
            AsyncXTaskVoid<TStateMachine>.SetStateMachine(ref stateMachine, ref _runner);
        }
        awaiter.UnsafeOnCompleted(_runner.MoveNext);
    }
}
