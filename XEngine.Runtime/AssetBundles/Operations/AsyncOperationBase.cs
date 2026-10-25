// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// AsyncOperationBase - the coroutine-style async primitive that the whole resource layer is built
// on. Every provider/loader/download/manifest op derives from this. The model: the scheduler calls
// InternalStart once, then InternalUpdate each frame until SetResult/SetError flips the status to a
// terminal state, then the Completed event fires and the awaiter resolves. A blocking
// WaitForCompletion path exists for sync callers (and re-entrant loader-thread loads).
//
// This is an independent rewrite informed by YooAsset's AsyncOperationBase (Apache-2.0); the public
// surface (IsDone/Status/Progress/Priority/Completed/WaitForCompletion) mirrors that model so the
// ported resource layer maps cleanly, but no YooAsset source or type names are reproduced.

using System;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Base class for all async work in the AssetBundles subsystem. Subclasses implement
/// <see cref="InternalStart"/> and <see cref="InternalUpdate"/> and call <see cref="SetResult"/> or
/// <see cref="SetError"/> when done. Driven by <see cref="AsyncOperationSystem"/>.
/// </summary>
public abstract partial class AsyncOperationBase : IEnumerator, IComparable<AsyncOperationBase>
{
    private EOperationStatus _status = EOperationStatus.None;
    private string? _error;
    private uint _priority;
    private int _executing;

    private Action<AsyncOperationBase>? _completedSingle;
    private System.Collections.Generic.List<Action<AsyncOperationBase>>? _completedList;
    private TaskCompletionSource<bool>? _tcs;

    /// <summary>Set by the scheduler when the priority changes so it can re-sort its queue.</summary>
    internal bool IsDirty { get; set; }

    /// <summary>True once terminal callbacks and the awaiter Task have been completed.</summary>
    internal bool IsFinalized { get; private set; }

    /// <summary>True while inside <see cref="WaitForCompletion"/>; disables the per-frame time slice.</summary>
    internal bool IsWaitForCompletion { get; private set; }

    /// <summary>
    /// True when the current frame's async time slice is exhausted. Subclasses should yield
    /// (return from InternalUpdate) when this becomes true so the frame can finish. Always false
    /// during <see cref="WaitForCompletion"/> so sync waits run to completion.
    /// </summary>
    protected bool IsBusy => IsWaitForCompletion ? false : AsyncOperationSystem.IsBusy;

    /// <summary>Priority for scheduler ordering (higher runs first).</summary>
    public uint Priority
    {
        get => _priority;
        set { if (_priority != value) { _priority = value; IsDirty = true; } }
    }

    /// <summary>Progress in the range [0,1].</summary>
    public float Progress { get; protected set; }

    /// <summary>True once the operation has reached a terminal state (Succeeded or Failed).</summary>
    public bool IsDone => _status == EOperationStatus.Succeeded || _status == EOperationStatus.Failed;

    /// <summary>The error message if the operation failed, otherwise null.</summary>
    public string? Error => _error;

    /// <summary>Current status.</summary>
    public EOperationStatus Status => _status;

    /// <summary>Completed event. If the op is already done, the handler runs immediately.</summary>
    public event Action<AsyncOperationBase> Completed
    {
        add
        {
            if (value == null) return;
            if (IsDone) { InvokeHandler(value); return; }
            if (_completedSingle == null) { _completedSingle = value; return; }
            _completedList ??= new System.Collections.Generic.List<Action<AsyncOperationBase>>(4);
            _completedList.Add(value);
        }
        remove
        {
            if (value == null) return;
            if (_completedSingle == value) _completedSingle = null;
            else _completedList?.Remove(value);
        }
    }

    /// <summary>Block the calling thread until the operation completes. For sync callers and the
    /// re-entrant loader-thread case. Avoid on the main render thread in shipped games.</summary>
    public void WaitForCompletion()
    {
        if (_status == EOperationStatus.None) StartOperation();

        if (!IsWaitForCompletion)
        {
            IsWaitForCompletion = true;
            try
            {
                if (!IsDone)
                {
                    // Default synchronous wait: drive the scheduler until this op completes. Subclasses
                    // MAY override InternalWaitForCompletion for a tighter loop (e.g. a download op that
                    // blocks on its own I/O), but most ops just need the scheduler to keep pumping.
                    try { InternalWaitForCompletion(); }
                    catch (NotImplementedException) { WaitForCompletionViaScheduler(); }
                }
                if (!IsDone)
                {
                    _error = $"Operation '{GetType().Name}' did not complete during synchronous wait.";
                    _status = EOperationStatus.Failed;
                }
            }
            catch (Exception ex)
            {
                _error = ex.ToString();
                _status = EOperationStatus.Failed;
            }
            finally
            {
                FinalizeOperation();
            }
        }
    }

    /// <summary>Pump the AsyncOperationSystem until this op completes. Used as the default
    /// synchronous-wait strategy. Bounded to avoid infinite loops on stuck ops.</summary>
    private void WaitForCompletionViaScheduler()
    {
        for (int i = 0; i < 100000 && !IsDone; i++)
            AsyncOperationSystem.Update();
    }

    // ---- Scheduler entry points (internal) ----

