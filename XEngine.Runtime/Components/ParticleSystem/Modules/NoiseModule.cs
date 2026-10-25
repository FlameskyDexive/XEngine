// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Vector;

namespace XEngine.Runtime.ParticleSystem;

public enum ParticleNoiseQuality
{
    Low,
    Medium,
    High,
}

/// <summary>Particle turbulence with a legacy native mode and authored spatial noise.</summary>
public sealed class NoiseModule : ParticleSystemModule
{
    // Existing native assets retain their serialized behavior unless they opt in.
    public float Strength;
    public float Frequency = 0.5f;
    public bool Damping = true;
    public bool UseSpatialNoise;
    public bool SeparateAxes;
    public MinMaxCurve StrengthCurve = new(1f);
    public MinMaxCurve StrengthYCurve = new(1f);
    public MinMaxCurve StrengthZCurve = new(1f);
    public MinMaxCurve ScrollSpeed = new(0f);
    public int Octaves = 1;
    public float OctaveMultiplier = 0.5f;
    public float OctaveScale = 2f;
    public ParticleNoiseQuality Quality = ParticleNoiseQuality.High;
    public bool RemapEnabled;
    public MinMaxCurve Remap = IdentityCurve();
    public MinMaxCurve RemapY = IdentityCurve();
    public MinMaxCurve RemapZ = IdentityCurve();
    public MinMaxCurve PositionAmount = new(1f);
    public MinMaxCurve RotationAmount = new(0f);
    public MinMaxCurve SizeAmount = new(0f);

    [NonSerialized] private double _scrollOffset;

    internal void Reset() => _scrollOffset = 0d;

    internal void AdvanceTime(float deltaTime, float normalizedSystemTime, uint systemSeed)
    {
        if (!Enabled || !UseSpatialNoise) return;
        _scrollOffset += Evaluate(ScrollSpeed, normalizedSystemTime, systemSeed, 0u) * (double)deltaTime;
        _scrollOffset -= Math.Floor(_scrollOffset / 289d) * 289d;
    }

    internal void RemoveLastContribution(ref Particle particle)
    {
        particle.Velocity -= particle.NoiseVelocity;
        particle.NoiseVelocity = Float3.Zero;
        particle.Size -= particle.NoiseScalarSizeOffset;
        particle.Size3D -= particle.NoiseSizeOffset;
        particle.NoiseScalarSizeOffset = 0f;
        particle.NoiseSizeOffset = Float3.Zero;
    }

    private static MinMaxCurve IdentityCurve() => new()
    {
        Mode = MinMaxCurveMode.Curve,
        Curve = new AnimationCurve([new KeyFrame(0f, 0f), new KeyFrame(1f, 1f)]),
    };

    public override void OnParticleUpdate(ref Particle particle, float deltaTime)
    {
        // The system removes these before Force/Velocity modules; doing this here
        // also preserves the contract for callers that update this module directly.
        RemoveLastContribution(ref particle);
        if (!Enabled || deltaTime <= 0f) return;
        if (UseSpatialNoise)
        {
            UpdateSpatial(ref particle, deltaTime);
            return;
        }
        if (!Enabled || Strength <= 0f) return;
        float t = particle.TotalTime * Frequency + particle.RandomSeed * 13.37f;
        Float3 noise = new Float3(ValueNoise1D(t), ValueNoise1D(t + 71.3f),
            ValueNoise1D(t + 152.9f)) * 2f - new Float3(1f);
        particle.Velocity += noise * Strength * deltaTime;
        if (Damping)
        {
            float speed = Float3.Length(particle.Velocity);
            if (speed > 1f) particle.Velocity *= 1f / speed;
        }
    }

    private void UpdateSpatial(ref Particle particle, float deltaTime)
    {
        float time = particle.NormalizedLifetime;
        uint seed = particle.RandomSeed;
        float strengthX = Evaluate(StrengthCurve, time, seed, 1u);
        Float3 strength = SeparateAxes
            ? new Float3(strengthX, Evaluate(StrengthYCurve, time, seed, 2u), Evaluate(StrengthZCurve, time, seed, 3u))
            : new Float3(strengthX);
        float frequency = MathF.Max(0.0001f, Frequency);
        Float3 fieldPosition = particle.Position * frequency + new Float3((float)_scrollOffset);
        Float3 field = Float3.Zero;
        float weight = 1f, scale = 1f;
        int octaves = Math.Clamp(Octaves, 1, 4);
        for (int octave = 0; octave < octaves; octave++)
        {
            field += ParticleNoiseField.Curl(fieldPosition * scale, Quality) * (weight * scale);
            weight *= OctaveMultiplier;
            scale *= OctaveScale;
        }
        // Curl differentiates a spatial field. Damping removes the extra frequency
        // factor; Unity's frequency=2 probe gives exactly half the undamped velocity.
        if (!Damping) field *= frequency;
        if (RemapEnabled)
        {
            field = new Float3(RemapValue(Remap, field.X, seed, 4u),
                RemapValue(SeparateAxes ? RemapY : Remap, field.Y, seed, 5u),
                RemapValue(SeparateAxes ? RemapZ : Remap, field.Z, seed, 6u));
        }
        field *= strength;
        // Unity GetParticles probes keep base velocity unchanged. Noise is a fresh
        // contribution each step, removed before other modules run on the next one.
        particle.NoiseVelocity = field * Evaluate(PositionAmount, time, seed, 7u);
        particle.Velocity += particle.NoiseVelocity;
        particle.Rotation += field.X * Evaluate(RotationAmount, time, seed, 8u) * (Maths.Deg2Rad * deltaTime);
        particle.Rotation3D.Z = particle.Rotation;
        float size = Evaluate(SizeAmount, time, seed, 9u);
        if (size != 0f)
        {
            float scalarScale = MathF.Max(0f, 1f + field.X * size);
            particle.NoiseScalarSizeOffset = particle.Size * (scalarScale - 1f);
            Float3 scale3D = Maths.Max(Float3.Zero, Float3.One + field * size);
            particle.NoiseSizeOffset = particle.Size3D * (scale3D - Float3.One);
            particle.Size += particle.NoiseScalarSizeOffset;
            particle.Size3D += particle.NoiseSizeOffset;
        }
    }

    private static float RemapValue(MinMaxCurve curve, float value, uint seed, uint channel) =>
        Evaluate(curve, Math.Clamp(value * 0.5f + 0.5f, 0f, 1f), seed, channel) * 2f - 1f;

    private static float Evaluate(MinMaxCurve curve, float time, uint seed, uint channel)
    {
        if (curve.Mode != MinMaxCurveMode.Random) return curve.Evaluate(time, null);
        uint hash = seed ^ (channel + 1u) * 0x9e3779b9u;
        hash ^= hash >> 16;
        hash *= 0x7feb352du;
        hash ^= hash >> 15;
        return Maths.Lerp((float)curve.MinCurve.Evaluate(time), (float)curve.MaxCurve.Evaluate(time),
            (hash & 0x00ffffffu) * (1f / 16777215f));
    }

    private static float ValueNoise1D(float t)
    {
        int i = (int)MathF.Floor(t);
        float f = t - i;
        float u = f * f * (3f - 2f * f);
        return Maths.Lerp(Hash01(i), Hash01(i + 1), u);
    }

    private static float Hash01(int n)
    {
        uint h = (uint)n;
        h = (h ^ 61u) ^ (h >> 16);
        h += h << 3;
        h ^= h >> 4;
        h *= 0x27d4eb2du;
        h ^= h >> 15;
        return (h & 0xFFFFFFu) / 16777215f;
    }
}
