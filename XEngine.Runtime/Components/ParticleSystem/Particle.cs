// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Runtime.ParticleSystem;

/// <summary>
/// Represents a single particle in the particle system.
/// This struct is used for GPU instancing.
/// </summary>
public struct Particle
{
    public Float3 Position;
    public Float3 Velocity;
    public Float3 LifetimeVelocity;
    public Float3 NoiseVelocity;
    public Float3 NoiseSizeOffset;
    public float NoiseScalarSizeOffset;
    public float Rotation;
    public Float3 Rotation3D;
    public Quaternion SpawnRotation;
    public float RotationalSpeed;
    public Float3 RotationRandom;
    public float StartSize;
    public float Size;
    public Float3 StartSize3D;
    public Float3 Size3D;
    public Color StartColor;
    public Color Color;
    public float StartLifetime;
    public float Lifetime;
    public uint RandomSeed;
    public uint ForceStep;
    public Float4 Custom1;

    // UV animation data
    public float UVFrame;        // Current animation frame
    public float TotalTime;      // Total time particle has been alive

    /// <summary>
    /// Gets the normalized lifetime (0 to 1) of the particle.
    /// </summary>
    public float NormalizedLifetime => 1.0f - (Lifetime / StartLifetime);

    /// <summary>
    /// Returns true if the particle is still alive.
    /// </summary>
    public bool IsAlive => Lifetime > 0;
}
