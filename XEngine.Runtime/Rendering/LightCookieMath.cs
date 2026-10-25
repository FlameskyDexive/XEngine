// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// CPU projection helpers for light cookies. Directional uses an ortho slab along the
/// light's travel direction; spots use the same perspective cone as spot shadows;
/// points use a lat-long mapping of the light-to-fragment direction.
/// </summary>
public static class LightCookieMath
{
    public const float SpotNearPlane = 0.1f;

    /// <summary>
    /// World-up fallback used by shaders when reconstructing a spot basis.
    /// Matches the GLSL/HLSL orthonormal hint so CPU tests stay in lockstep.
    /// </summary>
    public static Float3 ReconstructUp(Float3 forward)
    {
        Float3 f = Float3.Normalize(forward);
        return MathF.Abs(f.Y) < 0.999f ? Float3.UnitY : Float3.UnitX;
    }

    /// <summary>
    /// Ortho projection along the shining direction. <paramref name="toLightDirection"/> is
    /// the lighting vector (Transform.Forward); travel is its negation.
    /// </summary>
    public static Float4x4 DirectionalWorldToCookie(Float3 position, Float3 toLightDirection, Float3 up, float size)
    {
        Float3 travel = -Float3.Normalize(toLightDirection);
        float s = MathF.Max(0.01f, size);
        Float4x4 view = Float4x4.CreateLookTo(position, travel, up);
        Float4x4 proj = Float4x4.CreateOrtho(s, s, -s, s);
        return proj * view;
    }

    public static Float4x4 SpotWorldToCookie(Float3 position, Float3 forward, Float3 up, float outerAngleDegrees, float range)
    {
        Float4x4 view = Float4x4.CreateLookTo(position, Float3.Normalize(forward), up);
        float fov = MathF.Max(0.01f, outerAngleDegrees * 2f) * MathF.PI / 180f;
        if (fov >= MathF.PI)
            fov = MathF.PI - 0.001f;
        float far = MathF.Max(SpotNearPlane + 0.01f, range);
        Float4x4 proj = Float4x4.CreatePerspectiveFov(fov, 1f, SpotNearPlane, far);
        return proj * view;
    }

    public static bool TryWorldToUv(Float4x4 worldToClip, Float3 worldPos, out Float2 uv)
    {
        Float4 clip = worldToClip * new Float4(worldPos, 1f);
        if (MathF.Abs(clip.W) < 1e-8f)
        {
            uv = default;
            return false;
        }

        float x = clip.X / clip.W;
        float y = clip.Y / clip.W;
        uv = new Float2(x * 0.5f + 0.5f, y * 0.5f + 0.5f);
        return uv.X >= 0f && uv.X <= 1f && uv.Y >= 0f && uv.Y <= 1f;
    }

    public static Float2 PointLatLongUv(Float3 lightPos, Float3 worldPos)
    {
        Float3 d = Float3.Normalize(worldPos - lightPos);
        float u = MathF.Atan2(d.X, d.Z) * (0.5f / MathF.PI) + 0.5f;
        float v = MathF.Acos(Maths.Clamp(d.Y, -1f, 1f)) / MathF.PI;
        return new Float2(u, v);
    }
}
