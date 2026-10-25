// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Rendering;

/// <summary>LOD cross-fade (U17-M8). Stencil bits match URP 17 CrossFadeStencilRef (bits 2–3).</summary>
public enum LodCrossFadeMode : byte
{
    None = 0,
    Dither = 1,
    Stencil = 2,
}

/// <summary>CPU helpers for dither and stencil LOD cross-fade. Does not replace mesh LOD selection.</summary>
public static class LodCrossFade
{
    public const int StencilBit0 = 1 << 2;
    public const int StencilBit1 = 1 << 3;
    public const int StencilMask = StencilBit0 | StencilBit1;

    public static int StencilRef(int lodLevel)
        => (lodLevel & 1) != 0 ? StencilBit1 : StencilBit0;

    /// <summary>0 = fully previous LOD, 1 = fully next LOD.</summary>
    public static float FadeFactor(float normalizedDistance, float transitionWidth)
    {
        if (!float.IsFinite(normalizedDistance))
            return 1f;
        float width = transitionWidth;
        if (!float.IsFinite(width) || width <= 0f)
            return normalizedDistance >= 1f ? 1f : 0f;
        float start = 1f - width;
        if (normalizedDistance <= start)
            return 0f;
        if (normalizedDistance >= 1f)
            return 1f;
        return (normalizedDistance - start) / width;
    }

    /// <summary>4×4 Bayer dither threshold in 0–1. Reject the pixel when fade is below the threshold.</summary>
    public static float DitherThreshold(int x, int y)
    {
        int ix = x & 3;
        int iy = y & 3;
        int[] bayer =
        [
            0, 8, 2, 10,
            12, 4, 14, 6,
            3, 11, 1, 9,
            15, 7, 13, 5,
        ];
        return (bayer[iy * 4 + ix] + 0.5f) / 16f;
    }

    public static bool DitherVisible(float fade, int x, int y)
        => fade >= DitherThreshold(x, y);
}
