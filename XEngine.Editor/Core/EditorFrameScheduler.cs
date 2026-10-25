using System;

namespace XEngine.Editor.Core;

[Flags]
public enum EditorSurfaceDirtyReason
{
    None = 0,
    Input = 1 << 0,
    Layout = 1 << 1,
    Selection = 1 << 2,
    AssetData = 1 << 3,
    Window = 1 << 4,
    SceneCamera = 1 << 5,
    GameView = 1 << 6,
    Animation = 1 << 7,
    Theme = 1 << 8,
    ExplicitRepaint = 1 << 9,
}

public enum EditorSurfaceSchedulerState
{
    Unscheduled,
    Interactive,
    Animated,
    Idle,
    Unfocused,
}

/// <summary>
/// Allocation-free state machine for one editor presentation surface. Rendering integration is
/// intentionally separate so existing Scene/Game View invalidation producers remain authoritative.
/// </summary>
public sealed class EditorFrameScheduler
{
    private bool _focused = true;
    private bool _interactiveRequested;
    private bool _repaintQueued;
    private double _animationUntil;
    private EditorSurfaceDirtyReason _queuedReasons;

    public EditorSurfaceSchedulerState State { get; private set; } = EditorSurfaceSchedulerState.Idle;
    public EditorSurfaceDirtyReason LastRenderReasons { get; private set; }

    public void SetFocused(bool focused)
    {
        if (_focused == focused) return;
        _focused = focused;
        if (focused)
            Invalidate(EditorSurfaceDirtyReason.Window);
    }

    public void Invalidate(EditorSurfaceDirtyReason reason)
    {
        _queuedReasons |= reason;
        _repaintQueued = true;
    }

    public void RequestInteractiveFrame() => _interactiveRequested = true;

    public void ReleaseInteractiveFrame() => _interactiveRequested = false;

    /// <summary>
    /// Per-pass truth form of the Request/Release pair: callers must state whether the condition
    /// still holds this pass. A sticky <c>RequestInteractiveFrame</c> on a detached window renders
    /// that surface every pass even after the pointer leaves it, until visibility bypasses the gate.
    /// </summary>
    public void SetInteractiveActive(bool active)
        => _interactiveRequested = active;

    public void RegisterAnimation(double until)
    {
        if (until > _animationUntil)
            _animationUntil = until;
    }

    public bool ShouldRender(double now)
    {
        if (!_focused)
        {
            State = EditorSurfaceSchedulerState.Unfocused;
            return ConsumeQueuedRepaint();
        }

        if (_interactiveRequested)
        {
            State = EditorSurfaceSchedulerState.Interactive;
            LastRenderReasons = _queuedReasons;
            _queuedReasons = EditorSurfaceDirtyReason.None;
            _repaintQueued = false;
            return true;
        }

        if (now < _animationUntil)
        {
            State = EditorSurfaceSchedulerState.Animated;
            LastRenderReasons = _queuedReasons | EditorSurfaceDirtyReason.Animation;
            _queuedReasons = EditorSurfaceDirtyReason.None;
            _repaintQueued = false;
            return true;
        }

        State = EditorSurfaceSchedulerState.Idle;
        return ConsumeQueuedRepaint();
    }

    private bool ConsumeQueuedRepaint()
    {
        if (!_repaintQueued) return false;
        LastRenderReasons = _queuedReasons;
        _queuedReasons = EditorSurfaceDirtyReason.None;
        _repaintQueued = false;
        return true;
    }
}
