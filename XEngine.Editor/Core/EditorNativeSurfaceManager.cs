using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Silk.NET.Core.Contexts;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

using XEngine.Runtime;
using XEngine.Runtime.Backends.OpenGL;
using XEngine.Runtime.GUI;
using XEngine.Runtime.RHI;
using XEngine.OrigamiUI;
using XEngine.Editor.Theming;
using XEngine.Vector;

namespace XEngine.Editor.Core;

/// <summary>Creates and tears down native windows for workspace surface hosts.</summary>
internal sealed class EditorNativeSurfaceManager : IDisposable
{
    private readonly EditorWorkspace _workspace;
    private readonly IWindow _mainWindow;
    private readonly DockDragController _dockDragController;
    private readonly HashSet<int> _closingHosts = new();
    private readonly List<int> _pendingNativeCloses = new();
    private readonly List<EditorSurfaceHost> _pendingHosts = new();
    private readonly List<EditorSurfaceHost> _pumpHostsScratch = new();
    private readonly Dictionary<int, EditorSurfaceInput> _inputContexts = new();
    private EditorSurfaceHost? _externalDragSourceHost;
    private readonly HashSet<int> _focusedSecondaryHosts = new();
    private EditorSurfaceHost? _mainHost;
    private bool _mainWindowFocused = true;
    private bool _disposing;

    private const int VkLButton = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Win32Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(nint window, out Win32Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(nint window, ref Win32Point point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Win32Point point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint gaFlags);

    private const uint GaRoot = 2;

    public EditorNativeSurfaceManager(EditorWorkspace workspace, IWindow mainWindow)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _dockDragController = new DockDragController(workspace);
        XEngine.Runtime.Window.PostPresent += ProcessPendingHosts;
    }

    public bool TryCreate(EditorSurfaceHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.HasNativeWindow) return true;

        if (host.Id == 0)
        {
            IWindowSurface mainSurface = new SilkWindowSurface(_mainWindow);
            Graphics.TryGetPrimaryPresentationSurface(out PresentationSurfaceId primary);
            host.AttachNativeWindow(_mainWindow, mainSurface, primary);
            EditorWindowPlacement.Apply(host, _mainWindow);
            ConfigureDockDrag(host);
            TrackMainHost(host);
            return true;
        }

        if (!Graphics.SupportsPresentationSurfaces)
        {
            Runtime.Debug.LogWarning("The active graphics backend does not support detached editor windows.");
            return false;
        }

