// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Vector;

namespace XEngine.Runtime.ParticleSystem.Modules;

/// <summary>
/// Controls the initial properties of particles when they spawn.
/// </summary>
[Serializable]
public class InitialModule : ParticleSystemModule
{
    public MinMaxCurve StartLifetime = new(5.0f);
    public MinMaxCurve StartSpeed = new(5.0f);
    public MinMaxCurve StartSize = new(1.0f);
    public bool SeparateSizeAxes;
    public MinMaxCurve StartSizeY = new(1f);
    public MinMaxCurve StartSizeZ = new(1f);
    public MinMaxCurve StartRotation = new(0.0f);
    public bool SeparateRotationAxes;
    public MinMaxCurve StartRotationX = new(0f);
    public MinMaxCurve StartRotationY = new(0f);
    public MinMaxGradient StartColor = new(Color.White);
    public float GravityModifier = 0.0f;

    public override void OnParticleSpawn(ref Particle particle, Random random)
    {
        if (!Enabled) return;

        particle.StartLifetime = StartLifetime.EvaluateInitial(random);
        particle.Lifetime = particle.StartLifetime;
        particle.Size = StartSize.EvaluateInitial(random);
        particle.StartSize = particle.Size;
        particle.StartSize3D = SeparateSizeAxes
            ? new Float3(particle.Size, StartSizeY.EvaluateInitial(random), StartSizeZ.EvaluateInitial(random))
            : new Float3(particle.Size);
        particle.Size3D = particle.StartSize3D;
        particle.Rotation = StartRotation.EvaluateInitial(random);
        particle.Rotation3D = SeparateRotationAxes
            ? new Float3(StartRotationX.EvaluateInitial(random), StartRotationY.EvaluateInitial(random), particle.Rotation)
            : new Float3(0f, 0f, particle.Rotation);
        particle.SpawnRotation = Quaternion.Identity;
        particle.RotationalSpeed = 0;
        particle.Color = StartColor.EvaluateInitial(random);
        particle.StartColor = particle.Color;

        // Velocity is set by emission shape, but we apply start speed here
        float speed = StartSpeed.EvaluateInitial(random);
        if (particle.Velocity != Float3.Zero)
        {
            particle.Velocity = Float3.Normalize(particle.Velocity) * speed;
        }
    }

    public override void OnParticleUpdate(ref Particle particle, float deltaTime)
    {
        if (!Enabled) return;

        // Apply gravity
        if (GravityModifier != 0)
        {
            particle.Velocity += new Float3(0, -9.81f * GravityModifier * deltaTime, 0);
        }
    }
}
