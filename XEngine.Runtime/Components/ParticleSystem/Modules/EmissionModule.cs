// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using XEngine.Vector;

namespace XEngine.Runtime.ParticleSystem.Modules;

/// <summary>
/// Defines the shape from which particles are emitted.
/// </summary>
[Serializable]
public enum EmissionShape
{
    Point,
    LineSegment,
    Circle,
    Sphere,
    Box,
    Cone
}

/// <summary>
/// Represents a burst of particles emitted at a specific time.
/// </summary>
[Serializable]
public struct ParticleBurst
{
    public float Time;
    public int MinCount;
    public int MaxCount;
    public int CycleCount;
    public float RepeatInterval;

    public ParticleBurst(float time, int count)
    {
        Time = time;
        MinCount = count;
        MaxCount = count;
        CycleCount = 1;
        RepeatInterval = 0;
    }
}

/// <summary>
/// Controls when and how many particles are emitted.
/// </summary>
[Serializable]
public class EmissionModule : ParticleSystemModule
{
    public MinMaxCurve RateOverTime = new(0.0f);
    public MinMaxCurve RateOverDistance = new(0f);
    public List<ParticleBurst> Bursts = new();
    /// <summary>Unity emits at most one repetition of each burst per simulation step.</summary>
    public bool UseUnityBurstScheduling;

    // Shape properties
    public EmissionShape Shape = EmissionShape.Point;
    public bool ShapeEnabled = true;
    public Float3 ShapeForward = Float3.UnitY;
    /// <summary>Use the Unity shape basis (+Z axis, XY circle) before shape rotation.</summary>
    public bool ShapeUsesZAxis;
    public Float3 ShapePosition = Float3.Zero;
    public Float3 ShapeRotation = Float3.Zero; // Euler angles in degrees
    public Float3 ShapeScale = Float3.One;

    // Shape-specific properties
    public float LineLength = 1.0f;          // For LineSegment
    public float Radius = 1.0f;              // For Circle, Sphere, Cone
    public float RadiusThickness = 1f;
    public float Arc = 360f;                 // Circle/cone arc in degrees, beginning at +X
    public float ConeAngle = 25.0f;          // For Cone (in degrees)
    public bool EmitFromShell = false;       // For Sphere, Circle (emit from surface only)
    public Float3 BoxSize = Float3.One;      // For Box

    [NonSerialized]
    internal float EmitAccumulator = 0.0f;
    [NonSerialized]
    internal float DistanceAccumulator;

    [NonSerialized]
    internal Dictionary<int, int> BurstCycles = new();

    /// <summary>Schedules emission at a cycle time in seconds; only rate curves use normalized time.</summary>
    public int CalculateEmitCount(float deltaTime, float time, Random random, float duration = 1f)
    {
        if (!Enabled || deltaTime < 0f) return 0;

        int emitCount = 0;

        // Rate over time emission
        float rate = RateOverTime.Evaluate(duration > 0f ? time / duration : 0f, random);
        EmitAccumulator += MathF.Max(0f, rate) * deltaTime;

        while (EmitAccumulator >= 1.0f)
        {
            emitCount++;
            EmitAccumulator -= 1.0f;
        }

        // Burst emission
        for (int i = 0; i < Bursts.Count; i++)
        {
            var burst = Bursts[i];

            BurstCycles.TryGetValue(i, out int fired);
            int cycles = Math.Max(1, burst.CycleCount);
            // Native scheduling catches up repeats crossed by a frame. Imported Unity
            // systems cap each burst entry at one repetition per simulation step.
            while (fired < cycles && time >= burst.Time + fired * burst.RepeatInterval)
            {
                int count = burst.MinCount == burst.MaxCount
                    ? burst.MinCount
                    : random.Next(burst.MinCount, burst.MaxCount + 1);
                emitCount += count;
                fired++;
                if (burst.RepeatInterval <= 0f)
                    fired = cycles;
                if (UseUnityBurstScheduling)
                {
                    // Unity drops additional repeats crossed by this step. Advancing
                    // only by one would extend a short burst train into later frames.
                    if (burst.RepeatInterval > 0f)
                    {
                        float crossed = (time - burst.Time) / burst.RepeatInterval;
                        fired = crossed >= cycles - 1 ? cycles : Math.Max(fired, (int)crossed + 1);
                    }
                    break;
                }
            }
            BurstCycles[i] = fired;
        }

        return emitCount;
    }

