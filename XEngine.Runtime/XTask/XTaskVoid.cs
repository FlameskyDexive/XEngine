// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.CompilerServices;

namespace XEngine.Async;

/// <summary>
/// The return type for fire-and-forget <c>async</c> methods. An <c>async XTaskVoid</c> method runs
/// its state machine like an <c>async XTask</c> method, but its result is never awaited: unhandled
/// exceptions are routed to <see cref="XTaskScheduler.UnobservedTaskException"/> instead of
/// propagating to the caller. Call <see cref="Forget"/> to mark intent at the call site.
/// </summary>
[AsyncMethodBuilder(typeof(AsyncXTaskVoidMethodBuilder))]
public readonly struct XTaskVoid
{
    /// <summary>No-op; expresses "I intentionally do not await this."</summary>
    public void Forget() { }
}
