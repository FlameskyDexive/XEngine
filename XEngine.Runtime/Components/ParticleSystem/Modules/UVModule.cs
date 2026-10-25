// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Vector;

namespace XEngine.Runtime.ParticleSystem.Modules;

/// <summary>
/// Animation mode for UV coordinates.
/// </summary>
[Serializable]
public enum UVAnimationMode
{
    GridAnimation,  // Sprite sheet with rows and columns
    CurveAnimation  // Manual UV curve animation
}

/// <summary>
/// Controls UV coordinate animation for particles.
/// Enables sprite sheet animation and texture scrolling effects.
/// </summary>
[Serializable]
public class UVModule : ParticleSystemModule
{
    public UVAnimationMode Mode = UVAnimationMode.GridAnimation;

    // Grid animation settings
    public int TilesX = 1;              // Number of columns in sprite sheet
    public int TilesY = 1;              // Number of rows in sprite sheet
    public int CycleCount = 1;          // How many times to loop the animation
    public float FrameOverTime = 1.0f;  // Speed of animation (frames per second)
    public bool RandomStartFrame = false; // Start from random frame
    /// <summary>Evaluate authored normalized frame/start curves, keeping each particle's random choices stable.</summary>
    public bool UseMinMaxCurves;
    public MinMaxCurve FrameOverTimeCurve = new(1f);
    public MinMaxCurve StartFrameCurve = new(0f);
    public bool SingleRow;
    public int RowIndex;
    public bool RandomRow = true;
    public bool TopRowFirst;

    // Curve animation settings
    public AnimationCurve UOffsetCurve = new([new KeyFrame(0f, 0f), new KeyFrame(1f, 0f)]);
    public AnimationCurve VOffsetCurve = new([new KeyFrame(0f, 0f), new KeyFrame(1f, 0f)]);

    // UV scrolling
    public Float2 ScrollSpeed = Float2.Zero;

    // Flip options
    public bool FlipU = false;
    public bool FlipV = false;

    public override void OnParticleSpawn(ref Particle particle, Random random)
    {
        if (!Enabled)
            return;

        if (Mode == UVAnimationMode.GridAnimation && UseMinMaxCurves)
        {
            UpdateAuthoredGrid(ref particle);
            return;
        }

        // Set initial frame if using random start
        if (Mode == UVAnimationMode.GridAnimation && RandomStartFrame)
        {
            int totalFrames = TilesX * TilesY;
            particle.UVFrame = (float)random.Next(0, totalFrames);
        }
        else
        {
            particle.UVFrame = 0;
        }
    }

    public override void OnParticleUpdate(ref Particle particle, float deltaTime)
    {
        if (!Enabled)
            return;

        switch (Mode)
        {
            case UVAnimationMode.GridAnimation:
                UpdateGridAnimation(ref particle, deltaTime);
                break;
            case UVAnimationMode.CurveAnimation:
                UpdateCurveAnimation(ref particle);
                break;
        }
    }

    private void UpdateGridAnimation(ref Particle particle, float deltaTime)
    {
        if (UseMinMaxCurves)
        {
            UpdateAuthoredGrid(ref particle);
            return;
        }
        int totalFrames = TilesX * TilesY;
        if (totalFrames <= 1)
            return;

        // Calculate animation progress
        float animationSpeed = FrameOverTime;
        float lifetime = particle.NormalizedLifetime;

        // Update frame based on lifetime
        float frameFloat = lifetime * totalFrames * CycleCount * animationSpeed;

        // Loop or clamp
        if (CycleCount > 0)
        {
            frameFloat = frameFloat % totalFrames;
        }
        else
        {
            frameFloat = Maths.Min(frameFloat, totalFrames - 1);
        }

        particle.UVFrame = frameFloat;
    }

