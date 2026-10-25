using System;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Amanatides–Woo voxel DDA. <paramref name="isSolid"/> is queried per integer cell.</summary>
public static class VoxelDda
{
    public static bool Cast(
        Func<Int3, bool> isSolid,
        Float3 origin,
        Float3 direction,
        float maxDistance,
        out VoxelHit hit)
    {
        ArgumentNullException.ThrowIfNull(isSolid);
        hit = default;
        if (maxDistance <= 0f)
            return false;

        float length = Float3.Length(direction);
        if (length < 1e-8f)
            return false;
        Float3 dir = direction / length;

        int x = (int)MathF.Floor(origin.X);
        int y = (int)MathF.Floor(origin.Y);
        int z = (int)MathF.Floor(origin.Z);

        int stepX = dir.X > 0f ? 1 : dir.X < 0f ? -1 : 0;
        int stepY = dir.Y > 0f ? 1 : dir.Y < 0f ? -1 : 0;
        int stepZ = dir.Z > 0f ? 1 : dir.Z < 0f ? -1 : 0;

        float tDeltaX = stepX == 0 ? float.PositiveInfinity : MathF.Abs(1f / dir.X);
        float tDeltaY = stepY == 0 ? float.PositiveInfinity : MathF.Abs(1f / dir.Y);
        float tDeltaZ = stepZ == 0 ? float.PositiveInfinity : MathF.Abs(1f / dir.Z);

        float tMaxX = stepX == 0 ? float.PositiveInfinity : ((stepX > 0 ? x + 1 : x) - origin.X) / dir.X;
        float tMaxY = stepY == 0 ? float.PositiveInfinity : ((stepY > 0 ? y + 1 : y) - origin.Y) / dir.Y;
        float tMaxZ = stepZ == 0 ? float.PositiveInfinity : ((stepZ > 0 ? z + 1 : z) - origin.Z) / dir.Z;

        float t = 0f;
        Int3 enteredFrom = default;
        const int maxSteps = 512;
        for (int i = 0; i < maxSteps && t <= maxDistance; i++)
        {
            var cell = new Int3(x, y, z);
            if (t > 0f && isSolid(cell))
            {
                hit = new VoxelHit(cell, enteredFrom, t);
                return true;
            }

            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                t = tMaxX;
                enteredFrom = new Int3(-stepX, 0, 0);
                x += stepX;
                tMaxX += tDeltaX;
            }
            else if (tMaxY <= tMaxZ)
            {
                t = tMaxY;
                enteredFrom = new Int3(0, -stepY, 0);
                y += stepY;
                tMaxY += tDeltaY;
            }
            else
            {
                t = tMaxZ;
                enteredFrom = new Int3(0, 0, -stepZ);
                z += stepZ;
                tMaxZ += tDeltaZ;
            }
        }

        return false;
    }
}
