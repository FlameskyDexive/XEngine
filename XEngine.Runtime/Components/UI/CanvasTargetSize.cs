// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// Resolves the pixel size a screen-space canvas should layout against.
/// Pipeline <c>ScreenSizeOverride</c> wins, then a camera RenderTexture, then the
/// framebuffer. 90° and 270° rotations swap width and height (portrait/landscape).
/// </summary>
public static class CanvasTargetSize
{
    public const float FallbackWidth = 1280f;
    public const float FallbackHeight = 720f;

    /// <summary>Test hook. When set, <see cref="Resolve"/> applies this clockwise rotation.</summary>
    public static int? RotationOverride { get; set; }

    public static int NormalizeDegrees(int clockwiseDegrees)
    {
        int wrapped = clockwiseDegrees % 360;
        if (wrapped < 0) wrapped += 360;
        return wrapped;
    }

    public static Float2 ApplyRotation(Float2 size, int clockwiseDegrees)
    {
        int degrees = NormalizeDegrees(clockwiseDegrees);
        if (degrees == 90 || degrees == 270)
            return new Float2(size.Y, size.X);
        return size;
    }

    public static Float2 Resolve(Float2? overrideSize, Float2? cameraTargetSize, Float2 framebuffer, int clockwiseDegrees)
    {
        Float2 size;
        if (overrideSize is { } o && o.X > 0f && o.Y > 0f)
            size = o;
        else if (cameraTargetSize is { } t && t.X > 0f && t.Y > 0f)
            size = t;
        else if (framebuffer.X > 0f && framebuffer.Y > 0f)
            size = framebuffer;
        else
            size = new Float2(FallbackWidth, FallbackHeight);

        int rotation = RotationOverride ?? clockwiseDegrees;
        return ApplyRotation(size, rotation);
    }
}