    private void UpdateAuthoredGrid(ref Particle particle)
    {
        int columns = Math.Max(1, TilesX);
        int rows = Math.Max(1, TilesY);
        int frameCount = SingleRow ? columns : columns * rows;
        float lifetime = particle.StartLifetime > 0f
            ? Math.Clamp(particle.NormalizedLifetime, 0f, 1f) : 0f;
        // Unity repeats the authored curve's input, not its amplitude. A constant
        // frame curve therefore stays still even with multiple cycles.
        float curveTime = Fraction(lifetime * Math.Max(1, CycleCount));
        float frame = Sample(FrameOverTimeCurve, curveTime, particle.RandomSeed, 0u);
        float start = Sample(StartFrameCurve, 0f, particle.RandomSeed, 1u);
        particle.UVFrame = Fraction(frame + start) * frameCount;
        if (SingleRow)
        {
            int row = RandomRow
                ? Math.Min((int)(StableRandom(particle.RandomSeed, 2u) * rows), rows - 1)
                : Math.Clamp(RowIndex, 0, rows - 1);
            particle.UVFrame += row * columns;
        }
    }

    private static float Fraction(float value) => value - MathF.Floor(value);

    private static float Sample(MinMaxCurve curve, float time, uint seed, uint channel)
    {
        if (curve.Mode != MinMaxCurveMode.Random)
            return curve.Evaluate(time, null);
        return Maths.Lerp((float)curve.MinCurve.Evaluate(time), (float)curve.MaxCurve.Evaluate(time),
            StableRandom(seed, channel));
    }

    private static float StableRandom(uint seed, uint channel)
    {
        uint hash = seed ^ (channel + 1u) * 0x9e3779b9u;
        hash ^= hash >> 16;
        hash *= 0x7feb352du;
        hash ^= hash >> 15;
        return (hash & 0x00ffffffu) * (1f / 16777216f);
    }

    private void UpdateCurveAnimation(ref Particle particle)
    {
        // Curves control UV offset directly
        float lifetime = particle.NormalizedLifetime;

        // The UV offset is stored in UVFrame (X) and we can extend Particle if needed
        // For now, just store the U offset
        particle.UVFrame = (float)UOffsetCurve.Evaluate(lifetime);
    }

    /// <summary>
    /// Calculates the UV coordinates for a particle based on its current frame.
    /// </summary>
    public void GetUVCoordinates(Particle particle, out Float2 uvOffset, out Float2 uvScale)
    {
        if (!Enabled || Mode != UVAnimationMode.GridAnimation)
        {
            uvOffset = Float2.Zero;
            uvScale = Float2.One;
            return;
        }

        int columns = Math.Max(1, TilesX);
        int rows = Math.Max(1, TilesY);
        int totalFrames = columns * rows;
        if (totalFrames <= 1)
        {
            uvOffset = Float2.Zero;
            uvScale = Float2.One;
            return;
        }

        // Calculate current frame
        int frame = (int)particle.UVFrame;
        frame = Maths.Clamp(frame, 0, totalFrames - 1);

        // Calculate grid position
        int row = frame / columns;
        int col = frame % columns;
        if (TopRowFirst)
            row = rows - row - 1;

        // Calculate UV offset and scale
        float tileWidth = 1.0f / columns;
        float tileHeight = 1.0f / rows;

        float u = col * tileWidth;
        float v = row * tileHeight;

        // Apply flipping
        if (FlipU)
        {
            u = 1.0f - u - tileWidth;
        }

        if (FlipV)
        {
            v = 1.0f - v - tileHeight;
        }

        // Apply scrolling
        u += ScrollSpeed.X * particle.TotalTime;
        v += ScrollSpeed.Y * particle.TotalTime;

        uvOffset = new Float2(u, v);
        uvScale = new Float2(tileWidth, tileHeight);
    }

    /// <summary>
    /// Gets UV tile info for shader (offset and scale).
    /// </summary>
    public Float4 GetUVTileInfo(Particle particle)
    {
        GetUVCoordinates(particle, out Float2 offset, out Float2 scale);
        return new Float4(offset.X, offset.Y, scale.X, scale.Y);
    }
}
