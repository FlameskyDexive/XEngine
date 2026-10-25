using System;

namespace XEngine.Editor.Core;

internal static class GameViewScreenshotColorSpace
{
    internal static void PreserveDisplayReferredRgbAndMakeOpaqueInPlace(byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if ((rgba.Length & 3) != 0)
            throw new ArgumentException("RGBA data length must be a multiple of four.", nameof(rgba));

        for (int i = 0; i < rgba.Length; i += 4)
            rgba[i + 3] = 255;
    }
}
