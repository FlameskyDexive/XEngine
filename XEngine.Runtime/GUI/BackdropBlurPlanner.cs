using System;

namespace XEngine.Runtime.GUI;

/// <summary>Bounded dual-Kawase blur plan shared by all backdrop requests in one UI frame.</summary>
public readonly record struct BackdropBlurPlan(int Iterations, float Offset)
{
    public bool IsEnabled => Iterations > 0;
}

public static class BackdropBlurPlanner
{
    private const int MaxBlurLevels = 6;

    public static float SelectLargestRadius(ReadOnlySpan<float> radii)
    {
        float largest = 0f;
        for (int i = 0; i < radii.Length; i++)
            if (radii[i] > largest) largest = radii[i];
        return largest;
    }

    public static BackdropBlurPlan Create(float radius)
    {
        if (radius <= 0f) return default;

        float normalizedRadius = MathF.Max(radius, 2f);
        int iterations = Math.Clamp((int)MathF.Floor(MathF.Log2(normalizedRadius)) - 1, 1, MaxBlurLevels - 1);
        float offset = Math.Clamp(normalizedRadius / (1 << (iterations + 1)), 0.5f, 6f);
        return new BackdropBlurPlan(iterations, offset);
    }
}
