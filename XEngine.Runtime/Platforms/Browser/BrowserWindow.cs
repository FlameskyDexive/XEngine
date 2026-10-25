// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#if XENGINE_WEB
using System;

using Silk.NET.Maths;

using XEngine.Runtime.RHI;

namespace XEngine.Runtime;

/// <summary>Window state owned by the generated canvas + rAF host.</summary>
public sealed class BrowserWindowState
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
    public string Title { get; set; } = "XEngine";
}

/// <summary>
/// Browser / mini-game window facade. The JS host owns <c>requestAnimationFrame</c> and
/// calls the internal lifecycle methods; <see cref="Game.Run"/> remains the desktop entry point.
/// </summary>
public static class Window
{
    public static BrowserWindowState InternalWindow { get; } = new();
    public static IWindowSurface? PlatformSurface { get; internal set; }
    private static WindowSafeAreaInsets s_safeAreaInsets;

    public static event Action? Load;
    public static event Action<float>? Update;
    public static event Action<float>? Render;
    public static event Action<float>? PostRender;
    public static Func<bool>? RenderRequested { get; set; }
    public static event Action<bool>? FocusChanged;
    public static event Action<Vector2D<int>>? Resize;
    public static event Action<Vector2D<int>>? FramebufferResize;
    public static event Action<WindowSafeArea>? SafeAreaChanged;
    public static event Action? Closing;
    public static event Action? BackRequested;
    public static event Action<Vector2D<int>>? Move;
    public static event Action<string[]>? FileDrop;

    public static WindowSafeAreaInsets SafeAreaInsets => s_safeAreaInsets;

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
        InternalWindow.Title = string.IsNullOrWhiteSpace(title) ? "XEngine" : title;
        InternalWindow.Size = new Vector2D<int>(Math.Max(1, width), Math.Max(1, height));
        InternalWindow.FramebufferSize = InternalWindow.Size;
        InternalWindow.VSync = vSync;
    }

    public static void Start() => throw new PlatformNotSupportedException(
        "Web games are driven by BrowserPlatformHost and requestAnimationFrame, not the desktop Window.Start loop.");

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

    internal static void NotifyFrame(float deltaTime)
    {
        Update?.Invoke(deltaTime);
        if (RenderRequested?.Invoke() == false)
            return;
        Render?.Invoke(deltaTime);
        PostRender?.Invoke(deltaTime);
    }

    internal static void NotifyFocus(bool focused)
    {
        if (InternalWindow.IsFocused == focused)
            return;
        InternalWindow.IsFocused = focused;
        FocusChanged?.Invoke(focused);
    }

    internal static void NotifySurfaceChanged(int width, int height) => UpdateSize(width, height);

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

    internal static void NotifySafeAreaInsetsChanged(WindowSafeAreaInsets insets)
    {
        if (s_safeAreaInsets == insets)
            return;
        s_safeAreaInsets = insets;
        SafeAreaChanged?.Invoke(SafeArea);
    }

    private static void UpdateSize(int width, int height)
    {
        var size = new Vector2D<int>(Math.Max(1, width), Math.Max(1, height));
        InternalWindow.Size = size;
        InternalWindow.FramebufferSize = size;
        Resize?.Invoke(size);
        Graphics.Resize(size.X, size.Y);
        FramebufferResize?.Invoke(size);
        SafeAreaChanged?.Invoke(SafeArea);
    }
}
#endif
