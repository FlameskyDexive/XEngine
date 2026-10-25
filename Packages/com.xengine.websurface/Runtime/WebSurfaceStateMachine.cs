using System;

namespace XEngine.WebSurface;

/// <summary>
/// Session lifecycle: Idle → Loading → Running ⇄ Paused, Running → Reloading → Loading,
/// any state → Failed, everything → Disposed. Illegal transitions throw — callers (session,
/// backend callbacks) are engine-side and bugs there must be loud, not silently swallowed.
/// </summary>
public sealed class WebSurfaceStateMachine
{
    public enum State
    {
        Idle,
        Loading,
        Running,
        Paused,
        Reloading,
        Failed,
        Disposed,
    }

    private State _state = State.Idle;
    private readonly object _lock = new();

    public State Current { get { lock (_lock) return _state; } }

    /// <summary>Applies a lifecycle transition, or throws with a diagnostic message when illegal.</summary>
    public void Transition(State target)
    {
        lock (_lock)
        {
            if (!IsLegal(_state, target))
                throw new InvalidOperationException($"Illegal WebSurface session transition {_state} → {target}.");
            _state = target;
        }
    }

    /// <summary>Failure is legal from every live state (backend/page errors can surface anywhere).</summary>
    public void Fail() => Transition(State.Failed);

    public static bool IsLegal(State from, State to) => (from, to) switch
    {
        (State.Idle, State.Loading) => true,
        (State.Loading, State.Running) => true,
        (State.Loading, State.Failed) => true,
        (State.Running, State.Paused) => true,
        (State.Running, State.Reloading) => true,
        (State.Running, State.Failed) => true,
        (State.Paused, State.Running) => true,
        (State.Paused, State.Failed) => true,
        (State.Reloading, State.Loading) => true,
        (State.Reloading, State.Failed) => true,
        (State.Failed, State.Loading) => true,     // retry after a page/backend failure
        (State.Failed, State.Reloading) => true,   // session.Reload() from a failed state
        (State.Failed, State.Disposed) => true,
        (_, State.Disposed) => to == State.Disposed && from is not State.Disposed,
        _ => false,
    };
}
