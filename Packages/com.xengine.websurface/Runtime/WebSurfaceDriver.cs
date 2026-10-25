using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.UI;

namespace XEngine.WebSurface;

/// <summary>
/// MonoBehaviour shell that drives a bound <see cref="WebSurfaceFrameDriver"/> from the engine
/// update loop: schedules produce requests at the asset's framerate cap and presents the newest
/// frame into the <see cref="WebSurfaceGraphic"/>. Bind once after constructing the session and
/// backend; Dispose the session to tear everything down.
/// </summary>
public sealed class WebSurfaceDriver : MonoBehaviour
{
    [SerializeIgnore] private WebSurfaceFrameDriver? _driver;
    [SerializeIgnore] private WebSurfaceSession? _session;
    [SerializeIgnore] private WebSurfaceInput? _input;
    [SerializeIgnore] private WebSurfaceGraphic? _graphic;

    public WebSurfaceFrameDriver? Driver => _driver;

    /// <summary>The session this driver is bound to (for live diagnostics/tooling).</summary>
    public WebSurfaceSession? Session => _session;

    /// <summary>
    /// Input pipeline for the bound session: panel-local pointers map into the canvas viewport,
    /// keyboard is gated by focus (focused surfaces consume keys before XGUI navigation), and
    /// text sessions carry engine/platform IME edits.
    /// </summary>
    public WebSurfaceInput? Input => _input;

    /// <summary>Binds the driver to a session (and optionally its on-screen graphic).</summary>
    public void Bind(WebSurfaceSession session, WebSurfaceGraphic? graphic)
    {
        _session = session;
        _graphic = graphic;
        _driver = new WebSurfaceFrameDriver(session, graphic);
        _input = new WebSurfaceInput(
            () => session.Asset?.Quality.Width ?? 0,
            () => session.Asset?.Quality.Height ?? 0,
            session.Backend);
        _schedulerReset = true;
    }

    public void Unbind()
    {
        _graphic?.CancelWorldClick();
        _graphic = null;
        _input?.ReleaseCapture();
        _input = null;
        _driver = null;
        _session = null;
    }

    /// <summary>Number of Update ticks since bind (live check that the driver is being driven).</summary>
    public long TickCount { get; private set; }

    private bool _schedulerReset;

    public override void Update()
    {
        if (_driver is null)
            return;
        TickCount++;
        if (_schedulerReset)
        {
            _driver.Scheduler.Reset();
            _schedulerReset = false;
        }
        _driver.Tick(Time.UnscaledDeltaTime);
        _graphic?.WorldClickTick(_session?.Backend);
    }
}
