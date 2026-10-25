// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.Runtime.Platforms.iOS;

/// <summary>
/// Converts UIKit view-point coordinates into physical framebuffer pixels. UITouch and
/// <c>safeAreaInsets</c> are reported in the view's point space; the renderer and public
/// safe-area API operate in framebuffer pixels (Android V62 scale precompute).
/// </summary>
internal readonly record struct IosInputCoordinateTransform(
    int FramebufferWidth,
    int FramebufferHeight,
    float ScaleX,
    float ScaleY)
{
    public static IosInputCoordinateTransform Create(
        int viewWidth,
        int viewHeight,
        int framebufferWidth,
        int framebufferHeight)
    {
        int sourceWidth = Math.Max(1, viewWidth);
        int sourceHeight = Math.Max(1, viewHeight);
        int targetWidth = Math.Max(1, framebufferWidth);
        int targetHeight = Math.Max(1, framebufferHeight);
        return new IosInputCoordinateTransform(
            targetWidth,
            targetHeight,
            targetWidth / (float)sourceWidth,
            targetHeight / (float)sourceHeight);
    }

    [HotPath]
    public Float2 Map(float x, float y) => new(
        Math.Clamp(x * ScaleX, 0f, FramebufferWidth),
        Math.Clamp(y * ScaleY, 0f, FramebufferHeight));

    public WindowSafeAreaInsets MapInsets(WindowSafeAreaInsets insets) => new(
        ScaleInset(insets.Left, ScaleX),
        ScaleInset(insets.Top, ScaleY),
        ScaleInset(insets.Right, ScaleX),
        ScaleInset(insets.Bottom, ScaleY));

    private static int ScaleInset(int inset, float scale) =>
        Math.Max(0, (int)MathF.Round(Math.Max(0, inset) * scale));
}
