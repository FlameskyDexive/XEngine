// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.Runtime.Platforms.Browser;

/// <summary>
/// Converts canvas CSS pixels into framebuffer pixels. Browsers (and mini-game touch events) report
/// client coordinates in CSS space; the WebGL2 backbuffer is <c>canvas.width/height</c>.
/// </summary>
internal readonly record struct BrowserInputCoordinateTransform(
    int FramebufferWidth,
    int FramebufferHeight,
    float ScaleX,
    float ScaleY)
{
    public static BrowserInputCoordinateTransform Create(
        float cssWidth,
        float cssHeight,
        int framebufferWidth,
        int framebufferHeight)
    {
        float sourceWidth = Math.Max(1f, cssWidth);
        float sourceHeight = Math.Max(1f, cssHeight);
        int targetWidth = Math.Max(1, framebufferWidth);
        int targetHeight = Math.Max(1, framebufferHeight);
        return new BrowserInputCoordinateTransform(
            targetWidth,
            targetHeight,
            targetWidth / sourceWidth,
            targetHeight / sourceHeight);
    }

    [HotPath]
    public Float2 Map(float cssX, float cssY) => new(
        Math.Clamp(cssX * ScaleX, 0f, FramebufferWidth),
        Math.Clamp(cssY * ScaleY, 0f, FramebufferHeight));
}
