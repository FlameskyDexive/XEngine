using System;

using XEngine.Vector;

namespace XEngine.Editor.GUI.Panels;

/// <summary>
/// A phone or tablet the Game view can frame the game image in. All measurements are in the device's
/// own portrait pixels, so the whole body scales as one unit when it is fitted into the panel.
/// </summary>
/// <param name="Name">Label shown in the device picker.</param>
/// <param name="Width">Screen width in portrait orientation.</param>
/// <param name="Height">Screen height in portrait orientation.</param>
/// <param name="Bezel">Body border thickness around the screen.</param>
/// <param name="CornerRadius">Screen corner rounding.</param>
/// <param name="NotchWidth">Width of the top cutout, or 0 for a device without one.</param>
/// <param name="NotchHeight">Height of the top cutout / status area.</param>
/// <param name="HomeIndicator">Height of the bottom gesture area, or 0 for a home-button device.</param>
internal sealed record GameViewDevice(
    string Name,
    int Width,
    int Height,
    float Bezel,
    float CornerRadius,
    float NotchWidth,
    float NotchHeight,
    float HomeIndicator)
{
    /// <summary>Longest render-target edge. Device-native resolutions get downsampled to stay cheap.</summary>
    private const int MaxRenderEdge = 1600;

    /// <summary>Screen size including the body border, in device pixels.</summary>
    public Float2 BodySize(bool landscape) => landscape
        ? new Float2(Height + Bezel * 2f, Width + Bezel * 2f)
        : new Float2(Width + Bezel * 2f, Height + Bezel * 2f);

    /// <summary>
    /// Render-target size for this device. The aspect ratio is exact (that is what gameplay layout
    /// depends on) but the resolution is capped, since a 1179x2556 target costs the editor a lot for
    /// pixels that get scaled down into a small panel anyway.
    /// </summary>
    public (int width, int height) RenderSize(bool landscape)
    {
        int w = landscape ? Height : Width;
        int h = landscape ? Width : Height;

        int longest = Math.Max(w, h);
        if (longest > MaxRenderEdge)
        {
            float k = MaxRenderEdge / (float)longest;
            w = Math.Max(1, (int)MathF.Round(w * k));
            h = Math.Max(1, (int)MathF.Round(h * k));
        }
        return (w, h);
    }
}

/// <summary>The device presets offered by the Game view's simulator mode.</summary>
internal static class GameViewDevices
{
    public static readonly GameViewDevice[] All =
    {
        new("iPhone 15 Pro (1179x2556)", 1179, 2556, 42f, 160f, 300f, 118f, 130f),
        new("iPhone SE (750x1334)", 750, 1334, 70f, 24f, 0f, 40f, 0f),
        new("Pixel 8 (1080x2400)", 1080, 2400, 34f, 120f, 0f, 96f, 48f),
        new("Galaxy S23 (1080x2340)", 1080, 2340, 30f, 110f, 0f, 88f, 44f),
        new("iPad Pro 11\" (1668x2388)", 1668, 2388, 96f, 90f, 0f, 48f, 80f),
        new("iPad mini (1488x2266)", 1488, 2266, 84f, 80f, 0f, 48f, 76f),
    };
}
