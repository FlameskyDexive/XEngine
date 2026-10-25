using System;
using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

public struct VoxelDebris
{
    public Float3 Position;
    public Float3 Velocity;
    public Color Color;
    public float Life;
    public float Size;
}

/// <summary>Owned break chips (not Kronnect DamageParticle). CPU Euler pool.</summary>
public sealed class VoxelBreakFx
{
    public const int ChipsPerBreak = 8;
    public const float Lifetime = 0.7f;
    public const float Gravity = 18f;

    public readonly List<VoxelDebris> Alive = [];

    public int Burst(Float3 cellCenter, Color color, int seed = 1)
    {
        int before = Alive.Count;
        for (int i = 0; i < ChipsPerBreak; i++)
        {
            int h = Hash(seed, i);
            float ox = ((h & 255) / 255f - 0.5f) * 0.7f;
            float oy = (((h >> 8) & 255) / 255f) * 0.45f;
            float oz = (((h >> 16) & 255) / 255f - 0.5f) * 0.7f;
            Alive.Add(new VoxelDebris
            {
                Position = cellCenter + new Float3(ox, oy, oz),
                Velocity = new Float3(ox * 6f, 0.35f + oy, oz * 6f),
                Color = color,
                Life = Lifetime,
                Size = 0.22f + ((h >> 24) & 7) * 0.03f,
            });
        }

        return Alive.Count - before;
    }

    public void Tick(float dt)
    {
        if (dt <= 0f)
            return;
        for (int i = Alive.Count - 1; i >= 0; i--)
        {
            VoxelDebris chip = Alive[i];
            chip.Velocity.Y -= Gravity * dt;
            chip.Position += chip.Velocity * dt;
            chip.Life -= dt;
            if (chip.Life <= 0f)
                Alive.RemoveAt(i);
            else
                Alive[i] = chip;
        }
    }

    private static int Hash(int seed, int i)
    {
        int h = seed * 374761393 + i * 668265263;
        h = (h ^ (h >> 13)) * 1274126177;
        return h ^ (h >> 16);
    }
}
