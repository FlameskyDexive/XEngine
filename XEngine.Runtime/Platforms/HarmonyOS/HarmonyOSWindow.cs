// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#if XENGINE_HARMONYOS
using System;

using Silk.NET.Maths;

using XEngine.Runtime.RHI;

namespace XEngine.Runtime;

/// <summary>
/// Window state supplied by the generated HarmonyOS native host. Wave 3 wires the host's
/// XComponent callbacks to this facade; it exists now so the mobile Runtime closure compiles
/// without selecting desktop windowing.
/// </summary>
public sealed class HarmonyOSWindowState
{
    public Vector2D<int> Position { get; internal set; }
    public Vector2D<int> Size { get; internal set; } = new(1, 1);
    public Vector2D<int> FramebufferSize { get; internal set; } = new(1, 1);
    public bool IsVisible { get; internal set; } = true;
    public bool VSync { get; set; } = true;
    public bool IsFocused { get; internal set; } = true;
    public bool IsClosing { get; internal set; }
    public double FramesPerSecond { get; set; } = 60;
    public double UpdatesPerSecond { get; set; } = 60;
    public nint Handle { get; internal set; }
    public string Title { get; internal set; } = "XEngine";
}

/// <summary>
/// HarmonyOS window facade. The future native host owns event pumping and surface lifecycle;
/// desktop <see cref="Start"/> is intentionally unavailable on this target.
/// </summary>
public static class Window
{
    public static HarmonyOSWindowState InternalWindow { get; } = new();
    public static IWindowSurface? PlatformSurface { get; internal set; }

    public static event Action? Load;
    public static event Action<float>? Update;
    public static event Action<float>? Render;
    public static event Action<float>? PostRender;
    /// <summary>
    /// Optional per-tick render gate shared with the desktop Window facade. A false result keeps
    /// platform update/input processing alive while the native host skips render submission.
    /// </summary>
    public static Func<bool>? RenderRequested { get; set; }
    public static event Action<bool>? FocusChanged;
    public static event Action<Vector2D<int>>? Resize;
    public static event Action<Vector2D<int>>? FramebufferResize;
    /// <summary>Raised when the usable framebuffer rectangle changes because of resize or platform insets.</summary>
    public static event Action<WindowSafeArea>? SafeAreaChanged;
    public static event Action? Closing;
    public static event Action? BackRequested;
    public static event Action<Vector2D<int>>? Move;
    public static event Action<string[]>? FileDrop;

    private static WindowSafeAreaInsets s_safeAreaInsets;

    /// <summary>Insets reserved by the HarmonyOS system bars and display cutouts, in framebuffer pixels.</summary>
    public static WindowSafeAreaInsets SafeAreaInsets => s_safeAreaInsets;

    /// <summary>The visible, unobscured rectangle in physical framebuffer pixels.</summary>
    public static WindowSafeArea SafeArea => s_safeAreaInsets.ToSafeArea(
        InternalWindow.FramebufferSize.X,
        InternalWindow.FramebufferSize.Y);

    public static float ContentScale
    {
        get
        {
            Vector2D<int> logical = InternalWindow.Size;
            Vector2D<int> framebuffer = InternalWindow.FramebufferSize;
            return logical.X > 0 ? Math.Max(0.01f, (float)framebuffer.X / logical.X) : 1f;
        }
    }

    public static Vector2D<int> Position
    {
        get => InternalWindow.Position;
        set => InternalWindow.Position = value;
    }

    public static Vector2D<int> Size => InternalWindow.Size;
    public static bool IsVisible => InternalWindow.IsVisible;

    public static bool VSync
    {
        get => InternalWindow.VSync;
        set => InternalWindow.VSync = value;
    }

    public static float FramesPerSecond
    {
        get => (float)InternalWindow.FramesPerSecond;
        set
        {
            InternalWindow.FramesPerSecond = value;
            InternalWindow.UpdatesPerSecond = value;
        }
    }

    public static nint Handle => InternalWindow.Handle;
    public static bool IsFocused => InternalWindow.IsFocused;

    public static void InitWindow(string title, int width, int height, bool vSync = true)
    {
        InternalWindow.Size = new Vector2D<int>(Math.Max(1, width), Math.Max(1, height));
        InternalWindow.FramebufferSize = InternalWindow.Size;
        InternalWindow.VSync = vSync;
    }

    public static void Start() => throw new PlatformNotSupportedException(
        "HarmonyOS games are driven by the generated native host, not the desktop Window.Start loop.");

    public static void Stop() => InternalWindow.IsClosing = true;

    internal static void NotifySurfaceCreated(IWindowSurface surface, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(surface);
        PlatformSurface = surface;
        InternalWindow.Handle = surface.NativeHandle;
        UpdateSize(width, height);
        InternalWindow.IsClosing = false;
    }

    internal static void NotifyLoaded() => Load?.Invoke();

    internal static void NotifySafeAreaInsetsChanged(WindowSafeAreaInsets insets)
    {
        if (s_safeAreaInsets == insets)
            return;
        s_safeAreaInsets = insets;
        SafeAreaChanged?.Invoke(SafeArea);
    }

    internal static void NotifyFrame(float deltaTime)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Update?.Invoke(deltaTime);
        LastUpdateMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (RenderRequested?.Invoke() == false)
            return;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        Render?.Invoke(deltaTime);
        PostRender?.Invoke(deltaTime);
        LastRenderMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    internal static double LastUpdateMs { get; private set; }
    internal static double LastRenderMs { get; private set; }

    internal static void NotifyFocus(bool focused)
    {
        if (InternalWindow.IsFocused == focused)
            return;
        InternalWindow.IsFocused = focused;
        FocusChanged?.Invoke(focused);
    }

    internal static void NotifySurfaceChanged(int width, int height) => UpdateSize(width, height);

    internal static void NotifyMoved(int x, int y)
    {
        var position = new Vector2D<int>(x, y);
        InternalWindow.Position = position;
        Move?.Invoke(position);
    }

    internal static void NotifyFileDrop(string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        FileDrop?.Invoke(paths);
    }

    internal static void NotifySurfaceDestroyed()
    {
        PlatformSurface = null;
        InternalWindow.Handle = 0;
    }

    internal static void NotifyClosing()
    {
        InternalWindow.IsClosing = true;
        AssetLoader.Stop();
        Closing?.Invoke();
    }

    internal static bool NotifyBackRequested()
    {
        if (BackRequested == null)
            return false;
        BackRequested.Invoke();
        return true;
    }

    private static void UpdateSize(int width, int height)
    {
        var size = new Vector2D<int>(Math.Max(1, width), Math.Max(1, height));
        InternalWindow.Size = size;
        InternalWindow.FramebufferSize = size;
        Resize?.Invoke(size);
        Graphics.Resize(size.X, size.Y);
        FramebufferResize?.Invoke(size);
    }
}
#endif
