// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading;
using XEngine.Async.Internal;

namespace XEngine.Async;

// NOTE: XTaskCompletionSource and XTaskCompletionSource<T> are the multi-await, locked variant.
// Unlike AutoResetXTaskCompletionSource they accept arbitrarily many awaiters and are NOT pooled
// (Token is always 0; no version bump; safe to await from multiple coroutines). Use these when
// several coroutines must wait on the same one-shot event.

/// <summary>
/// Multi-await, thread-safe completion source for void-producing XTasks. Not pooled. Several
/// awaiters may register on the same instance; all fire when the source completes.
/// </summary>
public sealed class XTaskCompletionSource : IXTaskSource, IChainCollectibleSource
{
    private const int Pending = 0;
    private const int Succeeded = 1;
    private const int Faulted = 2;
    private const int Canceled = 3;

    private readonly object _lock = new();
    private int _status = Pending;
    private object? _error; // ExceptionHolder | OperationCanceledException
    private Action<object>? _firstContinuation;
    private object? _firstState;
    private List<KeyValuePair<Action<object>, object>>? _secondaryContinuations;

    /// <summary>An <see cref="XTask"/> that awaits this source. Token is always 0 (no versioning).</summary>
    public XTask Task => new(this, 0);

    public bool TrySetResult()
    {
        lock (_lock)
        {
            if (_status != Pending) return false;
            _status = Succeeded;
        }
        InvokeContinuations();
        return true;
    }

