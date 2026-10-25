// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Async;

/// <summary>
/// Convenience base for non-pooled operator sources (e.g. <c>WhenAllPromise</c>,
/// <c>WhenAnyPromise</c>) that produce a void result. Embeds a <see cref="XTaskCompletionSourceCore{TResult}"/>
/// and delegates <see cref="IXTaskSource"/> to it. Pooled operator sources embed their own core so
/// they can implement <see cref="ITaskPoolNode{T}"/> and recycle on <c>GetResult</c>.
/// </summary>
public abstract class MoveNextSource : IXTaskSource
{
    protected XTaskCompletionSourceCore<AsyncUnit> core;

    public XTask Task => new(this, core.Version);

    public XTaskStatus GetStatus(short token) => core.GetStatus(token);

    public XTaskStatus UnsafeGetStatus() => core.UnsafeGetStatus();

    public void OnCompleted(Action<object> continuation, object state, short token)
        => core.OnCompleted(continuation, state, token);

    public void GetResult(short token) => core.GetResult(token);
}
