using System;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Continuous voxel AABB sweep. Feet are the origin; unloaded sections are
/// barriers until streaming catches up, independently of deferred physics mesh cooking.</summary>
public static class VoxelCharacterCollision
{
    private const float Skin = 0.002f;

    public static Float3 Move(VoxelWorldGrid grid, Float3 position, Float3 motion,
        float radius, float height, out bool grounded)
    {
        Float3 extents = new(radius, height * 0.5f, radius);
        Float3 center = position + new Float3(0, extents.Y, 0);
        Sweep(grid, ref center, extents, 0, motion.X);
        Sweep(grid, ref center, extents, 2, motion.Z);
        float before = center.Y;
        Sweep(grid, ref center, extents, 1, motion.Y);
        grounded = motion.Y < 0 && center.Y > before + motion.Y + 0.0001f;
        // Probe support even on an idle frame, without snapping across a ledge.
        Float3 probe = center;
        Sweep(grid, ref probe, extents, 1, -0.025f);
        grounded |= probe.Y > center.Y - 0.0249f;
        return center - new Float3(0, extents.Y, 0);
    }

    private static void Sweep(VoxelWorldGrid grid, ref Float3 center, Float3 extents, int axis, float delta)
    {
        if (delta == 0) return;
        float c = Get(center, axis), e = Get(extents, axis);
        int sign = delta > 0 ? 1 : -1;
        float edge = c + sign * e;
        int start = (int)MathF.Floor(edge - sign * Skin);
        int end = (int)MathF.Floor(edge + delta);
        int a = (axis + 1) % 3, b = (axis + 2) % 3;
        int a0 = (int)MathF.Floor(Get(center, a) - Get(extents, a) + Skin);
        int a1 = (int)MathF.Floor(Get(center, a) + Get(extents, a) - Skin);
        int b0 = (int)MathF.Floor(Get(center, b) - Get(extents, b) + Skin);
        int b1 = (int)MathF.Floor(Get(center, b) + Get(extents, b) - Skin);
        for (int slab = start; sign > 0 ? slab <= end : slab >= end; slab += sign)
            for (int u = a0; u <= a1; u++)
                for (int v = b0; v <= b1; v++)
                {
                    int x = axis == 0 ? slab : a == 0 ? u : v;
                    int y = axis == 1 ? slab : a == 1 ? u : v;
                    int z = axis == 2 ? slab : a == 2 ? u : v;
                    var cell = new Int3(x, y, z);
                    bool known = grid.TryGetVoxel(cell, out var voxel);
                    // Air above the generated world remains traversable; its lower edge is bedrock.
                    if (known ? voxel.IsEmpty : y > (grid.MaxChunkY + 1) * VoxelCoords.ChunkSize - 1)
                        continue;
                    float allowed = (sign > 0 ? slab - Skin : slab + 1 + Skin) - edge;
                    delta = sign > 0 ? MathF.Min(delta, MathF.Max(0, allowed)) : MathF.Max(delta, MathF.Min(0, allowed));
                    Set(ref center, axis, c + delta);
                    return;
                }
        Set(ref center, axis, c + delta);
    }

    private static float Get(Float3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    private static void Set(ref Float3 v, int axis, float value)
    {
        if (axis == 0) v.X = value;
        else if (axis == 1) v.Y = value;
        else v.Z = value;
    }
}
