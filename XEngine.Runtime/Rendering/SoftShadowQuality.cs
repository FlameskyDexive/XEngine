// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Global soft-shadow PCF kernel. Independent of per-light <see cref="ShadowQuality"/> Hard/Soft.
/// Medium matches the pre-M17 8-tap rotated Poisson disk.
/// </summary>
public enum SoftShadowQuality
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>Tap count and world-radius scale for <see cref="SoftShadowQuality"/>.</summary>
public static class SoftShadowQualityUtil
{
    public static int TapCount(SoftShadowQuality quality)
    {
        if (quality == SoftShadowQuality.Low)
            return 4;
        if (quality == SoftShadowQuality.High)
            return 16;
        return 8;
    }

    public static float RadiusScale(SoftShadowQuality quality)
    {
        if (quality == SoftShadowQuality.Low)
            return 0.65f;
        if (quality == SoftShadowQuality.High)
            return 1.35f;
        return 1f;
    }
}
