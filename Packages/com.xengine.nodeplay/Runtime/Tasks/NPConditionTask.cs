namespace XEngine.NodePlay;

/// <summary>
/// An instant boolean query. The first evaluation runs <see cref="OnEnableCheck"/> once
/// subscribe/capture work), every evaluation runs <see cref="OnCheck"/>. Enable pairs with
/// disable at graph stop through <see cref="Disable"/>. <see cref="Invert"/> flips the answer.
/// </summary>
public abstract class NPConditionTask : NPTask
{
    public bool Invert;

    private bool _lazilyEnabled;

    public bool Check()
    {
        if (!_lazilyEnabled)
        {
            _lazilyEnabled = true;
            OnEnableCheck();
        }

        bool value = OnCheck();
        return Invert ? !value : value;
    }

    /// <summary>One-time setup before the first check.</summary>
    protected virtual void OnEnableCheck()
    {
    }

    /// <summary>The actual predicate.</summary>
    protected abstract bool OnCheck();

    /// <summary>Release subscriptions captured in OnEnableCheck (graph stop / unbind).</summary>
    public void Disable()
    {
        if (!_lazilyEnabled)
            return;
        _lazilyEnabled = false;
        OnDisableCheck();
        ResetForReuse();
    }

    protected virtual void OnDisableCheck()
    {
    }

    protected virtual void ResetForReuse()
    {
    }
}
