// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Terrain;

/// <summary>Unity-style wind source. Directional zones blow along local +Z; spherical zones radiate from the origin and fall off with <see cref="Radius"/>.</summary>
[AddComponentMenu("Terrain/Wind Zone")]
[ExecuteAlways]
[ComponentIcon("\uf72e")]
public sealed class WindZone : MonoBehaviour
{
    public enum WindZoneMode
    {
        Directional,
        Spherical,
    }

    public WindZoneMode Mode = WindZoneMode.Directional;
    public float WindMain = 1f;
    public float WindTurbulence = 0.5f;
    public float WindPulseMagnitude = 0.5f;
    public float WindPulseFrequency = 0.25f;
    public float Radius = 50f;

    private static readonly List<WindZone> s_active = [];

    /// <summary>Enabled wind zones in loaded scenes.</summary>
    public static IReadOnlyList<WindZone> ActiveWindZones => s_active;

    public override void OnEnable()
    {
        base.OnEnable();
        if (!s_active.Contains(this))
            s_active.Add(this);
    }

    public override void OnDisable()
    {
        s_active.Remove(this);
        base.OnDisable();
    }

    /// <summary>
    /// Combine active zones at <paramref name="worldPosition"/>.
    /// Returns false when nothing is registered so callers can keep material defaults.
    /// </summary>
    public static bool TrySample(Float3 worldPosition, out Float3 direction, out float strength, out float speed)
        => TrySample(worldPosition, Time.TimeSinceStartup, out direction, out strength, out speed);

    public static bool TrySample(Float3 worldPosition, float time, out Float3 direction, out float strength, out float speed)
    {
        direction = Float3.UnitZ;
        strength = 0f;
        speed = 1.5f;
        if (s_active.Count == 0)
            return false;

        Float3 dirAcc = Float3.Zero;
        float strAcc = 0f;
        float spdAcc = 0f;
        float weightAcc = 0f;

        for (int i = 0; i < s_active.Count; i++)
        {
            WindZone zone = s_active[i];
            if (zone == null || zone.IsDisposed || !zone.EnabledInHierarchy)
                continue;
            if (!zone.TryContribute(worldPosition, time, out Float3 dir, out float str, out float spd, out float w))
                continue;
            dirAcc += dir * w;
            strAcc += str * w;
            spdAcc += spd * w;
            weightAcc += w;
        }

        if (weightAcc <= 1e-5f)
            return false;

        float lenSq = Float3.LengthSquared(dirAcc);
        direction = lenSq > 1e-8f ? dirAcc / MathF.Sqrt(lenSq) : Float3.UnitZ;
        strength = MathF.Max(0f, strAcc / weightAcc);
        speed = MathF.Max(0.01f, spdAcc / weightAcc);
        return true;
    }

    /// <summary>Overwrite grass <c>_WindStrength</c> / <c>_WindSpeed</c> when a zone is active.</summary>
    public static void ApplyToGrassMaterial(Material mat, Float3 worldPosition)
    {
        if (mat == null)
            return;
        if (!TrySample(worldPosition, out _, out float strength, out float speed))
            return;
        mat.SetFloat("_WindStrength", strength);
        mat.SetFloat("_WindSpeed", speed);
    }

    /// <summary>Rotate a world matrix by a small wind-driven lean. No-op when no zone is active.</summary>
    public static Float4x4 ApplySway(Float4x4 worldMatrix, Float3 worldPosition, float bendFactor)
    {
        if (bendFactor <= 1e-4f)
            return worldMatrix;
        if (!TrySample(worldPosition, out Float3 dir, out float strength, out float speed))
            return worldMatrix;
        if (strength <= 1e-4f)
            return worldMatrix;

        Float3 axis = Float3.Cross(Float3.UnitY, dir);
        float axisLenSq = Float3.LengthSquared(axis);
        axis = axisLenSq > 1e-8f ? axis / MathF.Sqrt(axisLenSq) : Float3.UnitX;
        float phase = worldPosition.X * 0.7f + worldPosition.Z * 0.4f;
        float angle = MathF.Sin(Time.TimeSinceStartup * speed + phase) * strength * bendFactor * 0.12f;
        return worldMatrix * Float4x4.FromAxisAngle(axis, angle);
    }

    private bool TryContribute(Float3 worldPosition, float time, out Float3 direction, out float strength, out float speed, out float weight)
    {
        direction = Float3.UnitZ;
        strength = 0f;
        speed = 1.5f;
        weight = 0f;

        Float3 origin = Transform.Position;
        if (Mode == WindZoneMode.Spherical)
        {
            Float3 delta = worldPosition - origin;
            float dist = Float3.Length(delta);
            float radius = MathF.Max(Radius, 1e-3f);
            if (dist >= radius)
                return false;
            float t = dist / radius;
            weight = 1f - t * t;
            direction = dist > 1e-4f ? delta / dist : Float3.UnitY;
        }
        else
        {
            weight = 1f;
            Float3 forward = Transform.Forward;
            float flen = Float3.Length(forward);
            direction = flen > 1e-4f ? forward / flen : Float3.UnitZ;
        }

        float pulse = 1f + WindPulseMagnitude * MathF.Sin(time * WindPulseFrequency * (MathF.PI * 2f));
        float turb = 1f + WindTurbulence * 0.25f * MathF.Sin(time * 5.3f);
        float envelope = Mode == WindZoneMode.Spherical ? weight : 1f;
        strength = MathF.Max(0f, WindMain * pulse * turb * envelope);
        speed = 0.5f + WindPulseFrequency * 4f;
        return weight > 1e-4f;
    }
}