    internal void StartOperation()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _executing, 1, 0) != 0) return;
        try
        {
            if (_status != EOperationStatus.None) return;
            _status = EOperationStatus.Processing;
            try
            {
                InternalStart();
            }
            catch (Exception ex)
            {
                _error = ex.ToString();
                _status = EOperationStatus.Failed;
            }
            if (IsDone) FinalizeOperation();
        }
        finally { System.Threading.Volatile.Write(ref _executing, 0); }
    }

    internal void UpdateOperation()
    {
        // Deserialization can synchronously request another asset and pump this scheduler again.
        // Never run the outer provider twice, including from the background asset loader.
        if (System.Threading.Interlocked.CompareExchange(ref _executing, 1, 0) != 0) return;
        try
        {
            if (!IsDone)
            {
                try { InternalUpdate(); }
                catch (Exception ex)
                {
                    _error = ex.ToString();
                    _status = EOperationStatus.Failed;
                }
            }
            if (IsDone && !IsFinalized) FinalizeOperation();
        }
        finally { System.Threading.Volatile.Write(ref _executing, 0); }
    }

    internal void AbortOperation()
    {
        if (!IsDone)
        {
            _error = "Operation was aborted.";
            _status = EOperationStatus.Failed;
        }
        FinalizeOperation();
    }

    // ---- Subclass overrides ----

    protected abstract void InternalStart();
    protected abstract void InternalUpdate();
    protected virtual void InternalDispose() { }
    protected virtual void InternalWaitForCompletion() =>
        throw new NotImplementedException($"{GetType().Name} does not override InternalWaitForCompletion; the scheduler-pump fallback will be used.");

    /// <summary>Mark the operation as succeeded.</summary>
    protected void SetResult()
    {
        if (IsDone) throw new InvalidOperationException($"Operation '{GetType().Name}' already completed.");
        _status = EOperationStatus.Succeeded;
    }

    /// <summary>Mark the operation as failed with <paramref name="error"/>.</summary>
    protected void SetError(string error)
    {
        if (IsDone) throw new InvalidOperationException($"Operation '{GetType().Name}' already completed.");
        _error = error;
        _status = EOperationStatus.Failed;
    }

    /// <summary>Combine a sub-operation's progress and failure into this one. Returns true if the
    /// sub-operation is done (caller should then advance its own state machine).</summary>
    protected bool ProcessSubOperation(AsyncOperationBase sub, float weight)
    {
        if (!sub.IsDone) { Progress = sub.Progress * weight; return false; }
        if (sub.Status == EOperationStatus.Failed) { SetError(sub.Error ?? "sub-operation failed"); return true; }
        return true;
    }

    // ---- Finalization ----

    /// <summary>
    /// Hot-reload: strip <see cref="Completed"/> handlers whose delegate is (directly or through an
    /// XTask completion-source chain) bound to the collectible script assembly. The scheduler's
    /// static operation list otherwise roots such handlers until the op finishes, pinning the whole
    /// AssemblyLoadContext. Engine-owned handlers are kept.
    /// </summary>
    internal void RemoveCollectibleCompletedHandlers()
    {
        if (_completedSingle != null && XEngine.Async.Internal.CollectibleBinding.IsChainBound(_completedSingle, null))
            _completedSingle = null;
        if (_completedList != null)
            _completedList.RemoveAll(static h => XEngine.Async.Internal.CollectibleBinding.IsChainBound(h, null));
    }

    private void FinalizeOperation()
    {
        if (IsFinalized) return;
        IsFinalized = true;

        // Fire completion handlers before resolving the awaiter so synchronous event subscribers
        // observe the result at the same point awaiters do.
        var single = _completedSingle;
        _completedSingle = null;
        var list = _completedList;
        _completedList = null;

        if (single != null) InvokeHandler(single);
        if (list != null)
        {
            foreach (var h in list) InvokeHandler(h);
            list.Clear();
        }

        _tcs?.TrySetResult(IsDone);
        try { InternalDispose(); } catch { /* swallow dispose errors */ }
    }

    private void InvokeHandler(Action<AsyncOperationBase> handler)
    {
        try { handler(this); }
        catch (Exception ex) { Runtime.Debug.LogError($"Exception in completion callback: {ex}"); }
    }

    /// <summary>Lazy TaskCompletionSource backing GetAwaiter. Created on first await.</summary>
    internal TaskCompletionSource<bool> Tcs => _tcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- IComparable / IEnumerator (for scheduler priority + await compat) ----

    public int CompareTo(AsyncOperationBase? other) => other == null ? 1 : other._priority.CompareTo(_priority);

    // IEnumerator: a completed op yields nothing; the scheduler uses MoveNext as a "still running" probe.
    object? IEnumerator.Current => null;
    bool IEnumerator.MoveNext() => !IsDone;
    void IEnumerator.Reset() { }

    /// <summary>Get an awaiter so this operation can be awaited with `await op`. Bridges to the
    /// TaskCompletionSource in <see cref="Tcs"/>.</summary>
    public System.Runtime.CompilerServices.TaskAwaiter<bool> GetAwaiter() => Tcs.Task.GetAwaiter();
}
