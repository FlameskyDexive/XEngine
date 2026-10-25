// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Editor.GUI.SceneView;

/// <summary>
/// ★ M10 — Pure math behind the radius / distance / cone-angle scene handles. Kept free of input
/// and rendering so the drag behaviour can be unit-tested without an editor or a camera.
/// </summary>
public static class HandleMath
{
    /// <summary>
    /// Distance along <paramref name="axis"/> from <paramref name="origin"/> of the point closest to
    /// <paramref name="ray"/> — the classic closest-approach of two lines. Returns 0 when the axis
    /// and the ray are parallel (no well-defined answer).
    /// </summary>
    public static float ClosestAxisDistance(Float3 origin, Float3 axis, Ray ray)
    {
        Float3 a = Float3.Normalize(axis);
        Float3 d = Float3.Normalize(ray.Direction);
        float b = Float3.Dot(a, d);
        float denom = 1f - b * b;
        if (denom < 1e-6f) return 0f;

        Float3 w0 = origin - ray.Origin;
        float dw = Float3.Dot(a, w0);
        float ew = Float3.Dot(d, w0);
        return (b * ew - dw) / denom;
    }

    /// <summary>Radius implied by dragging a handle that slides along <paramref name="axis"/> from <paramref name="center"/>.</summary>
    public static float RadiusFromRay(Float3 center, Float3 axis, Ray ray, float min = 0.001f)
    {
        float t = ClosestAxisDistance(center, axis, ray);
        return t < min ? min : t;
    }

    /// <summary>Perpendicular distance from <paramref name="point"/> to <paramref name="ray"/> (used for handle picking).</summary>
    public static float DistanceToRay(Float3 point, Ray ray)
    {
        Float3 d = Float3.Normalize(ray.Direction);
        Float3 v = point - ray.Origin;
        float t = Float3.Dot(v, d);
        Float3 closest = ray.Origin + d * t;
        return Float3.Distance(point, closest);
    }

    /// <summary>Full cone angle (degrees) whose rim radius is <paramref name="rimRadius"/> at <paramref name="range"/>.</summary>
    public static float ConeAngleFromRimRadius(float range, float rimRadius, float maxAngle = 179f)
    {
        if (range <= 1e-4f) return maxAngle;
        if (rimRadius < 0f) rimRadius = 0f;
        float angle = 2f * MathF.Atan2(rimRadius, range) * (180f / MathF.PI);
        if (angle > maxAngle) angle = maxAngle;
        if (angle < 0.1f) angle = 0.1f;
        return angle;
    }

    /// <summary>Rim radius of a cone with full angle <paramref name="angleDegrees"/> at <paramref name="range"/>.</summary>
    public static float ConeRimRadius(float range, float angleDegrees)
        => range * MathF.Tan(angleDegrees * 0.5f * (MathF.PI / 180f));
}
