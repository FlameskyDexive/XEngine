// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Backends.WebGPU;

/// <summary>
/// Canvas presentation for emdawnwebgpu. <see cref="HtmlCanvasSelector"/> is the CSS selector
/// passed to <c>WGPUEmscriptenSurfaceSourceCanvasHTMLSelector</c> (usually <c>#canvas</c>).
/// </summary>
public sealed class WebGPUCanvasSurface : IWindowSurface
{
    private int _width;
    private int _height;

    public WebGPUCanvasSurface(string htmlCanvasSelector, int width, int height)
    {
        HtmlCanvasSelector = string.IsNullOrWhiteSpace(htmlCanvasSelector) ? "#canvas" : htmlCanvasSelector;
        UpdateSize(width, height);
    }

    public string HtmlCanvasSelector { get; }
    public nint NativeHandle => nint.Zero;
    public (int Width, int Height) FramebufferSize => (_width, _height);
    public bool VSync { get; set; } = true;
    public int SwapInterval { get; set; } = 1;

    public void UpdateSize(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
    }

    public void MakeCurrent() { }
    public void ClearCurrent() { }
    public void SwapBuffers() { }
}
