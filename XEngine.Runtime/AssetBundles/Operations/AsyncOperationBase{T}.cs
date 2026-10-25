// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// Generic result-bearing async operation. Subclasses set <see cref="Result"/> before calling
// <see cref="SetResult"/>; awaiters and Completed subscribers read it after IsDone.

using System;
using System.Threading.Tasks;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// An <see cref="AsyncOperationBase"/> that carries a typed result. Subclasses must populate
/// <see cref="Result"/> before calling <see cref="AsyncOperationBase.SetResult"/>.
/// </summary>
public abstract class AsyncOperationBase<TResult> : AsyncOperationBase
{
    /// <summary>The operation's result. Valid once <see cref="AsyncOperationBase.IsDone"/> is true
    /// and <see cref="AsyncOperationBase.Status"/> is <see cref="EOperationStatus.Succeeded"/>.</summary>
    public TResult? Result { get; protected set; }

    /// <summary>Get an awaiter that yields the typed result (not just bool).</summary>
    public new System.Runtime.CompilerServices.TaskAwaiter<TResult?> GetAwaiter()
    {
        // Bridge to the base Tcs, then map bool -> Result.
        var tcs = Tcs;
        return ContinueWithResult(tcs);
    }

    private System.Runtime.CompilerServices.TaskAwaiter<TResult?> ContinueWithResult(TaskCompletionSource<bool> tcs)
    {
        var tcs2 = new TaskCompletionSource<TResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.Task.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                if (t.Exception != null) tcs2.SetException(t.Exception);
                else tcs2.SetException(new Exception("op failed"));
            }
            else if (t.IsCanceled) tcs2.SetCanceled();
            else tcs2.SetResult(Result);
        }, System.Threading.Tasks.TaskScheduler.Default);
        return tcs2.Task.GetAwaiter();
    }
}
