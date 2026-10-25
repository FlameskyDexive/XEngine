// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Vector;

namespace XEngine.Runtime.ParticleSystem;

/// <summary>Spatial curl of a Perlin vector potential, independent of particle integration.</summary>
internal static class ParticleNoiseField
{
    private const float DerivativeStep = 0.01f;

    internal static Float3 Curl(Float3 position, ParticleNoiseQuality quality)
    {
        // CNoise repeats every 289 lattice cells. Keep coordinates bounded before
        // adding channel offsets so long-running scrolling retains subcell precision.
        Float3 p = new(Wrap(position.X), Wrap(position.Y), Wrap(position.Z));
        Float3 gradientX = Gradient(p);
        Float3 gradientY = quality >= ParticleNoiseQuality.Medium
            ? Gradient(p + new Float3(71.3f, 19.7f, 113.1f)) : gradientX;
        Float3 gradientZ = quality >= ParticleNoiseQuality.High
            ? Gradient(p + new Float3(149.5f, 47.9f, 61.7f)) : gradientX;
        return new Float3(gradientZ.Y - gradientY.Z,
            gradientX.Z - gradientZ.X, gradientY.X - gradientX.Y);
    }

    internal static Float3 CurlFiniteDifference(Float3 position, ParticleNoiseQuality quality)
    {
        Float3 p = new(Wrap(position.X), Wrap(position.Y), Wrap(position.Z));
        Float3 gradientX = GradientFiniteDifference(p);
        Float3 gradientY = quality >= ParticleNoiseQuality.Medium
            ? GradientFiniteDifference(p + new Float3(71.3f, 19.7f, 113.1f)) : gradientX;
        Float3 gradientZ = quality >= ParticleNoiseQuality.High
            ? GradientFiniteDifference(p + new Float3(149.5f, 47.9f, 61.7f)) : gradientX;
        return new Float3(gradientZ.Y - gradientY.Z,
            gradientX.Z - gradientZ.X, gradientY.X - gradientX.Y);
    }

    private static Float3 GradientFiniteDifference(Float3 p)
    {
        const float inverseDiameter = 0.5f / DerivativeStep;
        Float3 x = new(DerivativeStep, 0f, 0f);
        Float3 y = new(0f, DerivativeStep, 0f);
        Float3 z = new(0f, 0f, DerivativeStep);
        return new Float3(XEngine.Vector.Noise.CNoise(p + x) - XEngine.Vector.Noise.CNoise(p - x),
            XEngine.Vector.Noise.CNoise(p + y) - XEngine.Vector.Noise.CNoise(p - y),
            XEngine.Vector.Noise.CNoise(p + z) - XEngine.Vector.Noise.CNoise(p - z)) * inverseDiameter;
    }

