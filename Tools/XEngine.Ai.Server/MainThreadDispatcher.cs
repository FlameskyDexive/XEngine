// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Ai.Server;

/// <summary>
/// ★ Layer 2 (B3) — Marshals work onto the engine main loop. The MCP stdio server runs in a
/// background Task (async, reading stdin); engine state (Scene/GameObject/Debug) is only safe to touch
/// on the main loop. Tool handlers <see cref="InvokeOnMainThreadAsync{T}"/> to hop over, then return.
///
/// <para>The engine's per-frame update calls <see cref="Drain"/> (max 64 actions/frame, XMcp pattern)
/// to execute queued work. Until Drain is wired into the editor loop, the dispatcher still works for
/// tests (Drain can be called explicitly) — this is the B3 PoC seam.</para>
/// </summary>
public sealed class MainThreadDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private const int MaxDrainPerCall = 64;

    /// <summary>Queue an action for the main loop and return a task that completes when it runs.</summary>
    public Task InvokeOnMainThreadAsync(Action action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            try { action(); tcs.SetResult(true); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Queue work returning a value, completing the task with its result on the main loop.</summary>
    public Task<T> InvokeOnMainThreadAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>★ P1-4 — Queue async work returning a value. The async function is awaited on the main
    /// loop (it still occupies a Drain slot until it completes, but it doesn't block on a sync
    /// <c>.GetAwaiter().GetResult()</c>). Used by <c>runtime_eval</c> so a slow Roslyn compile doesn't
    /// do a sync-over-async block.</summary>
    public async Task<T> InvokeOnMainThreadAsync<T>(Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(async () =>
        {
            try { tcs.SetResult(await func().ConfigureAwait(false)); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Drain up to <see cref="MaxDrainPerCall"/> queued actions. Call this on the engine main
    /// loop each frame (e.g. from Game.BeginUpdate / EditorApplication.OnUpdate).</summary>
    public void Drain()
    {
        int n = 0;
        while (n < MaxDrainPerCall && _queue.TryDequeue(out Action? action))
        {
            action();
            n++;
        }
    }

    /// <summary>Approximate pending count (diagnostics).</summary>
    public int PendingCount => _queue.Count;
}
