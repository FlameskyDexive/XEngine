using System;

namespace XEngine.VoxelWorld;

/// <summary>Deterministic value noise for Earth height and moisture.</summary>
public static class VoxelValueNoise
{
    public static float Hash(int x, int z, int seed)
    {
        int h = unchecked(x * 374761393 + z * 668265263 + seed * 1274126177);
        h = (h ^ (h >> 13)) * 1274126177;
        return ((h & 0x7fffffff) / (float)int.MaxValue) * 2f - 1f;
    }

    public static float Smooth(float x, float z, int seed)
    {
        int x0 = (int)MathF.Floor(x);
        int z0 = (int)MathF.Floor(z);
        float tx = x - x0;
        float tz = z - z0;
        float a = Hash(x0, z0, seed);
        float b = Hash(x0 + 1, z0, seed);
        float c = Hash(x0, z0 + 1, seed);
        float d = Hash(x0 + 1, z0 + 1, seed);
        float ux = tx * tx * (3f - 2f * tx);
        float uz = tz * tz * (3f - 2f * tz);
        return Lerp(Lerp(a, b, ux), Lerp(c, d, ux), uz);
    }

    public static float Fbm(float x, float z, int seed, int octaves = 3)
    {
        float sum = 0f;
        float amp = 1f;
        float freq = 1f;
        float norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Smooth(x * freq, z * freq, seed + i * 101) * amp;
            norm += amp;
            amp *= 0.5f;
            freq *= 2f;
        }

        return norm > 0f ? sum / norm : 0f;
    }

    public static float Hash3(int x, int y, int z, int seed)
    {
        int h = unchecked(x * 374761393 + y * 1103515245 + z * 668265263 + seed * 1274126177);
        h = (h ^ (h >> 13)) * 1274126177;
        return ((h & 0x7fffffff) / (float)int.MaxValue) * 2f - 1f;
    }

    public static float Smooth3(float x, float y, float z, int seed)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        int z0 = (int)MathF.Floor(z);
        float tx = x - x0;
        float ty = y - y0;
        float tz = z - z0;
        float ux = tx * tx * (3f - 2f * tx);
        float uy = ty * ty * (3f - 2f * ty);
        float uz = tz * tz * (3f - 2f * tz);
        float c000 = Hash3(x0, y0, z0, seed);
        float c100 = Hash3(x0 + 1, y0, z0, seed);
        float c010 = Hash3(x0, y0 + 1, z0, seed);
        float c110 = Hash3(x0 + 1, y0 + 1, z0, seed);
        float c001 = Hash3(x0, y0, z0 + 1, seed);
        float c101 = Hash3(x0 + 1, y0, z0 + 1, seed);
        float c011 = Hash3(x0, y0 + 1, z0 + 1, seed);
        float c111 = Hash3(x0 + 1, y0 + 1, z0 + 1, seed);
        return Lerp(
            Lerp(Lerp(c000, c100, ux), Lerp(c010, c110, ux), uy),
            Lerp(Lerp(c001, c101, ux), Lerp(c011, c111, ux), uy),
            uz);
    }

    /// <summary>3D fractal noise in [-1, 1]; the cave carver's density field.</summary>
    public static float Fbm3(float x, float y, float z, int seed, int octaves = 2)
    {
        float sum = 0f;
        float amp = 1f;
        float freq = 1f;
        float norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Smooth3(x * freq, y * freq, z * freq, seed + i * 101) * amp;
            norm += amp;
            amp *= 0.5f;
            freq *= 2f;
        }

        return norm > 0f ? sum / norm : 0f;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
