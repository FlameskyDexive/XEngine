// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.Core.Contexts;

using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Backends.WebGL2;

/// <summary>
/// Canvas presentation adapter. The browser / mini-game host owns the
/// <c>HTMLCanvasElement</c> or <c>wx.createCanvas</c> / <c>tt.createCanvas</c> object;
/// Runtime only stores a handle, framebuffer size, and the GL loader used by
/// <c>Silk.NET.OpenGL.GL.GetApi</c>. Swap is a no-op on real WebGL2 (the compositor
/// presents); desktop tests may supply a Silk swap callback.
/// </summary>
public sealed class WebGL2CanvasSurface : IWindowSurface, INativeContext
{
    private readonly Func<string, nint> _getProcAddress;
    private readonly Action? _makeCurrent;
    private readonly Action? _clearCurrent;
    private readonly Action? _swapBuffers;
    private int _width;
    private int _height;

    public WebGL2CanvasSurface(
        nint canvasHandle,
        int width,
        int height,
        Func<string, nint> getProcAddress,
        Action? makeCurrent = null,
        Action? clearCurrent = null,
        Action? swapBuffers = null,
        bool enableDesktopSrgbFramebuffer = false)
    {
        ArgumentNullException.ThrowIfNull(getProcAddress);
        NativeHandle = canvasHandle;
        HtmlCanvasSelector = null;
        _getProcAddress = getProcAddress;
        _makeCurrent = makeCurrent;
        _clearCurrent = clearCurrent;
        _swapBuffers = swapBuffers;
        EnableDesktopSrgbFramebuffer = enableDesktopSrgbFramebuffer;
        UpdateSize(width, height);
    }

    /// <summary>Canvas pointer, emscripten context handle, or host-defined token.</summary>
    public nint NativeHandle { get; }

    public string? HtmlCanvasSelector { get; init; }

    /// <summary>
    /// Desktop GL 4.1 hosts enable <c>GL_FRAMEBUFFER_SRGB</c>. Real WebGL2 has no such
    /// cap; leave this false for browser / WeChat / Douyin canvases.
    /// </summary>
    public bool EnableDesktopSrgbFramebuffer { get; }

    public (int Width, int Height) FramebufferSize => (_width, _height);
    public bool VSync { get; set; } = true;
    public int SwapInterval { get; set; } = 1;

    public void UpdateSize(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
    }

    public void MakeCurrent() => _makeCurrent?.Invoke();
    public void ClearCurrent() => _clearCurrent?.Invoke();
    public void SwapBuffers() => _swapBuffers?.Invoke();

    public nint GetProcAddress(string proc, int? slot = null) => _getProcAddress(proc);

    public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
    {
        try
        {
            addr = _getProcAddress(proc);
        }
        catch
        {
            addr = nint.Zero;
            return false;
        }

        return addr != nint.Zero;
    }

    void IDisposable.Dispose()
    {
    }
}
