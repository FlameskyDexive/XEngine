namespace XEngine.NodePlay;

/// <summary>
/// A cooperative, potentially multi-frame behaviour unit. The owning node calls
/// <see cref="Tick"/> once per graph update; the task stays <see cref="IsRunning"/> until it calls
/// <see cref="EndAction"/>. Instant tasks (EndAction on the first tick) map straight to a Success.
/// Pause/resume propagates from the runner through <see cref="OnPauseChanged"/>.
/// </summary>
public abstract class NPActionTask : NPTask
{
    private bool _started;
    private bool _ended;
    private bool? _result;

    /// <summary>True between the first tick and EndAction.</summary>
    public bool IsRunning => _started && !_ended;

    /// <summary>Has this invocation reached an end; null while undecided/running.</summary>
    public bool? Result => _ended ? _result : null;

    /// <summary>Advance one graph tick. Bind must have succeeded beforehand.</summary>
    public void Tick()
    {
        if (_ended)
            return;

        if (!_started)
        {
            _started = true;
            OnExecute();
            if (!_ended)
                OnFirstFramePostExecute();
        }
        else
        {
            OnUpdate();
        }
    }

    protected virtual void OnFirstFramePostExecute()
    {
    }

    /// <summary>First-tick entry point.</summary>
    protected abstract void OnExecute();

    /// <summary>Subsequent ticks until EndAction. Default: stay running.</summary>
    protected virtual void OnUpdate()
    {
    }

    /// <summary>
    /// Terminate this action. Pass true/false for a decisive result — a running branch treats
    /// null as failure with "optional" semantics decided by callers that care.
    /// </summary>
    protected void EndAction(bool? success = true)
    {
        if (_ended)
            return;
        _result = success ?? false;
        _ended = true;
        OnStop(false);
    }

    /// <summary>Raised when the action stops for any reason (finished or interrupted).</summary>
    protected virtual void OnStop(bool interrupted)
    {
    }

    /// <summary>Runner pause/resume propagation.</summary>
    internal void SetPaused(bool paused) => OnPauseChanged(paused);

    protected virtual void OnPauseChanged(bool paused)
    {
    }

    /// <summary>
    /// Interrupt without a result change path: marks failure and raises OnStop(interrupted).
    /// Used by decorators (Interruptor/Guard) and graph stop.
    /// </summary>
    public void Abort()
    {
        if (_ended)
        {
            return;
        }

        _started = true;
        _ended = true;
        _result = false;
        OnStop(true);
    }

    /// <summary>Graph-driven delta (headless tests and editor stepping included).</summary>
    protected float DeltaOfGraph() => (OwnerSystem as NPGraphAsset)?.LastDelta ?? 1f / 60f;

    /// <summary>Prepare for a fresh run of the same configured task instance.</summary>
    public void ResetForReuse()
    {
        _started = false;
        _ended = false;
        _result = null;
        OnReset();
    }

    protected virtual void OnReset()
    {
    }
}
