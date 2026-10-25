// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

/// <summary>
/// Insets that screen-space UI should avoid. Values are measured in physical framebuffer pixels
/// from the left, top, right, and bottom edges respectively.
/// </summary>
public readonly record struct WindowSafeAreaInsets(int Left, int Top, int Right, int Bottom)
{
    public static WindowSafeAreaInsets None => default;

    /// <summary>Resolves the usable rectangle for a framebuffer of the supplied dimensions.</summary>
    public WindowSafeArea ToSafeArea(int framebufferWidth, int framebufferHeight)
    {
        int width = Math.Max(0, framebufferWidth);
        int height = Math.Max(0, framebufferHeight);
        int left = Math.Clamp(Left, 0, width);
        int top = Math.Clamp(Top, 0, height);
        int right = Math.Clamp(Right, 0, width - left);
        int bottom = Math.Clamp(Bottom, 0, height - top);
        return new WindowSafeArea(left, top, width - left - right, height - top - bottom);
    }
}

/// <summary>A framebuffer-pixel rectangle that remains visible and unobscured by system UI.</summary>
public readonly record struct WindowSafeArea(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}
