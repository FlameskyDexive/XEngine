// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;

namespace XEngine.Terrain;

/// <summary>
/// Shared brush weight: circular falloff, optional rotation, luminance mask, and strength curve.
/// Editor strokes and tests call this; it allocates nothing on the sample path.
/// </summary>
public static class TerrainBrushSampler
{
    /// <summary>
    /// Weight at a heightmap/splat pixel relative to the brush centre.
    /// <paramref name="dx"/>/<paramref name="dz"/> are in the same units as <paramref name="radius"/>.
    /// Normalized distance 0 is the centre, 1 is the edge.
    /// </summary>
    public static float Weight(
        float dx,
        float dz,
        float radius,
        float rotationRadians,
        float falloff,
        AnimationCurve? strengthCurve,
        ReadOnlySpan<float> maskLuma,
        int maskWidth,
        int maskHeight)
    {
        if (radius <= 1e-5f)
            return 0f;

        Rotate(dx, dz, rotationRadians, out float rx, out float rz);
        float dist = MathF.Sqrt(rx * rx + rz * rz);
        if (dist > radius)
            return 0f;

        float t = dist / radius;
        float w = 1f - SmoothStep(1f - Math.Clamp(falloff, 0f, 1f), 1f, t);

        if (maskWidth > 0 && maskHeight > 0 && maskLuma.Length >= maskWidth * maskHeight)
            w *= SampleMask(maskLuma, maskWidth, maskHeight, rx / radius, rz / radius);

        if (strengthCurve != null)
            w *= Math.Clamp(strengthCurve.Evaluate(t), 0f, 1f);

        return w < 0f ? 0f : w;
    }

    public static void Rotate(float dx, float dz, float rotationRadians, out float rx, out float rz)
    {
        if (MathF.Abs(rotationRadians) < 1e-6f)
        {
            rx = dx;
            rz = dz;
            return;
        }

        float c = MathF.Cos(rotationRadians);
        float s = MathF.Sin(rotationRadians);
        rx = dx * c - dz * s;
        rz = dx * s + dz * c;
    }

    /// <summary>
    /// Bilinear sample of a luminance mask. <paramref name="localX"/>/<paramref name="localZ"/> are
    /// brush-space coordinates in [-1, 1] (centre is 0).
    /// </summary>
    public static float SampleMask(ReadOnlySpan<float> luma, int width, int height, float localX, float localZ)
    {
        if (width <= 0 || height <= 0 || luma.Length < width * height)
            return 1f;

        float u = Math.Clamp(localX * 0.5f + 0.5f, 0f, 1f);
        float v = Math.Clamp(localZ * 0.5f + 0.5f, 0f, 1f);
        float px = u * (width - 1);
        float pz = v * (height - 1);
        int x0 = (int)MathF.Floor(px);
        int z0 = (int)MathF.Floor(pz);
        int x1 = Math.Min(x0 + 1, width - 1);
        int z1 = Math.Min(z0 + 1, height - 1);
        float fx = px - x0;
        float fz = pz - z0;
        float s00 = luma[z0 * width + x0];
        float s10 = luma[z0 * width + x1];
        float s01 = luma[z1 * width + x0];
        float s11 = luma[z1 * width + x1];
        return ((s00 * (1f - fx) + s10 * fx) * (1f - fz)) + ((s01 * (1f - fx) + s11 * fx) * fz);
    }

    public static float SmoothStep(float edge0, float edge1, float x)
    {
        float span = edge1 - edge0;
        if (MathF.Abs(span) < 1e-6f)
            return x >= edge1 ? 1f : 0f;
        float t = Math.Clamp((x - edge0) / span, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
