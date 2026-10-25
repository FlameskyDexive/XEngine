using System;

namespace XEngine.WebSurface;

/// <summary>
/// Owns one hosted web surface lifecycle: load → run → pause/resume/reload → dispose, one frame
/// pool and one backend. Every state change funnels through the machine so illegal sequences are
/// impossible, and every backend failure funnels here so a failed session can only be retried
/// (Reload) or disposed — never half-alive.
/// </summary>
public sealed class WebSurfaceSession : IWebSurfaceFrameSink, IDisposable
{
    private readonly WebSurfaceStateMachine _machine = new();
    private readonly WebSurfaceDiagnostics _diagnostics = new();
    private readonly WebSurfaceFramePool _pool;
    private IWebSurfaceBackend? _backend;
    private WebSurfaceAsset? _asset;
    private bool _disposed;

    public WebSurfaceSession()
        => _pool = new WebSurfaceFramePool(_diagnostics);

    public WebSurfaceSession(WebSurfaceDiagnostics diagnostics)
        => _pool = new WebSurfaceFramePool(diagnostics);

    public WebSurfaceStateMachine.State State => _machine.Current;
    public WebSurfaceDiagnostics Diagnostics => _diagnostics;
    public WebSurfaceCapabilities? Capabilities => _backend?.Capabilities;
    public WebSurfaceFramePool Pool => _pool;

    /// <summary>The asset this session was loaded with (null before Load).</summary>
    public WebSurfaceAsset? Asset => _asset;

    /// <summary>The driving backend (for live diagnostics; null before Load / after Dispose).</summary>
    public IWebSurfaceBackend? Backend => _backend;

    /// <summary>Backend failure diagnostic of the most recent failure (null while healthy).</summary>
    public string? LastFailure { get; private set; }

    public void Load(WebSurfaceAsset asset, IWebSurfaceBackend backend)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(backend);
        ValidateAsset(asset);

        _machine.Transition(WebSurfaceStateMachine.State.Loading);
        _asset = asset;
        _backend = backend;
        backend.Failed += OnBackendFailed;
        backend.Load(asset, this);
    }

    public void Run()
    {
        _machine.Transition(WebSurfaceStateMachine.State.Running);
        _backend!.Ready += OnReady;
    }

    public void Pause()
    {
        _machine.Transition(WebSurfaceStateMachine.State.Paused);
        _backend!.Pause();
    }

    public void Resume()
    {
        _machine.Transition(WebSurfaceStateMachine.State.Running);
        _backend!.Resume();
    }

    public void Reload()
    {
        // Reload bumps the generation: every frame of the old page/epoch is dropped.
        _pool.Resize();
        ClearFailure();
        _machine.Transition(WebSurfaceStateMachine.State.Reloading);
        _machine.Transition(WebSurfaceStateMachine.State.Loading);
        _backend!.Reload();
        _machine.Transition(WebSurfaceStateMachine.State.Running);
    }

    public bool RequestFrame() => _backend?.RequestFrame() ?? false;

    /// <summary>Takes the newest committed frame (or null); the caller must dispose the lease.</summary>
    public WebSurfaceFrame? TryAcquireLatest() => _pool.TryAcquireLatest();

    public void SendPointer(WebSurfacePointerEvent pointerEvent) => _backend?.SendPointer(pointerEvent);

    private void OnBackendFailed(string message)
    {
        LastFailure = message;
        if (_machine.Current is not WebSurfaceStateMachine.State.Disposed)
            _machine.Fail();
    }

    /// <summary>Clears the failure record on a deliberate reload so health checks stay honest.</summary>
    public void ClearFailure() => LastFailure = null;

    private void OnReady() { }

    private static void ValidateAsset(WebSurfaceAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.ContentEntry))
            throw new ArgumentException("WebSurface asset requires a content entry.");
        if (!asset.IsLocalContent && asset.Host is null)
            throw new ArgumentException($"Content entry is not a valid HTTPS URL: {asset.ContentEntry}");
        if (asset.Quality.Width is < 16 or > 4096 || asset.Quality.Height is < 16 or > 4096)
            throw new ArgumentException("WebSurface quality must be within 16..4096 pixels.");
    }

    // ---------------------------------------------------------------------------------
    // IWebSurfaceFrameSink — the backend writes frames through here; the pool enforces the
    // single-in-flight / stale-generation rules at this boundary.
    // ---------------------------------------------------------------------------------

    WebSurfaceFrame? IWebSurfaceFrameSink.BeginFrame(ulong frameNumber, int width, int height, int stride, WebSurfaceAlphaMode alphaMode) =>
        _pool.BeginFrame(frameNumber, width, height, stride, alphaMode);

    bool IWebSurfaceFrameSink.Commit(WebSurfaceFrame frame) => _pool.Commit(frame);

    void IWebSurfaceFrameSink.CancelFrame() => _pool.CancelWriting();

    public void Dispose()
    {
        if (_machine.Current is WebSurfaceStateMachine.State.Disposed)
            return;
        _machine.Transition(WebSurfaceStateMachine.State.Disposed);
        if (_backend is not null)
        {
            _backend.Failed -= OnBackendFailed;
            _backend.Dispose();
            _backend = null;
        }
        _pool.Dispose();
        GC.SuppressFinalize(this);
    }
}
