using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace XEngine.WebSurface;

/// <summary>
/// A dedicated STA thread owning a hidden native window and a Win32 message pump. WebView2's
/// core hosting (environment, controller) is UI-affine: everything must be created and touched
/// on a thread that pumps messages. The editor/player main loop is not a message pump, so the
/// backend marshals every control operation here via a concurrent queue drained from the window
/// procedure, and a <see cref="SynchronizationContext"/> that makes async WebView2 continuations
/// return to this thread deterministically (without WinForms).
/// </summary>
internal sealed class WebView2HostThread : IDisposable
{
    private const uint WmAppDispatch = 0x8000; // WM_APP
    private const uint WmAppQuit = 0x8001;

    // The Win32 window procedure must be a stable static callback; keep the delegate rooted.
    private static readonly WndProcDelegate s_wndProc = WndProcRouter;

    private readonly string _windowTitle;
    private readonly ConcurrentQueue<Action> _dispatchQueue = new();
    private readonly ManualResetEventSlim _windowReady = new(false);

    private Thread? _thread;
    private nint _hwnd;
    private WebView2PumpSynchronizationContext? _syncContext;
    private bool _disposed;

    public WebView2HostThread(string windowTitle) => _windowTitle = windowTitle;

    /// <summary>The hidden host window handle (0 until the pump is running).</summary>
    public nint WindowHandle => _hwnd;

    /// <summary>Raised when an operation dispatched onto the pump threw.</summary>
    public event Action<Exception>? DispatchFailed;

    /// <summary>Starts the STA pump and waits until the host window exists.</summary>
    public void Start(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
            return;

        _windowReady.Reset();
        _thread = new Thread(state => Run(((int Width, int Height))state))
        {
            Name = "XEngine.WebSurface.WebView2",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start((width, height));
        if (!_windowReady.Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("WebView2 host thread did not create its window in time.");
    }

    /// <summary>Queues an operation onto the STA pump thread.</summary>
    public void Post(Action operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatchQueue.Enqueue(operation);
        if (_hwnd != 0)
            PostMessageW(_hwnd, WmAppDispatch, nint.Zero, nint.Zero);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_hwnd != 0)
            PostMessageW(_hwnd, WmAppQuit, nint.Zero, nint.Zero);
        _thread?.Join(TimeSpan.FromSeconds(5));
        _thread = null;
        _windowReady.Dispose();
    }

    private void Run((int Width, int Height) size)
    {
        s_activeInstance = this;
        _hwnd = CreateHiddenWindow(size.Width, size.Height);
        _windowReady.Set();
        if (_hwnd == 0)
            return;

        _syncContext = new WebView2PumpSynchronizationContext(_dispatchQueue, () => PostMessageW(_hwnd, WmAppDispatch, nint.Zero, nint.Zero));
        SynchronizationContext.SetSynchronizationContext(_syncContext);

        try
        {
            while (GetMessageW(out MSG message, nint.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
        }
        finally
        {
            s_activeInstance = null;
            SynchronizationContext.SetSynchronizationContext(null);
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }

    private void DrainQueue()
    {
        while (_dispatchQueue.TryDequeue(out Action? operation))
        {
            try
            {
                operation();
            }
            catch (Exception ex)
            {
                DispatchFailed?.Invoke(ex);
            }
        }
    }

    private nint CreateHiddenWindow(int width, int height)
    {
        // Never shown: a hidden top-level window (no WS_VISIBLE) with WS_EX_NOACTIVATE so it can
        // never steal focus from the editor or a game. WebView2 child windows render offscreen
        // underneath it as long as the controller reports IsVisible=true.
        const uint wsExNoActivate = 0x0800_0000;
        const uint wsExToolWindow = 0x0000_0080;

        nint instance = GetModuleHandleW(null);
        WNDCLASSW windowClass = new()
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            lpszClassName = "XEngineWebSurfaceHostWindow",
            hInstance = instance,
        };
        if (RegisterClassW(ref windowClass) == 0 && Marshal.GetLastWin32Error() != 1410) // 1410 = class already registered
            return 0;

        return CreateWindowExW(
            wsExNoActivate | wsExToolWindow,
            windowClass.lpszClassName,
            _windowTitle,
            0, // WS_OVERLAPPED, deliberately without WS_VISIBLE
            0, 0, width, height,
            nint.Zero, nint.Zero, instance, nint.Zero);
    }

    private static nint WndProcRouter(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmAppDispatch:
                s_activeInstance?.DrainQueue();
                return nint.Zero;
            case WmAppQuit:
                PostQuitMessage(0);
                return nint.Zero;
            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    // One host window per thread, and one thread per backend instance: the routing window
    // procedure dispatches to the instance that created the window on its thread.
    [ThreadStatic]
    private static WebView2HostThread? s_activeInstance;

    private sealed class WebView2PumpSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<Action> _queue;
        private readonly Action _notify;

        public WebView2PumpSynchronizationContext(ConcurrentQueue<Action> queue, Action notify)
        {
            _queue = queue;
            _notify = notify;
        }

        public override void Post(SendOrPostCallback callback, object? state) =>
            PostInternal(() => callback(state));

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (SynchronizationContext.Current == this)
            {
                callback(state);
                return;
            }
            using ManualResetEventSlim done = new(false);
            Exception? failure = null;
            PostInternal(() =>
            {
                try { callback(state); }
                catch (Exception ex) { failure = ex; }
                finally { done.Set(); }
            });
            done.Wait();
            if (failure is not null)
                throw failure;
        }

        private void PostInternal(Action action)
        {
            _queue.Enqueue(action);
            _notify();
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG message, nint window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    private delegate nint WndProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);
}
