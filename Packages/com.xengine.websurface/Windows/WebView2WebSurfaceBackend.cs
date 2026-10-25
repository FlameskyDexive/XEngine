using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace XEngine.WebSurface;

/// <summary>
/// B2 Windows backend: hosts WebView2 in-process on a dedicated STA thread and receives page
/// frames through a <see cref="CoreWebView2SharedBuffer"/> (plain CPU memory shared with the
/// browser renderer — the WebGL readback target on the page side). A single produce token is
/// outstanding at any time (the <see cref="WebSurfaceFrameRelay"/> enforces it), the page only
/// captures when told to, and every delivered frame is copied once into the session's frame pool.
/// Navigation policy enforces the asset's host allow-list for HTTPS content; local content is
/// served through a virtual host mapped to the project content root. Renderer/browser process
/// failure raises <see cref="IWebSurfaceBackend.Failed"/>; a following <see cref="Reload"/>
/// re-creates the WebView2 environment from scratch.
/// </summary>
public sealed class WebView2WebSurfaceBackend : IWebSurfaceBackend
{
    /// <summary>
    /// Root directory local content entries resolve against (the project root for editor and
    /// player). Entry paths outside the root are rejected before any navigation happens.
    /// </summary>
    public string ContentRoot { get; init; } = AppContext.BaseDirectory;

    /// <summary>Virtual host name local content is mapped to (always on the navigation allow-list).</summary>
    public const string LocalVirtualHost = "websurface.local";

    private const string UserDataFolderName = "xengine-websurface";

    private readonly object _gate = new();
    private WebSurfaceDiagnostics? _diagnostics;
    private WebSurfaceFrameRelay? _relay;
    private WebView2HostThread? _hostThread;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2SharedBuffer? _sharedBuffer;
    private WebSurfaceAsset? _asset;
    private volatile bool _bufferAttached;
    private volatile bool _paused;
    private volatile bool _failed;
    private bool _readyRaised;
    private bool _disposed;

    public WebSurfaceCapabilities Capabilities { get; } = WebSurfaceCapabilities.CpuBaseline("Windows");

    /// <summary>One-line internal state for live diagnostics (tooling/acceptance).</summary>
    public string DebugState => $"attached={_bufferAttached} paused={_paused} failed={_failed} "
        + $"buffer={(_sharedBuffer is not null)} controller={(_controller is not null)} "
        + $"environment={(_environment is not null)} producing={_relay?.IsProducing.ToString() ?? "n/a"}";

    public event Action<string>? Failed;
    public event Action? Ready;

    // -------------------------------------------------------------------------------------
    // Loading
    // -------------------------------------------------------------------------------------

    public void Load(WebSurfaceAsset asset, IWebSurfaceFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(sink);

        _asset = asset;
        _diagnostics = sink is WebSurfaceSession session
            ? session.Diagnostics
            : new WebSurfaceDiagnostics(); // off-session sinks still need a counter target
        WebSurfaceAlphaMode alphaMode = asset.TransparentBackground
            ? WebSurfaceAlphaMode.Straight
            : WebSurfaceAlphaMode.Opaque;
        _relay = new WebSurfaceFrameRelay(
            sink, _diagnostics, asset.Quality.Width, asset.Quality.Height, alphaMode);

        _hostThread = new WebView2HostThread("XEngine WebSurface (WebView2)");
        _hostThread.DispatchFailed += ex => Fail("webview_dispatch_failed: " + ex.Message);
        _hostThread.Start(asset.Quality.Width, asset.Quality.Height);
        _hostThread.Post(() => _ = InitializeAsync(asset));
    }

