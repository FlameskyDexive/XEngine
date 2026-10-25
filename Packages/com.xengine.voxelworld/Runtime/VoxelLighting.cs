using System;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Sunlight columns plus a 6-connected flood (sun and optional torch emit).</summary>
public static class VoxelLighting
{
    public const byte Max = 15;

    public static void Bake(ReadOnlySpan<Voxel> voxels, Span<byte> light, Func<ushort, byte>? emit = null)
    {
        if (voxels.Length < VoxelCoords.ChunkVoxelCount)
            throw new ArgumentException("Voxel buffer too small.", nameof(voxels));
        if (light.Length < VoxelCoords.ChunkVoxelCount)
            throw new ArgumentException("Light buffer too small.", nameof(light));

        light.Clear();
        var queue = new int[VoxelCoords.ChunkVoxelCount];
        int head = 0, tail = 0;

        for (int x = 0; x < VoxelCoords.ChunkSize; x++)
        {
            for (int z = 0; z < VoxelCoords.ChunkSize; z++)
            {
                bool sky = true;
                for (int y = VoxelCoords.ChunkSize - 1; y >= 0; y--)
                {
                    int index = VoxelCoords.Index(x, y, z);
                    if (!voxels[index].IsEmpty)
                    {
                        sky = false;
                        byte torch = emit != null ? emit(voxels[index].TypeIndex) : (byte)0;
                        if (torch > 0)
                        {
                            light[index] = torch;
                            queue[tail++] = index;
                        }
                        continue;
                    }

                    if (sky)
                    {
                        light[index] = Max;
                        queue[tail++] = index;
                    }
                }
            }
        }

        while (head < tail)
        {
            int index = queue[head++];
            byte value = light[index];
            if (value <= 1)
                continue;
            Int3 p = VoxelCoords.LocalFromIndex(index);
            TrySpread(voxels, light, queue, ref tail, p.X + 1, p.Y, p.Z, value);
            TrySpread(voxels, light, queue, ref tail, p.X - 1, p.Y, p.Z, value);
            TrySpread(voxels, light, queue, ref tail, p.X, p.Y + 1, p.Z, value);
            TrySpread(voxels, light, queue, ref tail, p.X, p.Y - 1, p.Z, value);
            TrySpread(voxels, light, queue, ref tail, p.X, p.Y, p.Z + 1, value);
            TrySpread(voxels, light, queue, ref tail, p.X, p.Y, p.Z - 1, value);
        }
    }

    public static Color FaceColor(byte light, int occlusion)
    {
        float sun = light / (float)Max;
        float ao = 1f - (0.22f * Math.Clamp(occlusion, 0, 3));
        float v = Math.Clamp(sun * ao, 0.05f, 1f);
        return new Color(v, v, v, 1f);
    }

    private static void TrySpread(
        ReadOnlySpan<Voxel> voxels,
        Span<byte> light,
        int[] queue,
        ref int tail,
        int x,
        int y,
        int z,
        byte from)
    {
        if ((uint)x >= VoxelCoords.ChunkSize || (uint)y >= VoxelCoords.ChunkSize || (uint)z >= VoxelCoords.ChunkSize)
            return;
        if (!voxels[VoxelCoords.Index(x, y, z)].IsEmpty)
            return;
        int index = VoxelCoords.Index(x, y, z);
        byte next = (byte)(from - 1);
        if (light[index] >= next)
            return;
        light[index] = next;
        queue[tail++] = index;
    }
}
