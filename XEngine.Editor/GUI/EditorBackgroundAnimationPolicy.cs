namespace XEngine.Editor.GUI;

/// <summary>
/// Tracks whether decorative editor background animation may advance for the current surface.
/// </summary>
public sealed class EditorBackgroundAnimationPolicy
{
    public const float DefaultIdleTimeoutSeconds = 5f;

    private readonly float _idleTimeoutSeconds;
    private bool _wasForegroundSurface;
    private float _lastActivityTime;

    public EditorBackgroundAnimationPolicy(float idleTimeoutSeconds = DefaultIdleTimeoutSeconds)
    {
        _idleTimeoutSeconds = idleTimeoutSeconds;
    }

    public bool ShouldAdvance(
        bool animationEnabled,
        bool isFocused,
        bool isMinimized,
        bool receivedInput,
        float unscaledTime)
    {
        if (!animationEnabled || !isFocused || isMinimized)
        {
            _wasForegroundSurface = false;
            return false;
        }

        // A focus transition, fresh input, or a replaced TimeData source resumes immediately.
        if (!_wasForegroundSurface || receivedInput || unscaledTime < _lastActivityTime)
            _lastActivityTime = unscaledTime;

        _wasForegroundSurface = true;
        return unscaledTime - _lastActivityTime < _idleTimeoutSeconds;
    }
}
