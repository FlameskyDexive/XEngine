namespace XEngine.Editor.GUI.Panels;

internal sealed class GameViewRenderSchedule
{
    private object? _lastScene;
    private long _lastSceneVersion = -1;
    private long _lastQualityVersion = -1;
    private int _lastWidth;
    private int _lastHeight;
    private long _lastViewVersion = -1;
    private bool _lastContinuousRendering;
    private bool _force = true;

    public bool ShouldRender(
        bool continuousRendering,
        object scene,
        long sceneVersion,
        long qualityVersion,
        int width,
        int height,
        long viewVersion = 0)
    {
        bool changed = _force
            || !ReferenceEquals(_lastScene, scene)
            || _lastSceneVersion != sceneVersion
            || _lastQualityVersion != qualityVersion
            || _lastWidth != width
            || _lastHeight != height
            || _lastViewVersion != viewVersion
            || _lastContinuousRendering != continuousRendering;

        if (!continuousRendering && !changed)
            return false;

        _force = false;
        _lastScene = scene;
        _lastSceneVersion = sceneVersion;
        _lastQualityVersion = qualityVersion;
        _lastWidth = width;
        _lastHeight = height;
        _lastViewVersion = viewVersion;
        _lastContinuousRendering = continuousRendering;
        return true;
    }

    public void Invalidate() => _force = true;
}
