// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Platforms.iOS;

/// <summary>
/// Metal presentation adapter over a <c>CAMetalLayer*</c>. The generated UIKit host owns the
/// <c>UIView</c>; Runtime never imports UIKit or Microsoft.iOS types.
/// </summary>
public sealed class IosWindowSurface : IWindowSurface
{
    private int _width;
    private int _height;

    public IosWindowSurface(nint metalLayer, int width, int height)
    {
        if (metalLayer == nint.Zero)
            throw new ArgumentException("CAMetalLayer handle cannot be zero.", nameof(metalLayer));
        NativeHandle = metalLayer;
        UpdateSize(width, height);
    }

    /// <summary><c>CAMetalLayer*</c> presented by the UIKit host.</summary>
    public nint NativeHandle { get; }

    /// <inheritdoc />
    public nint MetalLayer => NativeHandle;

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