    public bool TrySetException(Exception exception)
    {
        lock (_lock)
        {
            if (_status != Pending) return false;
            _error = (exception is OperationCanceledException) ? exception : new ExceptionHolder(System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception));
            _status = (exception is OperationCanceledException) ? Canceled : Faulted;
        }
        InvokeContinuations();
        return true;
    }

    public bool TrySetCanceled(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_status != Pending) return false;
            _error = new OperationCanceledException(cancellationToken);
            _status = Canceled;
        }
        InvokeContinuations();
        return true;
    }

    public XTaskStatus GetStatus(short token)
    {
        lock (_lock) { return (XTaskStatus)_status; }
    }

    public XTaskStatus UnsafeGetStatus()
    {
        lock (_lock) { return (XTaskStatus)_status; }
    }

    public void OnCompleted(Action<object> continuation, object state, short token)
    {
        if (continuation == null) throw Error.ArgumentNull(nameof(continuation));

        bool runNow = false;
        lock (_lock)
        {
            if (_status != Pending)
            {
                runNow = true;
            }
            else if (_firstContinuation == null)
            {
                _firstContinuation = continuation;
                _firstState = state;
            }
            else
            {
                (_secondaryContinuations ??= new List<KeyValuePair<Action<object>, object>>(4))
                    .Add(new KeyValuePair<Action<object>, object>(continuation, state));
            }
        }

        if (runNow)
        {
            continuation(state);
        }
    }

    public void GetResult(short token)
    {
        int s;
        object? e;
        lock (_lock)
        {
            s = _status;
            e = _error;
            if (s == Pending)
            {
                throw Error.InvalidOperation(Error.NotYetCompleted);
            }
        }

        if (e is ExceptionHolder holder)
        {
            holder.Throw();
        }
        else if (e is OperationCanceledException oce)
        {
            throw oce;
        }
    }

    private void InvokeContinuations()
    {
        Action<object>? first;
        object? firstState;
        List<KeyValuePair<Action<object>, object>>? secondary;

        lock (_lock)
        {
            first = _firstContinuation;
            firstState = _firstState;
            secondary = _secondaryContinuations;
            _firstContinuation = null;
            _firstState = null;
            _secondaryContinuations = null;
        }

        try
        {
            first?.Invoke(firstState!);
            if (secondary != null)
            {
                for (int i = 0; i < secondary.Count; i++)
                {
                    secondary[i].Key.Invoke(secondary[i].Value);
                }
            }
        }
        catch (Exception ex)
        {
            // A faulting continuation must not break sibling continuations or leak.
            XTaskScheduler.PublishUnobservedTaskException(ex);
        }
    }

    /// <summary>Hot-reload: true when any pending awaiter belongs to a collectible assembly (see
    /// <see cref="Internal.IChainCollectibleSource"/>).</summary>
    bool IChainCollectibleSource.HasCollectibleAwaiter
    {
        get
        {
            lock (_lock)
            {
                if (_status != Pending) return false;
                if (CollectibleBinding.IsBound(_firstContinuation) || CollectibleBinding.IsBound(_firstState)) return true;
                if (_secondaryContinuations != null)
                {
                    for (int i = 0; i < _secondaryContinuations.Count; i++)
                    {
                        if (CollectibleBinding.IsBound(_secondaryContinuations[i].Key)
                            || CollectibleBinding.IsBound(_secondaryContinuations[i].Value))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
        }
    }
}

/// <summary>
/// Multi-await, thread-safe completion source for typed XTasks. See <see cref="XTaskCompletionSource"/>.
/// </summary>
public sealed class XTaskCompletionSource<T> : IXTaskSource<T>, IChainCollectibleSource
{
    private const int Pending = 0;
    private const int Succeeded = 1;
    private const int Faulted = 2;
    private const int Canceled = 3;

    private readonly object _lock = new();
    private int _status = Pending;
    private T? _result;
    private object? _error; // ExceptionHolder | OperationCanceledException
    private Action<object>? _firstContinuation;
    private object? _firstState;
    private List<KeyValuePair<Action<object>, object>>? _secondaryContinuations;

    public XTask<T> Task => new(this, 0);

    public bool TrySetResult(T value)
    {
        lock (_lock)
        {
            if (_status != Pending) return false;
            _result = value;
            _status = Succeeded;
        }
        InvokeContinuations();
        return true;
    }

    public bool TrySetException(Exception exception)
    {
        lock (_lock)
        {
            if (_status != Pending) return false;
            _error = (exception is OperationCanceledException) ? exception : new ExceptionHolder(System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception));
            _status = (exception is OperationCanceledException) ? Canceled : Faulted;
        }
        InvokeContinuations();
        return true;
    }

    public bool TrySetCanceled(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_status != Pending) return false;
            _error = new OperationCanceledException(cancellationToken);
            _status = Canceled;
        }
        InvokeContinuations();
        return true;
    }

    public XTaskStatus GetStatus(short token)
    {
        lock (_lock) { return (XTaskStatus)_status; }
    }

    public XTaskStatus UnsafeGetStatus()
    {
        lock (_lock) { return (XTaskStatus)_status; }
    }

    public void OnCompleted(Action<object> continuation, object state, short token)
    {
        if (continuation == null) throw Error.ArgumentNull(nameof(continuation));

        bool runNow = false;
        lock (_lock)
        {
            if (_status != Pending)
            {
                runNow = true;
            }
            else if (_firstContinuation == null)
            {
                _firstContinuation = continuation;
                _firstState = state;
            }
            else
            {
                (_secondaryContinuations ??= new List<KeyValuePair<Action<object>, object>>(4))
                    .Add(new KeyValuePair<Action<object>, object>(continuation, state));
            }
        }

        if (runNow)
        {
            continuation(state);
        }
    }

    public T GetResult(short token)
    {
        int s;
        object? e;
        lock (_lock)
        {
            s = _status;
            e = _error;
            if (s == Pending)
            {
                throw Error.InvalidOperation(Error.NotYetCompleted);
            }
        }

        if (e is ExceptionHolder holder)
        {
            holder.Throw();
        }
        else if (e is OperationCanceledException oce)
        {
            throw oce;
        }

        return _result!;
    }

    private void InvokeContinuations()
    {
        Action<object>? first;
        object? firstState;
        List<KeyValuePair<Action<object>, object>>? secondary;

        lock (_lock)
        {
            first = _firstContinuation;
            firstState = _firstState;
            secondary = _secondaryContinuations;
            _firstContinuation = null;
            _firstState = null;
            _secondaryContinuations = null;
        }

        try
        {
            first?.Invoke(firstState!);
            if (secondary != null)
            {
                for (int i = 0; i < secondary.Count; i++)
                {
                    secondary[i].Key.Invoke(secondary[i].Value);
                }
            }
        }
        catch (Exception ex)
        {
            XTaskScheduler.PublishUnobservedTaskException(ex);
        }
    }

    /// <summary>Hot-reload: see <see cref="IChainCollectibleSource"/> on the non-generic variant.</summary>
    bool IChainCollectibleSource.HasCollectibleAwaiter
    {
        get
        {
            lock (_lock)
            {
                if (_status != Pending) return false;
                if (CollectibleBinding.IsBound(_firstContinuation) || CollectibleBinding.IsBound(_firstState)) return true;
                if (_secondaryContinuations != null)
                {
                    for (int i = 0; i < _secondaryContinuations.Count; i++)
                    {
                        if (CollectibleBinding.IsBound(_secondaryContinuations[i].Key)
                            || CollectibleBinding.IsBound(_secondaryContinuations[i].Value))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
        }
    }
}
