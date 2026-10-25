// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Playables;

/// <summary>
/// M49 — deferred event invocation queue for playable graphs (Animancer's Invoker equivalent):
/// events detected during evaluation are enqueued and invoked only after the graph's outputs have
/// applied (end of <see cref="PlayableGraph.EvaluateInternal"/>), so handlers observe the settled
/// frame and may safely restructure the graph without re-entering a mid-evaluation walk.
/// Animation layers route an optional name hash through <c>NamedRouter</c> (the owning Animator's
/// named-event table) at flush time. Steady-state zero allocation (persistent buffer, Clear after
/// flush).
/// </summary>
public sealed class PlayableEventQueue
{
    private struct Pending
    {
        public Action? Callback;
        public int NameHash;
    }

    private readonly List<Pending> _pending = new();

    private PlayableEventQueue() { }

    /// <summary>The shared queue every playable evaluation enqueues into.</summary>
    public static readonly PlayableEventQueue Shared = new();

    /// <summary>
    /// Named-event router active for the current evaluation (set by the owning Animator around its
    /// graph Evaluate; standalone graphs flush without one). Consumed at flush, not stored.
    /// </summary>
    public Action<int>? NamedRouter;

    /// <summary>Number of invocations waiting for the next flush.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Enqueues one crossed event. Called from dispatch during evaluation (hot path, no alloc).</summary>
    internal void Enqueue(Action? callback, int nameHash)
        => _pending.Add(new Pending { Callback = callback, NameHash = nameHash });

    /// <summary>
    /// Invokes every queued event now, then clears. Named events also fire the router. Exceptions in
    /// one handler do not stop the rest.
    /// </summary>
    public void Flush(Action<int>? namedRouter)
    {
        int n = _pending.Count;
        if (n == 0)
            return;

        for (int i = 0; i < n; i++)
        {
            Pending p = _pending[i];
            try
            {
                p.Callback?.Invoke();
                if (p.NameHash != 0)
                    namedRouter?.Invoke(p.NameHash);
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogError($"[PlayableEvent] handler threw: {ex.Message}\n{ex.StackTrace}");
            }
        }
        _pending.Clear();
    }

    /// <summary>Drops every queued event without invoking (teardown paths).</summary>
    public void Clear() => _pending.Clear();
}
