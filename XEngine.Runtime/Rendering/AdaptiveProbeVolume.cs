// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Simplified Adaptive Probe Volume (U17-M7). Regular brick SH grid; does not replace
/// tetrahedral <see cref="LightProbeVolume"/>. Surface Cache GI stays out of scope.
/// </summary>
public sealed class AdaptiveProbeVolume
{
    public Float3 Origin;
    public Float3 Size = new(8f, 8f, 8f);
    public float ProbeSpacing = 1f;
    public SphericalHarmonicsL2[] Probes = [];

    public int CountX => AxisCount(Size.X);
    public int CountY => AxisCount(Size.Y);
    public int CountZ => AxisCount(Size.Z);
    public int ProbeCount => CountX * CountY * CountZ;

    public void FillConstant(Float3 irradiance)
    {
        int count = ProbeCount;
        Probes = new SphericalHarmonicsL2[count];
        SphericalHarmonicsL2 sh = SphericalHarmonicsL2.FromConstant(irradiance);
        for (int i = 0; i < count; i++)
            Probes[i] = sh;
    }

    public Float3 SampleIrradiance(Float3 worldPos, Float3 normal)
    {
        if (Probes == null || Probes.Length == 0 || ProbeCount == 0)
            return Float3.Zero;

        int nx = CountX, ny = CountY, nz = CountZ;
        float sx = (float)Math.Max(Size.X, ProbeSpacing);
        float sy = (float)Math.Max(Size.Y, ProbeSpacing);
        float sz = (float)Math.Max(Size.Z, ProbeSpacing);

        float u = nx <= 1 ? 0f : (float)((worldPos.X - Origin.X) / sx) * (nx - 1);
        float v = ny <= 1 ? 0f : (float)((worldPos.Y - Origin.Y) / sy) * (ny - 1);
        float w = nz <= 1 ? 0f : (float)((worldPos.Z - Origin.Z) / sz) * (nz - 1);

        int x0 = ClampIndex((int)Math.Floor(u), nx);
        int y0 = ClampIndex((int)Math.Floor(v), ny);
        int z0 = ClampIndex((int)Math.Floor(w), nz);
        int x1 = ClampIndex(x0 + 1, nx);
        int y1 = ClampIndex(y0 + 1, ny);
        int z1 = ClampIndex(z0 + 1, nz);
        float tx = Saturate(u - x0);
        float ty = Saturate(v - y0);
        float tz = Saturate(w - z0);

        SphericalHarmonicsL2 c00 = LerpSh(At(x0, y0, z0, nx, ny), At(x1, y0, z0, nx, ny), tx);
        SphericalHarmonicsL2 c10 = LerpSh(At(x0, y1, z0, nx, ny), At(x1, y1, z0, nx, ny), tx);
        SphericalHarmonicsL2 c01 = LerpSh(At(x0, y0, z1, nx, ny), At(x1, y0, z1, nx, ny), tx);
        SphericalHarmonicsL2 c11 = LerpSh(At(x0, y1, z1, nx, ny), At(x1, y1, z1, nx, ny), tx);
        SphericalHarmonicsL2 c0 = LerpSh(c00, c10, ty);
        SphericalHarmonicsL2 c1 = LerpSh(c01, c11, ty);
        return LerpSh(c0, c1, tz).Evaluate(normal);
    }

    private SphericalHarmonicsL2 At(int x, int y, int z, int nx, int ny)
    {
        int index = x + y * nx + z * nx * ny;
        if ((uint)index >= (uint)Probes.Length)
            return default;
        return Probes[index];
    }

    private int AxisCount(double size)
    {
        float spacing = ProbeSpacing;
        if (!float.IsFinite(spacing) || spacing <= 0f)
            spacing = 1f;
        int count = (int)Math.Floor(size / spacing) + 1;
        return count < 1 ? 1 : count;
    }

    private static int ClampIndex(int i, int count)
    {
        if (i < 0) return 0;
        if (i >= count) return count - 1;
        return i;
    }

    private static float Saturate(float t)
    {
        if (t < 0f) return 0f;
        if (t > 1f) return 1f;
        return t;
    }

    private static SphericalHarmonicsL2 LerpSh(in SphericalHarmonicsL2 a, in SphericalHarmonicsL2 b, float t)
        => SphericalHarmonicsL2.Lerp(a, b, t);
}