    public void Reset()
    {
        EmitAccumulator = 0;
        DistanceAccumulator = 0f;
        RestartCycle();
    }

    internal int CalculateDistanceEmitCount(float distance, float normalizedTime, Random random,
        out float firstFraction, out float fractionStep)
    {
        firstFraction = 0f;
        fractionStep = 0f;
        if (!Enabled || distance <= 0f)
            return 0;
        float expected = MathF.Max(0f, RateOverDistance.Evaluate(normalizedTime, random)) * distance;
        if (expected <= 0f)
            return 0;
        float previous = DistanceAccumulator;
        DistanceAccumulator += expected;
        int count = (int)DistanceAccumulator;
        DistanceAccumulator -= count;
        firstFraction = (1f - previous) / expected;
        fractionStep = 1f / expected;
        return count;
    }

    internal void RestartCycle() => BurstCycles.Clear();

    /// <summary>
    /// Calculates the spawn position and initial direction for a particle based on the emission shape.
    /// </summary>
    public void GetShapePositionAndDirection(Random random, out Float3 position, out Float3 direction)
    {
        position = Float3.Zero;
        direction = new Float3(0, 1, 0); // Default up
        if (!ShapeEnabled)
        {
            direction = ShapeForward;
            return;
        }

        switch (Shape)
        {
            case EmissionShape.Point:
                position = Float3.Zero;
                direction = RandomDirection(random);
                break;

            case EmissionShape.LineSegment:
                {
                    float t = (float)random.NextDouble();
                    position = new Float3(0, 0, (t - 0.5f) * LineLength);
                    direction = RandomDirection(random);
                }
                break;

            case EmissionShape.Circle:
                {
                    if (ShapeUsesZAxis)
                    {
                        float arcAngle = random.NextSingle() * Math.Clamp(Arc, 0f, 360f) * (MathF.PI / 180f);
                        float inner = EmitFromShell ? 1f : 1f - Math.Clamp(RadiusThickness, 0f, 1f);
                        float radialFraction = MathF.Sqrt(inner * inner + (1f - inner * inner) * random.NextSingle());
                        float circleRadius = MathF.Max(0f, Radius) * radialFraction;
                        direction = new Float3(MathF.Cos(arcAngle), MathF.Sin(arcAngle), 0f);
                        position = direction * circleRadius;
                        break;
                    }
                    float angle = (float)random.NextDouble() * 2.0f * MathF.PI;
                    float radius =EmitFromShell ? Radius : (float)random.NextDouble() * Radius;
                    position = new Float3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
                    direction = new Float3(0, 1, 0);
                }
                break;

            case EmissionShape.Sphere:
                {
                    // Uniform random point in or on sphere
                    direction = RandomDirection(random);
                    float radius;
                    if (ShapeUsesZAxis)
                    {
                        float inner = EmitFromShell ? 1f : 1f - Math.Clamp(RadiusThickness, 0f, 1f);
                        float innerVolume = inner * inner * inner;
                        radius = MathF.Max(0f, Radius) * MathF.Cbrt(innerVolume + (1f - innerVolume) * random.NextSingle());
                    }
                    else radius = EmitFromShell ? Radius : (float)Maths.Pow(random.NextDouble(), 1.0 / 3.0) * Radius;
                    position = direction * radius;
                }
                break;

            case EmissionShape.Box:
                {
                    position = new Float3(
                        ((float)random.NextDouble() - 0.5f) * BoxSize.X,
                        ((float)random.NextDouble() - 0.5f) * BoxSize.Y,
                        ((float)random.NextDouble() - 0.5f) * BoxSize.Z
                    );
                    direction = RandomDirection(random);
                }
                break;

            case EmissionShape.Cone:
                {
                    if (ShapeUsesZAxis)
                    {
                        // Unity Cone emits from its base disk. Angle controls velocity
                        // spread, never the spawn radius: angle zero still has a full
                        // disk of parallel particles instead of collapsing to one line.
                        float diskAngle = random.NextSingle() * Math.Clamp(Arc, 0f, 360f) * (MathF.PI / 180f);
                        float inner = 1f - Math.Clamp(RadiusThickness, 0f, 1f);
                        float radialFraction = MathF.Sqrt(inner * inner + (1f - inner * inner) * random.NextSingle());
                        float radius = MathF.Max(0f, Radius) * radialFraction;
                        float cos = MathF.Cos(diskAngle);
                        float sin = MathF.Sin(diskAngle);
                        position = new Float3(cos * radius, sin * radius, 0f);
                        float diskSpread = radialFraction * MathF.Tan(ConeAngle * MathF.PI / 180f);
                        direction = Float3.Normalize(new Float3(cos * diskSpread, sin * diskSpread, 1f));
                        break;
                    }

                    // rpgvfx A2 fix: direction must be the cone axis tilted by the spread
                    // angle — the old code normalized the spawn position, so particles born
                    // near the apex (h≈0) got near-horizontal random directions and the
                    // fountain sprayed sideways. h/u are independent uniform samples.
                    float angle = (float)random.NextDouble() * 2.0f * MathF.PI;
                    float coneAngleRad = ConeAngle * MathF.PI / 180.0f;
                    float u = (float)random.NextDouble();
                    float height = u * Maths.Max(0.001f, Radius);
                    float coneRadius = MathF.Tan(coneAngleRad) * height;

                    position = new Float3(
                        MathF.Cos(angle) * coneRadius,
                        height,
                        MathF.Sin(angle) * coneRadius
                    );

                    float spread = MathF.Tan(coneAngleRad) * (float)random.NextDouble();
                    direction = Float3.Normalize(new Float3(
                        MathF.Cos(angle) * spread,
                        1f,
                        MathF.Sin(angle) * spread
                    ));
                }
                break;
        }

        if (ShapeUsesZAxis)
        {
            if (Shape == EmissionShape.Box)
                direction = Float3.UnitZ;
        }

        // Apply shape transformation (rotation and scale)
        position = new Float3(position.X * ShapeScale.X, position.Y * ShapeScale.Y, position.Z * ShapeScale.Z);

        // Apply rotation (convert euler angles to rotation)
        if (ShapeRotation.X != 0 || ShapeRotation.Y != 0 || ShapeRotation.Z != 0)
        {
            // Simple euler rotation (X, Y, Z order)
            position = RotateVector(position, ShapeRotation);
            direction = RotateVector(direction, ShapeRotation);
        }

        // Add shape position offset
        position = position + ShapePosition;
    }