    // Analytic derivative of Vector.Noise.CNoise: identical lattice permutation,
    // normalized gradients, quintic interpolation and 2.2 output scale. Each value
    // carries its gradient in xyz and scalar value in w through the interpolation.
    private static Float3 Gradient(Float3 p)
    {
        Float3 integer = Maths.Floor(p);
        Float3 pi0 = integer - Maths.Floor(integer * (1f / 289f)) * 289f;
        Float3 pi1 = pi0 + Float3.One;
        pi1 -= Maths.Floor(pi1 * (1f / 289f)) * 289f;
        Float3 pf0 = Maths.Frac(p);
        Float3 pf1 = pf0 - Float3.One;
        Float4 ix = new(pi0.X, pi1.X, pi0.X, pi1.X);
        Float4 iy = new(pi0.Y, pi0.Y, pi1.Y, pi1.Y);
        Float4 ixy = Permute(Permute(ix) + iy);
        Float4 ixy0 = Permute(ixy + new Float4(pi0.Z));
        Float4 ixy1 = Permute(ixy + new Float4(pi1.Z));

        GradientAxes(ixy0, out Float4 gx0, out Float4 gy0, out Float4 gz0);
        GradientAxes(ixy1, out Float4 gx1, out Float4 gy1, out Float4 gz1);
        Float3 g000 = NormalizeGradient(new Float3(gx0.X, gy0.X, gz0.X));
        Float3 g100 = NormalizeGradient(new Float3(gx0.Y, gy0.Y, gz0.Y));
        Float3 g010 = NormalizeGradient(new Float3(gx0.Z, gy0.Z, gz0.Z));
        Float3 g110 = NormalizeGradient(new Float3(gx0.W, gy0.W, gz0.W));
        Float3 g001 = NormalizeGradient(new Float3(gx1.X, gy1.X, gz1.X));
        Float3 g101 = NormalizeGradient(new Float3(gx1.Y, gy1.Y, gz1.Y));
        Float3 g011 = NormalizeGradient(new Float3(gx1.Z, gy1.Z, gz1.Z));
        Float3 g111 = NormalizeGradient(new Float3(gx1.W, gy1.W, gz1.W));

        Float4 n000 = new(g000, Float3.Dot(g000, pf0));
        Float4 n100 = new(g100, Float3.Dot(g100, new Float3(pf1.X, pf0.Y, pf0.Z)));
        Float4 n010 = new(g010, Float3.Dot(g010, new Float3(pf0.X, pf1.Y, pf0.Z)));
        Float4 n110 = new(g110, Float3.Dot(g110, new Float3(pf1.X, pf1.Y, pf0.Z)));
        Float4 n001 = new(g001, Float3.Dot(g001, new Float3(pf0.X, pf0.Y, pf1.Z)));
        Float4 n101 = new(g101, Float3.Dot(g101, new Float3(pf1.X, pf0.Y, pf1.Z)));
        Float4 n011 = new(g011, Float3.Dot(g011, new Float3(pf0.X, pf1.Y, pf1.Z)));
        Float4 n111 = new(g111, Float3.Dot(g111, pf1));
        Float3 fade = pf0 * pf0 * pf0 * (pf0 * (pf0 * 6f - 15f) + 10f);
        Float3 derivative = 30f * pf0 * pf0 * (pf0 * (pf0 - 2f) + 1f);
        Float3 dz = new(0f, 0f, derivative.Z);
        Float4 n00 = Mix(n000, n001, fade.Z, dz);
        Float4 n10 = Mix(n100, n101, fade.Z, dz);
        Float4 n01 = Mix(n010, n011, fade.Z, dz);
        Float4 n11 = Mix(n110, n111, fade.Z, dz);
        Float3 dy = new(0f, derivative.Y, 0f);
        Float4 n0 = Mix(n00, n01, fade.Y, dy);
        Float4 n1 = Mix(n10, n11, fade.Y, dy);
        Float4 result = Mix(n0, n1, fade.X, new Float3(derivative.X, 0f, 0f));
        return new Float3(result.X, result.Y, result.Z) * 2.2f;
    }

    private static Float4 Mix(Float4 a, Float4 b, float t, Float3 derivative)
    {
        Float4 result = Maths.Lerp(a, b, t);
        Float3 extra = derivative * (b.W - a.W);
        result.X += extra.X;
        result.Y += extra.Y;
        result.Z += extra.Z;
        return result;
    }

    private static Float4 Permute(Float4 value)
    {
        value = (34f * value + 10f) * value;
        return value - Maths.Floor(value * (1f / 289f)) * 289f;
    }

    private static void GradientAxes(Float4 hash, out Float4 gx, out Float4 gy, out Float4 gz)
    {
        gx = hash * (1f / 7f);
        gy = Maths.Frac(Maths.Floor(gx) * (1f / 7f)) - 0.5f;
        gx = Maths.Frac(gx);
        gz = new Float4(0.5f) - Maths.Abs(gx) - Maths.Abs(gy);
        Float4 sign = Maths.Step(gz, new Float4(0f));
        gx -= sign * (Maths.Step(new Float4(0f), gx) - 0.5f);
        gy -= sign * (Maths.Step(new Float4(0f), gy) - 0.5f);
    }

    private static Float3 NormalizeGradient(Float3 value) =>
        value * (1.79284291400159f - 0.85373472095314f * Float3.Dot(value, value));

    private static float Wrap(float value) => value - MathF.Floor(value / 289f) * 289f;
}
