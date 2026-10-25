// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// DPI-based canvas scale math shared by the package <c>CanvasScaler</c> and host tests.
/// Scale factor is <c>dpi / defaultSpriteDpi</c>, matching UGUI Constant Physical Size.
/// </summary>
public static class CanvasPhysicalScale
{
    public const float BaselineDpi = 96f;

    /// <summary>Test hook. When set and positive, <see cref="ResolveDpi"/> uses this value.</summary>
    public static float? DpiOverride { get; set; }

    public static float ComputeScaleFactor(float dpi, float defaultSpriteDpi)
        => Maths.Max(dpi / Maths.Max(defaultSpriteDpi, 1f), 0.001f);

    public static float ResolveDpi(float fallbackDpi, float contentScale)
    {
        if (DpiOverride is float overrideDpi && overrideDpi > 0f)
            return overrideDpi;
        if (contentScale > 0f)
            return contentScale * BaselineDpi;
        return Maths.Max(fallbackDpi, 1f);
    }
}