    private async Task InitializeAsync(WebSurfaceAsset asset)
    {
        try
        {
            string userDataFolder = Path.Combine(Path.GetTempPath(), UserDataFolderName, "WebView2");
            _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);

            nint hwnd = _hostThread?.WindowHandle ?? 0;
            if (hwnd == 0)
            {
                Fail("webview_host_window_missing");
                return;
            }
            _controller = await _environment.CreateCoreWebView2ControllerAsync(hwnd);
            CoreWebView2 core = _controller.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = true;

            core.WebMessageReceived += OnWebMessageReceived;
            core.ProcessFailed += OnProcessFailed;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;

            // The bridge installs itself into every new document; buffer hand-off happens in
            // response to the bridge's ready message (single code path for load and reload).
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(WebView2BridgeScript.Script);

            core.SetVirtualHostNameToFolderMapping(
                LocalVirtualHost,
                Path.GetFullPath(ContentRoot),
                CoreWebView2HostResourceAccessKind.Allow);

            _controller.Bounds = new System.Drawing.Rectangle(0, 0, asset.Quality.Width, asset.Quality.Height);
            _controller.IsVisible = !_paused;

            core.Navigate(WebSurfaceNavigationPolicy.ResolveTarget(asset, ContentRoot));
        }
        catch (Exception ex)
        {
            Fail("webview_init_failed: " + ex.Message);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        WebSurfaceAsset? asset = _asset;
        if (asset is null)
            return;

        WebSurfaceNavigationDecision decision = WebSurfaceNavigationPolicy.DecideNavigation(asset, e.Uri);
        if (!decision.Allow)
        {
            e.Cancel = true;
            Fail(decision.BlockedReason ?? "navigation_blocked");
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
            Fail("navigation_failed: " + e.WebErrorStatus);
    }

    // -------------------------------------------------------------------------------------
    // Page messages → relay
    // -------------------------------------------------------------------------------------

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? eventType = WebSurfacePageProtocol.TryParseEvent(
            e.WebMessageAsJson, out ulong frameNumber, out double pageMs, out string? message);

        switch (eventType)
        {
            case "ready":
                // A fresh document is up: hand it a fresh shared buffer (the previous buffer's
                // ArrayBuffer died with the old document, and a buffer must not be re-posted).
                _bufferAttached = false;
                PostFreshSharedBuffer();
                break;

            case "buffer":
                _bufferAttached = true;
                break;

            case "frame":
                OnPageFrameReady(frameNumber, pageMs);
                break;

            case "error":
                Fail("page_error: " + (message ?? "unknown"));
                break;

            default:
                break; // foreign or future message: ignored by contract
        }
    }

    private void PostFreshSharedBuffer()
    {
        CoreWebView2? core = _controller?.CoreWebView2;
        WebSurfaceAsset? asset = _asset;
        CoreWebView2Environment? environment = _environment;
        if (core is null || asset is null || environment is null)
            return;

        try
        {
            _sharedBuffer?.Dispose();
            long bytes = (long)asset.Quality.Width * asset.Quality.Height * 4;
            _sharedBuffer = environment.CreateSharedBuffer((ulong)bytes);
            core.PostSharedBufferToScript(
                _sharedBuffer,
                CoreWebView2SharedBufferAccess.ReadWrite,
                WebSurfacePageProtocol.SharedBufferHandoff(asset.Quality.Width, asset.Quality.Height));
        }
        catch (Exception ex)
        {
            Fail("shared_buffer_post_failed: " + ex.Message);
        }
    }

    private void OnPageFrameReady(ulong frameNumber, double pageMs)
    {
        WebSurfaceFrameRelay? relay = _relay;
        CoreWebView2SharedBuffer? buffer = _sharedBuffer;
        if (relay is null || buffer is null)
            return;

        bool committed = relay.OnFrameReady(frameNumber, pageMs, frame =>
            CopySharedBufferIntoFrame(buffer, frame));
        if (committed && !_readyRaised)
        {
            _readyRaised = true;
            Ready?.Invoke();
        }
    }

