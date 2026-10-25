// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Axis-aligned XY occluder for <see cref="Light2D"/> (M22). Full size is <see cref="Size"/> scaled
/// by lossy XY scale. Used by both CPU <see cref="Light2D.EvaluateAt"/> and the GPU shadow mask.
/// </summary>
[AddComponentMenu("Rendering/Shadow Caster 2D")]
[ComponentIcon("\uf1ad")]
public sealed class ShadowCaster2D : MonoBehaviour
{
    /// <summary>Full local XY extents of the caster box.</summary>
    [SerializeField] public Float2 Size = new(1f, 1f);

    [SerializeField] public bool CastsShadows = true;

    /// <summary>When true, samples inside the box are treated as shadowed.</summary>
    [SerializeField] public bool SelfShadows;

    public override void OnEnable() => ShadowCaster2DSystem.Instance.Register(this);

    public override void OnDisable() => ShadowCaster2DSystem.Instance.Unregister(this);

    public override void OnValidate()
    {
        Size = new Float2(
            MathF.Max(0.01f, float.IsFinite(Size.X) ? Size.X : 1f),
            MathF.Max(0.01f, float.IsFinite(Size.Y) ? Size.Y : 1f));
    }

    public void GetWorldAabb(out Float2 min, out Float2 max)
    {
        OnValidate();
        Float3 pos = Transform.Position;
        Float3 lossy = Transform.LossyScale;
        float hx = MathF.Abs(lossy.X) * Size.X * 0.5f;
        float hy = MathF.Abs(lossy.Y) * Size.Y * 0.5f;
        min = new Float2(pos.X - hx, pos.Y - hy);
        max = new Float2(pos.X + hx, pos.Y + hy);
    }

    /// <summary>
    /// True when the segment from <paramref name="lightXy"/> to <paramref name="sampleXy"/> hits this box.
    /// Interior samples are shadowed only when <see cref="SelfShadows"/> is set.
    /// </summary>
    public bool Occludes(Float2 lightXy, Float2 sampleXy)
    {
        if (!CastsShadows)
            return false;

        GetWorldAabb(out Float2 min, out Float2 max);
        bool sampleInside = Contains(sampleXy, min, max);
        if (sampleInside)
            return SelfShadows;
        if (Contains(lightXy, min, max))
            return false;
        return SegmentHitsAabb(lightXy, sampleXy, min, max);
    }

    private static bool Contains(Float2 p, Float2 min, Float2 max)
        => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;

    private static bool SegmentHitsAabb(Float2 p0, Float2 p1, Float2 min, Float2 max)
    {
        Float2 d = p1 - p0;
        float t0 = 0f;
        float t1 = 1f;
        if (!ClipAxis(p0.X, d.X, min.X, max.X, ref t0, ref t1))
            return false;
        if (!ClipAxis(p0.Y, d.Y, min.Y, max.Y, ref t0, ref t1))
            return false;
        return t1 >= t0;
    }

    private static bool ClipAxis(float origin, float delta, float min, float max, ref float t0, ref float t1)
    {
        if (MathF.Abs(delta) < 1e-8f)
            return origin >= min && origin <= max;

        float inv = 1f / delta;
        float a = (min - origin) * inv;
        float b = (max - origin) * inv;
        if (a > b)
        {
            float tmp = a;
            a = b;
            b = tmp;
        }

        t0 = MathF.Max(t0, a);
        t1 = MathF.Min(t1, b);
        return t0 <= t1;
    }
}
