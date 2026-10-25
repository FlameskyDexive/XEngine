using System;

namespace XEngine.WebSurface;

/// <summary>
/// B1 pointer path: collects UI-side pointer events, maps panel-local coordinates into the
/// surface's canvas viewport (covering scale, pivot offset and the UV rect actually shown) and
/// forwards them to the backend. Out-of-bounds positions are clamped into the viewport so a
/// drag that leaves the panel keeps dragging (pointer capture semantics); captures are released
/// on hide/pause/dispose.
/// </summary>
public sealed class WebSurfaceInput
{
    private readonly IWebSurfaceBackend? _backend;
    private readonly Func<int> _surfaceWidth;
    private readonly Func<int> _surfaceHeight;
    private int _capturedPointerId = -1;
    private bool _hasKeyboardFocus;

    /// <summary>UV rect of the surface actually displayed (sub-rect for pan/zoom later).</summary>
    public RectF UvRect { get; set; } = new(0f, 0f, 1f, 1f);

    public bool Captured => _capturedPointerId >= 0;

    /// <summary>
    /// True while the surface owns keyboard focus (B6). While set, keys the surface consumes
    /// (typing, navigation inside the page) must not also drive XGUI navigation — the owner
    /// queries <see cref="HasKeyboardFocus"/> before falling through to UI navigation.
    /// </summary>
    public bool HasKeyboardFocus => _hasKeyboardFocus;

    public WebSurfaceInput(Func<int> surfaceWidth, Func<int> surfaceHeight, IWebSurfaceBackend? backend = null)
    {
        _surfaceWidth = surfaceWidth;
        _surfaceHeight = surfaceHeight;
        _backend = backend;
    }

    /// <summary>Gives keyboard focus to the surface (a click into the panel typically does this).</summary>
    public void AcquireKeyboardFocus() => _hasKeyboardFocus = true;

    /// <summary>Releases keyboard focus (click outside, hide, pause, dispose).</summary>
    public void ReleaseKeyboardFocus()
    {
        _hasKeyboardFocus = false;
        _textSession?.End();
        _textSession = null;
    }

    private WebSurfaceTextSession? _textSession;

    /// <summary>The active text session for the focused editable element (null when none).</summary>
    public WebSurfaceTextSession? TextSession => _textSession;

    /// <summary>
    /// Begins a text session for the focused editable element (the platform then owns native
    /// IME; edits flow through the session). Ends any previous session first.
    /// </summary>
    public WebSurfaceTextSession BeginTextSession(string initialText, WebSurfaceTextRange initialSelection)
    {
        _textSession?.End();
        _textSession = WebSurfaceTextSession.Begin(initialText, initialSelection);
        return _textSession;
    }

    /// <summary>
    /// Forwards a keyboard event when the surface holds focus. Returns true when the key was
    /// forwarded (consumed by the surface — the caller must not let it fall through to XGUI
    /// navigation), false when there is no focus (the key stays with the UI).
    /// </summary>
    public bool ForwardKey(WebSurfaceKeyKind kind, string key, char character, WebSurfaceKeyModifiers modifiers)
    {
        if (!_hasKeyboardFocus)
            return false;
        _backend?.SendKeyboard(new WebSurfaceKeyboardEvent(kind, key, character, modifiers));
        return true;
    }

    /// <summary>
    /// Maps a panel-local point (x,y in panel pixels, panel of size panelW×panelH) into canvas
    /// pixels honoring UvRect, then forwards. Returns false when the point is outside and no
    /// capture is active.
    /// </summary>
    public bool ForwardPointer(WebSurfacePointerKind kind, float x, float y, float panelW, float panelH, int pointerId, float scrollDelta = 0, int buttons = 0)
    {
        bool outside = x < 0 || y < 0 || x > panelW || y > panelH;
        if (outside && !Captured && kind != WebSurfacePointerKind.Up)
            return false;

        if (kind == WebSurfacePointerKind.Down)
            _capturedPointerId = pointerId;
        else if (kind is WebSurfacePointerKind.Up or WebSurfacePointerKind.Cancel && _capturedPointerId == pointerId)
            _capturedPointerId = -1;

        float u = UvRect.MinX + Math.Clamp(x / MathF.Max(panelW, 1f), 0f, 1f) * UvRect.SizeX;
        float v = UvRect.MinY + Math.Clamp(y / MathF.Max(panelH, 1f), 0f, 1f) * UvRect.SizeY;

        int canvasX = (int)MathF.Round(u * _surfaceWidth());
        int canvasY = (int)MathF.Round(v * _surfaceHeight());

        _backend?.SendPointer(new WebSurfacePointerEvent(kind, canvasX, canvasY, pointerId, scrollDelta, buttons));
        return true;
    }

    /// <summary>Losing the surface (hide, pause, dispose) must cancel captures and text sessions.</summary>
    public void ReleaseCapture()
    {
        _capturedPointerId = -1;
        ReleaseKeyboardFocus();
    }
}

/// <summary>Minimal float rect (avoids pulling the engine geometry types into the package's pure layer).</summary>
public readonly record struct RectF(float MinX, float MinY, float SizeX, float SizeY);
