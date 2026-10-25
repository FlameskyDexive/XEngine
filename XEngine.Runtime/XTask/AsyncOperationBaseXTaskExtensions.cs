// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Runtime.AssetBundles;

namespace XEngine.Async;

// NOTE: AsyncOperationBase already exposes a GetAwaiter() bridging to a TaskCompletionSource<bool>.
// This AsXTask() extension is the v1 zero-churn integration: it lets new code await an operation as
// an XTask (with the rest of the XTask combinator/cancellation surface) WITHOUT modifying
// AsyncOperationBase itself. The deep integration (AsyncOperationBase : IXTaskSource, dropping the
// intermediate TaskCompletionSource) is a v2 candidate that touches the whole resource layer.

/// <summary>XTask bridge extensions for <see cref="AsyncOperationBase"/>.</summary>
public static class AsyncOperationBaseXTaskExtensions
{
    /// <summary>Await an <see cref="AsyncOperationBase"/> as an <see cref="XTask"/>.</summary>
    public static XTask AsXTask(this AsyncOperationBase op)
    {
        if (op == null) throw new ArgumentNullException(nameof(op));

        if (op.IsDone)
        {
            return op.Status == EOperationStatus.Failed
                ? XTask.FromException(new InvalidOperationException(op.Error))
                : XTask.CompletedTask;
        }

        var tcs = AutoResetXTaskCompletionSource.Create();
        op.Completed += OnCompleted;
        return tcs.Task;

        void OnCompleted(AsyncOperationBase completed)
        {
            if (completed.Status == EOperationStatus.Failed)
            {
                tcs.TrySetException(new InvalidOperationException(completed.Error));
            }
            else
            {
                tcs.TrySetResult();
            }
        }
    }
}
