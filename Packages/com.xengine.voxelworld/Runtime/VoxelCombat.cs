using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>LMB prefers a live mob AABB, otherwise breaks the aimed voxel.</summary>
public sealed class VoxelCombat
{
    public int LastDamage { get; private set; }

    public bool TryAttack(
        VoxelWorld? world,
        IReadOnlyList<VoxelMob> mobs,
        Float3 origin,
        Float3 direction,
        float maxDistance,
        int damage = 4)
    {
        LastDamage = 0;
        VoxelMob? best = null;
        float bestDist = maxDistance;
        for (int i = 0; i < mobs.Count; i++)
        {
            if (!mobs[i].RayHits(origin, direction, maxDistance, out float dist))
                continue;
            if (dist >= bestDist)
                continue;
            bestDist = dist;
            best = mobs[i];
        }

        if (best != null)
        {
            int before = best.Health;
            best.Hurt(damage);
            LastDamage = before - best.Health;
            return LastDamage > 0;
        }

        return world != null && world.TryBreak(origin, direction, maxDistance);
    }
}
