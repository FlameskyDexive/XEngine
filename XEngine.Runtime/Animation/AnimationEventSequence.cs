// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Playables;

namespace XEngine.Animation;

/// <summary>
/// M49 — an ordered, sorted-by-time collection of normalized-time events with a distinct end event
/// (Animancer <c>AnimancerEvent.Sequence</c> equivalent). Events fire when the state's normalized
/// time crosses theirs, forward or backward, including across loop wraps (capped at
/// <see cref="MaximumFullLoopCount"/> full loops per frame). A <see cref="Version"/> counter lets
/// caches invalidate when the sequence is edited.
/// </summary>
public sealed class AnimationEventSequence
{
    /// <summary>Normalized time that means "end of the clip" while still firing forward crosses.</summary>
    public const float AlmostOne = 0.99999994f;

    /// <summary>Maximum full loops whose events can fire in a single frame (huge-dt guard).</summary>
    public const int MaximumFullLoopCount = 3;

    /// <summary>One timed callback. The optional name hash also routes to the Animator's named events.</summary>
    public struct Event
    {
        public float NormalizedTime;
        public int NameHash;      // 0 = anonymous.
        public Action? Callback;  // Null when only the named handler applies.
    }

    private List<Event> _events = new();
    private Event _endEvent;
    private bool _hasEndEvent;

    /// <summary>Incremented on every mutation so dispatch caches can invalidate.</summary>
    public int Version { get; private set; }

    /// <summary>Number of timed events (excluding the end event).</summary>
    public int Count => _events.Count;

    /// <summary>True when a distinct end event is set.</summary>
    public bool HasEndEvent => _hasEndEvent;

    /// <summary>The end event fires when normalized time crosses <see cref="AlmostOne"/> forward
    /// (or 0 backward) — after every other event of that cycle, like Animancer's End Event.</summary>
    public Event EndEvent => _endEvent;

    /// <summary>Read-only indexed access; events are kept sorted by normalized time.</summary>
    public Event GetEvent(int index) => _events[index];

    /// <summary>Adds a timed event, keeping the list sorted. Times outside [0,1] are clamped.</summary>
    public void Add(float normalizedTime, Action? callback = null, int nameHash = 0)
    {
        if (normalizedTime < 0f) normalizedTime = 0f;
        else if (normalizedTime > 1f) normalizedTime = 1f;

        var e = new Event { NormalizedTime = normalizedTime, NameHash = nameHash, Callback = callback };
        int i = _events.Count - 1;
        while (i >= 0 && _events[i].NormalizedTime > normalizedTime)
            i--;
        _events.Insert(i + 1, e);
        Version++;
    }

    /// <summary>Sets (or replaces) the distinct end event.</summary>
    public void SetEnd(Action? callback = null, int nameHash = 0)
    {
        _endEvent = new Event { NormalizedTime = AlmostOne, NameHash = nameHash, Callback = callback };
        _hasEndEvent = true;
        Version++;
    }

    /// <summary>Removes the end event.</summary>
    public void ClearEnd()
    {
        _hasEndEvent = false;
        Version++;
    }

    /// <summary>Removes all events including the end event.</summary>
    public void Clear()
    {
        _events.Clear();
        _hasEndEvent = false;
        Version++;
    }

    /// <summary>
    /// Invokes every event whose normalized time lies in (<paramref name="from"/>, <paramref name="to"/>]
    /// (forward) or [<paramref name="from"/>, <paramref name="to"/>) (backward), in firing order.
    /// The end event participates at <see cref="AlmostOne"/> (forward) / 0 (backward). Used by the
    /// dispatcher; direct callers own the invocation timing.
    /// </summary>
    public void CollectInRange(double from, double to, PlayableEventQueue queue)
    {
        const double boundaryEpsilon = 1e-5;
        bool forward = to > from;
        if (forward)
        {
            int n = _events.Count;
            for (int i = 0; i < n; i++)
            {
                Event e = _events[i];
                if (e.NormalizedTime > from + boundaryEpsilon && e.NormalizedTime <= to + boundaryEpsilon)
                    queue.Enqueue(e.Callback, e.NameHash);
            }
            if (_hasEndEvent && AlmostOne > from + boundaryEpsilon && AlmostOne <= to + boundaryEpsilon)
                queue.Enqueue(_endEvent.Callback, _endEvent.NameHash);
        }
        else
        {
            for (int i = _events.Count - 1; i >= 0; i--)
            {
                Event e = _events[i];
                if (e.NormalizedTime < from - boundaryEpsilon && e.NormalizedTime >= to - boundaryEpsilon)
                    queue.Enqueue(e.Callback, e.NameHash);
            }
            if (_hasEndEvent && 0d < from - boundaryEpsilon)
                queue.Enqueue(_endEvent.Callback, _endEvent.NameHash);
        }
    }
}

/// <summary>
/// M49 — the Animator's named-event table (Animancer <c>AnimancerGraph.Events</c> equivalent):
/// hash-keyed callback lists fired whenever any queued event carrying that name flushes. Bind once
/// at startup (<c>animator.Events.Add("Footstep", PlayStep)</c>), fire from any clip/transition.
/// </summary>
public sealed class AnimatorNamedEvents
{
    private Dictionary<int, List<Action>>? _handlers;

    /// <summary>Appends a handler for <paramref name="name"/>. Returns false for null/empty names.</summary>
    public bool Add(string name, Action handler)
    {
        if (string.IsNullOrEmpty(name) || handler == null)
            return false;
        _handlers ??= new Dictionary<int, List<Action>>();
        if (!_handlers.TryGetValue(AnimationNameHash.Hash(name), out List<Action>? list))
        {
            list = new List<Action>(2);
            _handlers[AnimationNameHash.Hash(name)] = list;
        }
        list.Add(handler);
        return true;
    }

    /// <summary>Removes every handler for <paramref name="name"/>.</summary>
    public void Remove(string name)
        => _handlers?.Remove(AnimationNameHash.Hash(name));

    /// <summary>Invokes handlers for <paramref name="nameHash"/>. Exceptions propagate to the queue's guard.</summary>
    internal void Invoke(int nameHash)
    {
        if (_handlers == null || !_handlers.TryGetValue(nameHash, out List<Action>? list))
            return;
        for (int i = 0; i < list.Count; i++)
            list[i].Invoke();
    }
}
