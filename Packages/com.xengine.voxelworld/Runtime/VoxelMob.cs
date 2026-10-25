using System;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

public sealed class VoxelMob
{
    public Float3 Position = new(12f, 16f, 8f);
    public int Health = 20;
    public int MaxHealth = 20;
    public Float3 Extents = new(0.4f, 0.9f, 0.3f);

    public bool IsAlive => Health > 0;

    public int Hurt(int damage)
    {
        if (damage <= 0 || !IsAlive)
            return Health;
        Health = Math.Max(0, Health - damage);
        return Health;
    }

    public bool RayHits(Float3 origin, Float3 direction, float maxDistance, out float distance)
    {
        distance = 0f;
        if (!IsAlive)
            return false;
        Float3 min = Position - Extents;
        Float3 max = Position + Extents;
        return RayAabb(origin, direction, min, max, maxDistance, out distance);
    }

    public static bool RayAabb(Float3 origin, Float3 direction, Float3 min, Float3 max, float maxDistance, out float distance)
    {
        distance = 0f;
        float tMin = 0f;
        float tMax = maxDistance;
        if (!Slab(origin.X, direction.X, min.X, max.X, ref tMin, ref tMax))
            return false;
        if (!Slab(origin.Y, direction.Y, min.Y, max.Y, ref tMin, ref tMax))
            return false;
        if (!Slab(origin.Z, direction.Z, min.Z, max.Z, ref tMin, ref tMax))
            return false;
        distance = tMin;
        return tMax >= tMin && tMin <= maxDistance;
    }

    private static bool Slab(float origin, float dir, float min, float max, ref float tMin, ref float tMax)
    {
        if (MathF.Abs(dir) < 1e-8f)
            return origin >= min && origin <= max;
        float inv = 1f / dir;
        float t0 = (min - origin) * inv;
        float t1 = (max - origin) * inv;
        if (t0 > t1)
            (t0, t1) = (t1, t0);
        tMin = MathF.Max(tMin, t0);
        tMax = MathF.Min(tMax, t1);
        return tMax >= tMin;
    }
}