    private Float3 RandomDirection(Random random)
    {
        // Uniform random direction on unit sphere
        float theta = (float)random.NextDouble() * 2.0f * MathF.PI;
        float phi = MathF.Acos(2.0f * (float)random.NextDouble() - 1.0f);

        return new Float3(
            MathF.Sin(phi) * MathF.Cos(theta),
            MathF.Sin(phi) * MathF.Sin(theta),
            MathF.Cos(phi)
        );
    }

    private Float3 RotateVector(Float3 v, Float3 eulerDegrees)
    {
        // Convert to radians
        float rx = eulerDegrees.X * MathF.PI / 180.0f;
        float ry = eulerDegrees.Y * MathF.PI / 180.0f;
        float rz = eulerDegrees.Z * MathF.PI / 180.0f;

        // Rotate around X
        float cosX = MathF.Cos(rx), sinX = MathF.Sin(rx);
        float y1 = v.Y * cosX - v.Z * sinX;
        float z1 = v.Y * sinX + v.Z * cosX;

        // Rotate around Y
        float cosY = MathF.Cos(ry), sinY = MathF.Sin(ry);
        float x2 = v.X * cosY + z1 * sinY;
        float z2 = -v.X * sinY + z1 * cosY;

        // Rotate around Z
        float cosZ = MathF.Cos(rz), sinZ = MathF.Sin(rz);
        float x3 = x2 * cosZ - y1 * sinZ;
        float y3 = x2 * sinZ + y1 * cosZ;

        return new Float3(x3, y3, z2);
    }
}