        _pendingHosts.Add(host);
        return true;
    }

    public bool IsWindowGroupActive
    {
        get
        {
            nint foreground = 0;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                foreground = GetForegroundWindow();

            // Stack-allocate a small handle list; editor window counts stay tiny.
            int count = _workspace.Hosts.Count;
            Span<nint> handles = count <= 16 ? stackalloc nint[count] : new nint[count];
            for (int i = 0; i < count; i++)
                handles[i] = GetOsWindowHandle(_workspace.Hosts[i].NativeWindow);

            return ComputeWindowGroupActive(
                foreground, handles, _mainWindowFocused, _focusedSecondaryHosts.Count > 0);
        }
    }

    /// <summary>
    /// Foreground HWND match wins. If the RHI <c>NativeHandle</c> is a DXGI/GL surface
    /// that is not the GLFW top-level HWND, Silk's own FocusChanged flags still keep
    /// Preferences / Package Manager / Build Project in the active group. Returning
    /// false here used to idle every surface so menu/dropdown catchers ate all clicks.
    /// </summary>
    internal static bool ComputeWindowGroupActive(
        nint foreground,
        ReadOnlySpan<nint> hostOsHandles,
        bool mainFocused,
        bool anySecondaryFocused)
    {
        if (foreground != nint.Zero)
        {
            for (int i = 0; i < hostOsHandles.Length; i++)
            {
                nint handle = hostOsHandles[i];
                if (handle != nint.Zero && handle == foreground)
                    return true;
            }
        }

        return mainFocused || anySecondaryFocused;
    }

    // A hidden GLFW window can gain focus during show, before HookWindow subscribes.
    // An available foreground HWND is authoritative for one surface, even if a callback was missed.
    internal static bool ComputeSurfaceFocused(nint foreground, nint hostHandle, bool callbackFocused)
        => foreground != nint.Zero && hostHandle != nint.Zero ? foreground == hostHandle : callbackFocused;

    /// <summary>
    /// Top-level OS window for hit-testing and focus. Do not use
    /// <see cref="IWindowSurface.NativeHandle"/> — that prefers Silk <c>DXHandle</c>,
    /// which is not always the GLFW HWND that <c>GetForegroundWindow</c> / <c>WindowFromPoint</c> return.
    /// </summary>
    internal static nint GetOsWindowHandle(IWindow? window)
    {
        if (window == null) return 0;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            nint hwnd = window.Native?.Win32?.Hwnd ?? 0;
            if (hwnd != 0) return hwnd;
            hwnd = window.Native?.Win32?.Item1 ?? 0;
            if (hwnd != 0) return hwnd;
        }

        return window.Handle;
    }

    /// <summary>Cursor is over the main editor HWND or any detached host.</summary>
    public bool IsPointerOverAnyEditorWindow
    {
        get
        {
            if (!TryGetTopWindowAtCursor(out nint root))
                return false;

            int count = _workspace.Hosts.Count;
            Span<nint> secondary = count <= 16 ? stackalloc nint[count] : new nint[count];
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                EditorSurfaceHost host = _workspace.Hosts[i];
                if (host.Id == 0) continue;
                secondary[n++] = GetOsWindowHandle(host.NativeWindow);
            }

            return IsPointerOverAnyEditor(root, GetOsWindowHandle(_mainWindow), secondary[..n]);
        }
    }

    internal static bool IsPointerOverAnyEditor(
        nint windowFromPointRoot,
        nint mainOsHandle,
        ReadOnlySpan<nint> secondaryOsHandles)
    {
        if (windowFromPointRoot == nint.Zero)
            return false;
        if (mainOsHandle != nint.Zero && windowFromPointRoot == mainOsHandle)
            return true;
        return IsPointerOverSecondary(windowFromPointRoot, mainOsHandle, secondaryOsHandles);
    }

    public bool IsPointerOverSecondaryWindow
    {
        get
        {
            if (!TryGetTopWindowAtCursor(out nint root))
                return false;

            int count = _workspace.Hosts.Count;
            Span<nint> secondary = count <= 16 ? stackalloc nint[count] : new nint[count];
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                EditorSurfaceHost host = _workspace.Hosts[i];
                if (host.Id == 0) continue;
                secondary[n++] = GetOsWindowHandle(host.NativeWindow);
            }

            return IsPointerOverSecondary(root, GetOsWindowHandle(_mainWindow), secondary[..n]);
        }
    }

    /// <summary>
    /// The HWND under the cursor wins. A floater whose OS handle is 0 or equal to the main
    /// window must not steal main-window clicks — that is what made Scene keep zooming
    /// (stale viewport hover) while the main Paper stopped seeing buttons.
    /// </summary>
    internal static bool IsPointerOverSecondary(
        nint windowFromPointRoot,
        nint mainOsHandle,
        ReadOnlySpan<nint> secondaryOsHandles)
    {
        if (windowFromPointRoot == nint.Zero)
            return false;
        if (mainOsHandle != nint.Zero && windowFromPointRoot == mainOsHandle)
            return false;

        for (int i = 0; i < secondaryOsHandles.Length; i++)
        {
            nint handle = secondaryOsHandles[i];
            if (handle != nint.Zero && handle != mainOsHandle && handle == windowFromPointRoot)
                return true;
        }

        return false;
    }

    internal static bool IsPointerOverWindow(IWindow? window)
    {
        if (window == null) return false;
        if (TryGetTopWindowAtCursor(out nint root))
            return GetOsWindowHandle(window) == root;
        // Other platforms retain their existing pointer routing until a native hit test is available.
        return !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    internal static bool IsDragButtonDown(PaperUI.Paper paper) => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? (GetAsyncKeyState(VkLButton) & 0x8000) != 0 : paper.IsPointerDown(PaperUI.PaperMouseBtn.Left);

    internal static bool TryGetDragPointer(out Float2 position)
    {
        position = default;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !GetCursorPos(out var point)) return false;
        position = new Float2(point.X, point.Y);
        return true;
    }

    private bool IsPointerOverHost(EditorSurfaceHost host)
    {
        if (!host.IsVisible || host.NativeWindow == null)
            return false;

        nint osHandle = GetOsWindowHandle(host.NativeWindow);
        if (TryGetTopWindowAtCursor(out nint root) && osHandle != nint.Zero)
            return root == osHandle;

        if (!GetCursorPos(out Win32Point point))
            return false;
        Rect bounds = host.ScreenBounds;
        return point.X >= bounds.Min.X && point.X < bounds.Max.X
            && point.Y >= bounds.Min.Y && point.Y < bounds.Max.Y;
    }

    private static bool TryGetTopWindowAtCursor(out nint root)
    {
        root = nint.Zero;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !GetCursorPos(out Win32Point point))
            return false;
        nint hit = WindowFromPoint(point);
        if (hit == nint.Zero) return false;
        root = GetAncestor(hit, GaRoot);
        if (root == nint.Zero) root = hit;
        return true;
    }

    public void SetMainWindowFocused(bool focused)
    {
        if (!focused) _workspace.FindHost(0)?.Paper?.CancelDragEdit();
        if (_mainWindowFocused != focused)
            _workspace.FindHost(0)?.FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Window);
        _mainWindowFocused = focused;
        UpdateWindowGroupFocus();
    }

    public void ReconfigureHost(EditorSurfaceHost host)
    {
        ConfigureDockDrag(host);
        host.DockSpace.WindowChrome = null;
    }

    private bool TryAttachSecondary(EditorSurfaceHost host)
    {
        IWindow? window = null;
        bool attached = false;
        try
        {
            window = Silk.NET.Windowing.Window.Create(CreateOptions(host));
            if (Graphics.Device is OpenGLGraphicsDevice openGlDevice)
                openGlDevice.ExecuteWithContextReleased(window.Initialize);
            else
                window.Initialize();
            var surface = new SilkWindowSurface(window);
            if (!Graphics.TryCreatePresentationSurface(surface, out PresentationSurfaceId presentationSurface,
                    out string unavailableReason))
            {
                Runtime.Debug.LogWarning($"Unable to create detached editor window: {unavailableReason}");
                window.Reset();
                return false;
            }

            host.AttachNativeWindow(window, surface, presentationSurface);
            EditorWindowPlacement.Apply(host, window);
            EditorWindowChrome.Apply(window);
            // Owner must use GLFW HWNDs (GetOsWindowHandle), never DXGI/GL NativeHandle.
            // Assign while hidden, then again after show — GLFW can overwrite GWLP_HWNDPARENT.
            nint mainHwnd = GetOsWindowHandle(_mainWindow);
            nint childHwnd = GetOsWindowHandle(window);
            EditorNativeWindowOwner.TryAssignOwner(childHwnd, mainHwnd);
            window.IsVisible = true;
            host.IsVisible = true;
            EditorNativeWindowOwner.TryAssignOwner(GetOsWindowHandle(window), mainHwnd);
            attached = true;
            _inputContexts.Add(host.Id, CreatePaperContext(host, window, presentationSurface));
            ConfigureDockDrag(host);
            HookWindow(host, window);
            UpdateDockingScreenBounds(host, window);
            host.FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Layout | EditorSurfaceDirtyReason.Window);
            return true;
        }
        catch (Exception ex)
        {
            if (attached)
                Release(host);
            else
                try { window?.Reset(); } catch { }
            Runtime.Debug.LogWarning($"Unable to create detached editor window: {ex.Message}");
            return false;
        }
    }

    public void Release(EditorSurfaceHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _pendingHosts.Remove(host);
        _focusedSecondaryHosts.Remove(host.Id);
        host.DockSpace.WindowChrome = null;
        if (_inputContexts.Remove(host.Id, out EditorSurfaceInput? input))
            input.Dispose();
        (IWindow? window, _, PresentationSurfaceId surface) = host.DetachNativeWindow();
        host.ReleasePaper();
        if (host.Id == 0 || window == null)
            return;

        if (surface.IsValid)
            Graphics.TryDestroyPresentationSurface(surface);
        bool wasClosing = _closingHosts.Contains(host.Id);
        _closingHosts.Add(host.Id);
        try { window.Close(); } catch { }
        finally
        {
            if (!wasClosing) _closingHosts.Remove(host.Id);
        }
        try { window.Reset(); } catch { }
    }

    public void Dispose()
    {
        if (_disposing) return;
        _disposing = true;
        OrigamiUI.DragDrop.Cancel();
        EditorPopupMapping.Uninstall(_workspace);
        XEngine.Runtime.Window.PostPresent -= ProcessPendingHosts;
        if (_mainHost != null)
        {
            _mainWindow.Move -= OnMainWindowMoved;
            _mainWindow.Resize -= OnMainWindowResized;
            _mainWindow.FramebufferResize -= OnMainWindowFramebufferResized;
            _mainHost = null;
        }
        _pendingHosts.Clear();
        foreach (EditorSurfaceInput input in _inputContexts.Values)
            input.Dispose();
        _inputContexts.Clear();
        for (int i = _workspace.Hosts.Count - 1; i >= 0; i--)
        {
            EditorSurfaceHost host = _workspace.Hosts[i];
            if (host.Id != 0)
                Release(host);
        }
    }

    public void InvalidateSecondary(EditorSurfaceDirtyReason reason)
    {
        for (int i = 0; i < _workspace.Hosts.Count; i++)
        {
            EditorSurfaceHost host = _workspace.Hosts[i];
            if (host.Id == 0) continue;
            if (!EditorShellIdleHosts.HostWantsReason(host, reason)) continue;
            host.FrameScheduler.Invalidate(reason);
        }
    }

    private WindowOptions CreateOptions(EditorSurfaceHost host)
    {
        Rect bounds = host.ScreenBounds;
        int width = Math.Max(150, (int)MathF.Round(bounds.Max.X - bounds.Min.X));
        int height = Math.Max(80, (int)MathF.Round(bounds.Max.Y - bounds.Min.Y));
        var options = WindowOptions.Default;
        options.Title = host.WindowTitle;
        options.Size = new Vector2D<int>(width, height);
        options.Position = new Vector2D<int>((int)MathF.Round(bounds.Min.X), (int)MathF.Round(bounds.Min.Y));
        options.ShouldSwapAutomatically = false;
        options.IsVisible = false;
        options.WindowBorder = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowBorder.Resizable : WindowBorder.Hidden;
        // The primary window paces the editor loop. Serial secondary presents must not each
        // wait for another refresh interval.
        options.VSync = false;
        options.API = Graphics.Device?.Backend switch
        {
            GraphicsBackend.OpenGL or GraphicsBackend.WebGL2 => new GraphicsAPI(
                ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 1)),
            GraphicsBackend.Vulkan => new GraphicsAPI(
                ContextAPI.Vulkan, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(1, 2)),
            GraphicsBackend.Direct3D12 => new GraphicsAPI(
                ContextAPI.None, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(12, 0)),
            _ => GraphicsAPI.None,
        };
        if (Graphics.Device?.Backend is GraphicsBackend.OpenGL or GraphicsBackend.WebGL2)
            options.SharedContext = _mainWindow.GLContext;
        return options;
    }

    private void HookWindow(EditorSurfaceHost host, IWindow window)
    {
        window.Move += position =>
        {
            Rect bounds = host.ScreenBounds;
            float width = bounds.Max.X - bounds.Min.X;
            float height = bounds.Max.Y - bounds.Min.Y;
            host.ScreenBounds = new Rect(position.X, position.Y, position.X + width, position.Y + height);
            UpdateDockingScreenBounds(host, window);
            NotifyWindowInteraction(host);
        };
        window.Resize += size =>
        {
            var position = window.Position;
            host.ScreenBounds = new Rect(position.X, position.Y, position.X + size.X, position.Y + size.Y);
            UpdateDockingScreenBounds(host, window);
            NotifyWindowInteraction(host);
        };
        window.FramebufferResize += size =>
        {
            if (host.PresentationSurface.IsValid)
                Graphics.TryResizePresentationSurface(host.PresentationSurface, size.X, size.Y);
            UpdateDockingScreenBounds(host, window);
            NotifyWindowInteraction(host);
        };
        window.StateChanged += state =>
        {
            bool visible = state != WindowState.Minimized;
            if (host.IsVisible == visible) return;
            host.IsVisible = visible;
            if (visible)
                host.FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Window);
        };
        window.FocusChanged += focused =>
        {
            SetSecondaryFocused(host, focused);
            UpdateWindowGroupFocus();
        };
        window.Closing += () => CloseFromNativeWindow(host);
    }

    private bool _wasWindowGroupActive = true;

    private void SetSecondaryFocused(EditorSurfaceHost host, bool focused)
    {
        host.SetNativeFocused(focused);
        if (focused) _focusedSecondaryHosts.Add(host.Id);
        else _focusedSecondaryHosts.Remove(host.Id);
    }

    private void UpdateWindowGroupFocus()
    {
        bool focused = IsWindowGroupActive;
        if (_wasWindowGroupActive && !focused)
        {
            ContextMenu.Close();
            OrigamiUI.DragDrop.Cancel();
        }
        _wasWindowGroupActive = focused;
        for (int i = 0; i < _workspace.Hosts.Count; i++)
            _workspace.Hosts[i].FrameScheduler.SetFocused(focused);
    }

    private static void NotifyWindowInteraction(EditorSurfaceHost host)
    {
        host.FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Window);
        host.FrameScheduler.RegisterAnimation(Time.UnscaledTotalTime + 0.2);
    }

    private void CloseFromNativeWindow(EditorSurfaceHost host)
    {
        if (_disposing || host.Id == 0 || !_closingHosts.Add(host.Id)) return;
        _pendingNativeCloses.Add(host.Id);
    }

    private void ConfigureDockDrag(EditorSurfaceHost host)
    {
        host.DockSpace.BeginExternalTabDrag = (panel, position) =>
        {
            ClearDockDropPreview();
            bool started = _dockDragController.BeginDrag(panel, ToScreenPosition(host, position));
            if (started)
            {
                _externalDragSourceHost = host;
                NotifyWindowInteraction(host);
            }
            return started;
        };
        host.DockSpace.UpdateExternalTabDrag = position =>
        {
            if (_dockDragController.IsDragging)
            {
                _dockDragController.UpdateDrag(ToScreenPosition(host, position));
                UpdateDockDropPreview();
                NotifyWindowInteraction(host);
            }
        };
        host.DockSpace.EndExternalTabDrag = position =>
            CompleteDockDrag(host, ToScreenPosition(host, position));
    }

    private void CompleteDockDrag(EditorSurfaceHost sourceHost, Float2 screenPosition)
    {
        if (!_dockDragController.IsDragging) return;
        _dockDragController.UpdateDrag(screenPosition);
        try
        {
            DockDropCandidate candidate = _dockDragController.CurrentCandidate;
            DockNode? sourceLeaf = _dockDragController.DraggedPanel != null
                ? _workspace.FindPanelLeaf(_dockDragController.DraggedPanel)
                : null;
            if (candidate.HostId == sourceHost.Id && candidate.Zone == DockDropZone.Tab
                && ReferenceEquals(candidate.TargetLeaf, sourceLeaf))
            {
                _dockDragController.CancelDrag();
                return;
            }

            DockDragResult result = _dockDragController.CommitDrag();
            EditorSurfaceHost? targetHost = result.Candidate.IsValid
                ? _workspace.FindHost(result.Candidate.HostId)
                : null;
            if (result.Outcome == DockDragOutcome.Docked && sourceHost.Id != 0 && !sourceHost.HasPanels)
                _workspace.UnregisterHost(sourceHost.Id);
            InvalidateSecondary(EditorSurfaceDirtyReason.Layout);
            _workspace.FindHost(sourceHost.Id)?.FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Layout);
            targetHost?.FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Layout);
        }
        finally
        {
            sourceHost.DockSpace.CancelExternalTabDrag();
            if (ReferenceEquals(_externalDragSourceHost, sourceHost))
                _externalDragSourceHost = null;
            ClearDockDropPreview();
        }
    }

    private void UpdateDockDropPreview()
    {
        ClearDockDropPreview();
        DockDropCandidate candidate = _dockDragController.CurrentCandidate;
        if (!candidate.IsValid || candidate.Zone == DockDropZone.Float) return;
        EditorSurfaceHost? target = _workspace.FindHost(candidate.HostId);
        if (target == null) return;

        Rect bounds = target.DockingScreenBounds;
        Rect preview = candidate.PreviewBounds;
        target.DockSpace.ExternalDropPreview = new Rect(
            ToPaperPosition(target, preview.Min.X - bounds.Min.X),
            ToPaperPosition(target, preview.Min.Y - bounds.Min.Y),
            ToPaperPosition(target, preview.Max.X - bounds.Min.X),
            ToPaperPosition(target, preview.Max.Y - bounds.Min.Y));
        target.FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Input);
    }

    private void ClearDockDropPreview()
    {
        for (int i = 0; i < _workspace.Hosts.Count; i++)
        {
            EditorSurfaceHost host = _workspace.Hosts[i];
            if (host.DockSpace.ExternalDropPreview == null) continue;
            host.DockSpace.ExternalDropPreview = null;
            host.FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Input);
        }
    }

    private static Float2 ToScreenPosition(EditorSurfaceHost host, Float2 localPosition) => new(
        host.DockingScreenBounds.Min.X + ToWindowPosition(host, localPosition.X),
        host.DockingScreenBounds.Min.Y + ToWindowPosition(host, localPosition.Y));

    private static float ToWindowPosition(EditorSurfaceHost host, float paperPosition) =>
        paperPosition * Math.Max(0.01f, host.DpiScale * EditorTheme.UserScale);

    private static float ToPaperPosition(EditorSurfaceHost host, float windowPosition) =>
        windowPosition / Math.Max(0.01f, host.DpiScale * EditorTheme.UserScale);

    private void TrackMainHost(EditorSurfaceHost host)
    {
        if (_mainHost != null) return;
        _mainHost = host;
        _mainWindow.Move += OnMainWindowMoved;
        _mainWindow.Resize += OnMainWindowResized;
        _mainWindow.FramebufferResize += OnMainWindowFramebufferResized;
        UpdateDockingScreenBounds(host, _mainWindow);
    }

    private void OnMainWindowMoved(Vector2D<int> _) => RefreshMainHostDockingBounds();
    private void OnMainWindowResized(Vector2D<int> _) => RefreshMainHostDockingBounds();
    private void OnMainWindowFramebufferResized(Vector2D<int> _) => RefreshMainHostDockingBounds();

    private void RefreshMainHostDockingBounds()
    {
        if (_mainHost != null)
            UpdateDockingScreenBounds(_mainHost, _mainWindow);
    }

    private static void UpdateDockingScreenBounds(EditorSurfaceHost host, IWindow window)
    {
        Vector2D<int> logical = window.Size;
        Vector2D<int> framebuffer = window.FramebufferSize;
        float scale = logical.X > 0
            ? Math.Max(0.01f, framebuffer.X / (float)logical.X)
            : 1f;
        host.DpiScale = scale;

        nint nativeHandle = GetOsWindowHandle(window);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && nativeHandle != nint.Zero
            && GetClientRect(nativeHandle, out Win32Rect clientBounds))
        {
            var clientOrigin = new Win32Point { X = clientBounds.Left, Y = clientBounds.Top };
            if (ClientToScreen(nativeHandle, ref clientOrigin))
            {
                host.SetDockingScreenBounds(new Rect(
                    clientOrigin.X,
                    clientOrigin.Y,
                    clientOrigin.X + clientBounds.Right - clientBounds.Left,
                    clientOrigin.Y + clientBounds.Bottom - clientBounds.Top));
                return;
            }
        }

        Vector2D<int> position = window.Position;
        host.SetDockingScreenBounds(new Rect(
            position.X,
            position.Y,
            position.X + framebuffer.X,
            position.Y + framebuffer.Y));
    }

    private void ProcessPendingHosts(float delta)
    {
        for (int i = _pendingHosts.Count - 1; i >= 0; i--)
        {
            EditorSurfaceHost host = _pendingHosts[i];
            _pendingHosts.RemoveAt(i);
            if (_workspace.FindHost(host.Id) != host || host.HasNativeWindow)
                continue;
            if (TryAttachSecondary(host))
                continue;

            // The host was accepted optimistically so creation could happen outside the primary frame.
            // If the OS/RHI rejects it, return its panels to the main host instead of leaving them hidden.
            _workspace.TryCloseHost(host.Id, 0, out _);
        }

        PumpSecondaryWindowEvents();
        ProcessPendingNativeCloses();
        PruneEmptySecondaryHosts();
        UpdateWindowGroupFocus();
        PumpExternalDockDrag();
        RenderSecondaryHosts();
    }

    private void ProcessPendingNativeCloses()
    {
        for (int i = _pendingNativeCloses.Count - 1; i >= 0; i--)
        {
            int hostId = _pendingNativeCloses[i];
            _pendingNativeCloses.RemoveAt(i);
            // Caption-X destroys panels (Unity). Create/DoEvents salvage still uses TryCloseHost.
            if (_workspace.FindHost(hostId) != null)
                _workspace.TryDestroyHost(hostId, out _);
            _closingHosts.Remove(hostId);
            InvalidateSecondary(EditorSurfaceDirtyReason.Layout);
        }
    }

    /// <summary>
    /// Drops empty secondary HWNDs after the last tab X (Unity: window vanishes with last panel).
    /// Skipped while a cross-host drag is in flight so the source host is not torn down mid-drag.
    /// </summary>
    private void PruneEmptySecondaryHosts()
    {
        if (_dockDragController.IsDragging || _externalDragSourceHost != null)
            return;

        bool pruned = false;
        for (int i = _workspace.Hosts.Count - 1; i >= 0; i--)
        {
            EditorSurfaceHost host = _workspace.Hosts[i];
            if (host.Id == 0 || host.HasPanels) continue;
            _workspace.UnregisterHost(host.Id);
            pruned = true;
        }

        if (pruned)
            InvalidateSecondary(EditorSurfaceDirtyReason.Layout);
    }

    private void PumpExternalDockDrag()
    {
        EditorSurfaceHost? sourceHost = _externalDragSourceHost;
        if (sourceHost == null || !_dockDragController.IsDragging || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        try
        {
            if (!GetCursorPos(out Win32Point point)) return;
            var screenPosition = new Float2(point.X, point.Y);
            bool buttonDown = (GetAsyncKeyState(VkLButton) & 0x8000) != 0;
            if (buttonDown)
            {
                _dockDragController.UpdateDrag(screenPosition);
                UpdateDockDropPreview();
                NotifyWindowInteraction(sourceHost);
                return;
            }

            CompleteDockDrag(sourceHost, screenPosition);
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"External dock drag polling failed: {ex.Message}");
            _dockDragController.CancelDrag();
            sourceHost.DockSpace.CancelExternalTabDrag();
            _externalDragSourceHost = null;
            ClearDockDropPreview();
        }
    }

    private void PumpSecondaryWindowEvents()
    {
        // Reused buffer, not a per-pass copy: DoEvents can pump window callbacks that mutate the
        // workspace (close → unregister), so a snapshot is required — but it must not allocate
        // every PostPresent tick of the main loop.
        _pumpHostsScratch.Clear();
        _pumpHostsScratch.AddRange(_workspace.Hosts);
        nint foreground = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? GetForegroundWindow() : nint.Zero;
        for (int i = 0; i < _pumpHostsScratch.Count; i++)
        {
            EditorSurfaceHost host = _pumpHostsScratch[i];
            if (host.Id == 0 || host.NativeWindow == null)
                continue;
            try
            {
                host.NativeWindow.DoEvents();
                SetSecondaryFocused(host, ComputeSurfaceFocused(foreground,
                    GetOsWindowHandle(host.NativeWindow), host.IsNativeFocused));
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogWarning($"Detached editor window event pump failed: {ex.Message}");
                _workspace.TryCloseHost(host.Id, 0, out _);
            }
        }
        _pumpHostsScratch.Clear();
    }

    private static EditorSurfaceInput CreatePaperContext(
        EditorSurfaceHost host,
        IWindow window,
        PresentationSurfaceId presentationSurface)
    {
        var framebuffer = window.FramebufferSize;
        var renderer = new PaperRenderer();
        try
        {
            renderer.Initialize(framebuffer.X, framebuffer.Y);
            renderer.SetPresentationSurface(presentationSurface);
            var logical = window.Size;
            var paper = new XEngine.PaperUI.Paper(renderer, logical.X, logical.Y, new XEngine.Quill.FontAtlasSettings());
            if (Program.HierarchyPaintSharedAtlas) paper.Canvas.EnableFontAtlasRetirement();
            var input = new EditorSurfaceInput(window.CreateInput());
            paper.SetClipboardHandler(input);
            paper.OnCursorChange += input.SetCursor;
            host.AttachPaper(renderer, paper);
            return input;
        }
        catch
        {
            renderer.Dispose();
            throw;
        }
    }

    private void RenderSecondaryHosts()
    {
        double now = Time.UnscaledTotalTime;
        for (int i = 0; i < _workspace.Hosts.Count; i++)
        {
            EditorSurfaceHost host = _workspace.Hosts[i];
            if (host.Id == 0 || !host.IsVisible || host.NativeWindow == null || host.Paper == null
                || host.PaperRenderer == null || !host.PresentationSurface.IsValid)
                continue;
            // Render continuously only while the pointer is on this HWND, or a spreading
            // menu needs every surface. A focused-but-unhovered floater rendered every frame after the
            // user moved to Scene — its DoEvents/present stole main-window clicks.
            // SetInteractiveActive (not a sticky Request) so the flag drops on the first pass
            // where hover no longer holds; otherwise a hovered-then-abandoned floater keeps
            // presenting every tick and freezes its pixels when hidden conditions skip it.
            bool interactiveSurface = EditorShellIdleHosts.SecondaryIsInteractive(
                IsPointerOverHost(host), ContextMenu.SpreadsToAllSurfaces, Modal.IsOpen);
            _inputContexts.TryGetValue(host.Id, out EditorSurfaceInput? input);
            // Input callbacks can outlive hover, including a full click or keyboard tap.
            // Queue one repaint per drain pass without making this surface continuously active.
            if (interactiveSurface || input?.HasPendingInput == true)
                host.FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Input);
            if (EditorShellIdleHosts.ShouldPumpHost(host))
                host.FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Animation);
            host.FrameScheduler.SetInteractiveActive(interactiveSurface);
            if (!host.FrameScheduler.ShouldRender(now))
                continue;

            IWindow window = host.NativeWindow;
            var framebuffer = window.FramebufferSize;
            if (framebuffer.X <= 0 || framebuffer.Y <= 0)
                continue;
            if (!Graphics.TryBeginPresentationFrame(host.PresentationSurface))
                continue;

            bool paperFrameStarted = false;
            try
            {
                using (var clear = Graphics.GetCommandBuffer("Editor Secondary Surface Clear"))
                {
                    clear.SetPresentationSurface(host.PresentationSurface);
                    clear.SetRenderTarget(null);
                    clear.SetViewport(0, 0, (uint)framebuffer.X, (uint)framebuffer.Y);
                    clear.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil,
                        new Color(56f / 255f, 56f / 255f, 56f / 255f, 1f));
                    Graphics.Submit(clear);
                }

                GetPaperDimensions(window, framebuffer, out float logicalWidth, out float logicalHeight, out float scale);
                host.PaperRenderer.UpdateProjection(framebuffer.X, framebuffer.Y);
                host.Paper.SetResolution(logicalWidth, logicalHeight);
                host.Paper.DisplayFramebufferScale = new Float2(scale, scale);
                host.Paper.BeginFrame((float)Time.UnscaledDeltaTime, -1f);
                paperFrameStarted = true;
                if (input != null)
                {
                    Vector2D<int> nativeSize = window.Size;
                    input.Apply(host.Paper,
                        nativeSize.X > 0 ? logicalWidth / nativeSize.X : 1f,
                        nativeSize.Y > 0 ? logicalHeight / nativeSize.Y : 1f);
                }
                using (EditorTheme.PushOrigami())
                {
                    OrigamiUI.Origami.BeginFrame(host.Paper, (float)Time.UnscaledDeltaTime);
                    host.DockSpace.Draw(host.Paper, 0f, 0f, logicalWidth, logicalHeight);
                    OrigamiUI.Origami.EndFrame(host.Paper);
                }
                if (!string.Equals(window.Title, host.WindowTitle, StringComparison.Ordinal))
                    window.Title = host.WindowTitle;
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogError($"Detached editor surface render failed: {ex.Message}");
            }
            finally
            {
                if (paperFrameStarted)
                {
                    host.Paper.EndFrame();
                    if (host.Paper.NeedsFollowUpFrame)
                    {
                        host.FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Input);
                        host.FrameScheduler.RegisterAnimation(
                            now + EditorUiRenderGate.PaperClickFollowUpSeconds);
                    }
                }
                Graphics.TryEndPresentationFrame(host.PresentationSurface);
                host.RenderedFrameCount++;
            }
        }
    }

    private static void GetPaperDimensions(IWindow window, Vector2D<int> framebuffer,
        out float logicalWidth, out float logicalHeight, out float scale)
    {
        Vector2D<int> logical = window.Size;
        float contentScale = logical.X > 0 ? Math.Max(0.01f, framebuffer.X / (float)logical.X) : 1f;
        scale = contentScale * Math.Max(0.01f, EditorTheme.UserScale);
        logicalWidth = framebuffer.X / scale;
        logicalHeight = framebuffer.Y / scale;
    }
}