    private static unsafe void CopySharedBufferIntoFrame(CoreWebView2SharedBuffer buffer, WebSurfaceFrame frame)
    {
        uint bytes = checked((uint)(frame.Width * frame.Height * 4));
        fixed (byte* destination = frame.Pixels)
        {
            Buffer.MemoryCopy((void*)buffer.Buffer, destination, bytes, bytes);
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        // Renderer/browser death: drop everything WebView2-owned so the next Reload rebuilds it.
        _bufferAttached = false;
        _environment = null;
        _controller = null;
        _sharedBuffer = null;
        _relay?.CancelProduce();
        Fail($"webview_process_failed: {e.ProcessFailedKind} exit={e.ExitCode}");
    }

    // -------------------------------------------------------------------------------------
    // Session control
    // -------------------------------------------------------------------------------------

    public bool RequestFrame()
    {
        if (_disposed || _paused || _failed || !_bufferAttached)
            return false;

        WebSurfaceFrameRelay? relay = _relay;
        if (relay is null)
            return false;

        ulong? token = relay.BeginProduce();
        if (token is null)
            return false;

        _hostThread?.Post(() =>
        {
            try
            {
                _controller?.CoreWebView2.PostWebMessageAsJson(
                    WebSurfacePageProtocol.Produce(token.Value));
            }
            catch (Exception ex)
            {
                relay.CancelProduce();
                Fail("produce_post_failed: " + ex.Message);
            }
        });
        return true;
    }

    public void Pause()
    {
        _paused = true;
        _relay?.CancelProduce();
        _hostThread?.Post(() => _controller?.LetVisible(false));
    }

    public void Resume()
    {
        _paused = false;
        _hostThread?.Post(() => _controller?.LetVisible(true));
    }

    public void Reload()
    {
        _bufferAttached = false;
        _relay?.CancelProduce();
        _readyRaised = false;
        WebSurfaceAsset? asset = _asset;
        _hostThread?.Post(() =>
        {
            try
            {
                if (_controller is null)
                {
                    // After a process failure the whole WebView2 stack is rebuilt (A1b pattern).
                    if (asset is not null)
                        _ = InitializeAsync(asset);
                }
                else
                {
                    _controller.CoreWebView2.Reload();
                }
            }
            catch (Exception ex)
            {
                Fail("reload_failed: " + ex.Message);
            }
        });
    }

    public void SendPointer(WebSurfacePointerEvent pointerEvent)
    {
        PostInputMessage(new
        {
            type = WebSurfacePageProtocol.MessageType,
            @event = "input",
            kind = pointerEvent.Kind.ToString().ToLowerInvariant(),
            x = pointerEvent.X,
            y = pointerEvent.Y,
            pointerId = pointerEvent.PointerId,
            pointerType = "mouse",
            scrollDelta = pointerEvent.ScrollDelta,
            buttons = pointerEvent.Buttons,
        });
    }

    public void SendKeyboard(WebSurfaceKeyboardEvent keyboardEvent)
    {
        PostInputMessage(new
        {
            type = WebSurfacePageProtocol.MessageType,
            @event = "input",
            kind = "key",
            keyKind = keyboardEvent.Kind.ToString().ToLowerInvariant(),
            key = keyboardEvent.Key,
            @char = keyboardEvent.Character == '\0' ? string.Empty : keyboardEvent.Character.ToString(),
            modifiers = (int)keyboardEvent.Modifiers,
        });
    }

    private void PostInputMessage(object message)
    {
        // B6: pointer and keyboard input forward to the page as DOM events (the bridge
        // dispatches); IME itself stays native to the browser. Unknown messages are ignored
        // by the bridge contract.
        string json = System.Text.Json.JsonSerializer.Serialize(message);
        _hostThread?.Post(() =>
        {
            try { _controller?.CoreWebView2.PostWebMessageAsJson(json); }
            catch { /* host shutting down */ }
        });
    }

    // -------------------------------------------------------------------------------------
    // Teardown
    // -------------------------------------------------------------------------------------

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _relay?.Dispose();
        _hostThread?.Post(Teardown);
        _hostThread?.Dispose();
        _hostThread = null;
        _sharedBuffer = null;
        _relay = null;
    }

    private void Teardown()
    {
        if (_controller is not null)
        {
            CoreWebView2 core = _controller.CoreWebView2;
            core.WebMessageReceived -= OnWebMessageReceived;
            core.ProcessFailed -= OnProcessFailed;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            _controller.Close();
            _controller = null;
        }
        _environment = null;
        _sharedBuffer?.Dispose();
        _sharedBuffer = null;
    }

    private void Fail(string diagnostic)
    {
        if (_failed)
            return;
        _failed = true;
        Failed?.Invoke(diagnostic);
    }
}

internal static class WebView2ControllerExtensions
{
    /// <summary>Sets controller visibility, tolerating a torn-down controller.</summary>
    public static void LetVisible(this CoreWebView2Controller controller, bool visible)
    {
        try
        {
            controller.IsVisible = visible;
        }
        catch { /* controller already closed */ }
    }
}
