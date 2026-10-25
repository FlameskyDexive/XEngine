using System;

namespace XEngine.WebSurface;

/// <summary>
/// A platform backend drives the hosted browser and produces frames into the session's pool.
/// B1 ships only the CPU baseline contract; Windows SharedBuffer (B2), Browser (B3), Android
/// (B4) and ArkWeb (B5) implement this same interface. Implementations must respect a single
/// in-flight production request and must route every callback through the session so stale
/// generations and old page callbacks are dropped at the pool.
/// </summary>
public interface IWebSurfaceBackend : IDisposable
{
    WebSurfaceCapabilities Capabilities { get; }

    /// <summary>Raised when the backend cannot continue (page crash, navigation failure, OOM).</summary>
    event Action<string>? Failed;

    /// <summary>
    /// Begin loading the asset content. The backend calls <see cref="IWebSurfaceFrameSink"/>
    /// callbacks as frames arrive; completion is reported through <see cref="Ready"/>.
    /// </summary>
    void Load(WebSurfaceAsset asset, IWebSurfaceFrameSink sink);

    /// <summary>Raised once the first frame of a (re)load is committed.</summary>
    event Action? Ready;

    void Pause();
    void Resume();
    void Reload();

    /// <summary>Requests one frame production; returns false when a request is already in flight.</summary>
    bool RequestFrame();

    /// <summary>Pointer/keyboard forwarding (B6 completes text sessions; B1 carried pointer only).</summary>
    void SendPointer(WebSurfacePointerEvent pointerEvent) { }

    /// <summary>
    /// Forwards a synthesized keyboard event (the platform's native IME delivers composed text
    /// directly to the page; the engine only forwards discrete key events).
    /// </summary>
    void SendKeyboard(WebSurfaceKeyboardEvent keyboardEvent) { }
}

/// <summary>Keyboard event forwarded from WebSurfaceInput to the backend.</summary>
public readonly record struct WebSurfaceKeyboardEvent(
    WebSurfaceKeyKind Kind,
    string Key,
    char Character,
    WebSurfaceKeyModifiers Modifiers);

public enum WebSurfaceKeyKind : byte
{
    Down,
    Up,
}

[Flags]
public enum WebSurfaceKeyModifiers : byte
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Meta = 8,
}

/// <summary>Frame-production sink the backend writes into (the session's frame pool).</summary>
public interface IWebSurfaceFrameSink
{
    /// <summary>
    /// Begins one frame carrying the producer's monotonically increasing frame number; null when
    /// a production request is already in flight.
    /// </summary>
    WebSurfaceFrame? BeginFrame(ulong frameNumber, int width, int height, int stride, WebSurfaceAlphaMode alphaMode);

    /// <summary>Commits the frame; stale generations are rejected by the pool.</summary>
    bool Commit(WebSurfaceFrame frame);

    /// <summary>Drops the in-flight frame.</summary>
    void CancelFrame();
}

/// <summary>Pointer event forwarded from WebSurfaceInput to the backend.</summary>
public readonly record struct WebSurfacePointerEvent(
    WebSurfacePointerKind Kind,
    float X,
    float Y,
    int PointerId,
    float ScrollDelta,
    int Buttons);

public enum WebSurfacePointerKind : byte
{
    Move,
    Down,
    Up,
    Cancel,
    Scroll,
}
