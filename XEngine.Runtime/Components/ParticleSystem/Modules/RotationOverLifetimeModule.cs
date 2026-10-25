// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Vector;

namespace XEngine.Runtime.ParticleSystem.Modules;

/// <summary>
/// Controls particle rotation over their lifetime.
/// </summary>
[Serializable]
public class RotationOverLifetimeModule : ParticleSystemModule
{
    public MinMaxCurve AngularVelocity = new(0.0f);
    public bool SeparateAxes;
    public MinMaxCurve AngularVelocityX = new(0f);
    public MinMaxCurve AngularVelocityY = new(0f);

    public override void OnParticleSpawn(ref Particle particle, Random random)
    {
        if (!Enabled) return;

        particle.RotationalSpeed = AngularVelocity.EvaluateInitial(random);
        particle.RotationRandom = new Float3(random.NextSingle(), random.NextSingle(), random.NextSingle());
    }

    public override void OnParticleUpdate(ref Particle particle, float deltaTime)
    {
        if (!Enabled) return;

        float time = particle.NormalizedLifetime;
        particle.RotationalSpeed = Evaluate(AngularVelocity, time, particle.RotationRandom.Z);
        particle.Rotation += particle.RotationalSpeed * deltaTime;
        if (SeparateAxes)
        {
            particle.Rotation3D.X += Evaluate(AngularVelocityX, time, particle.RotationRandom.X) * deltaTime;
            particle.Rotation3D.Y += Evaluate(AngularVelocityY, time, particle.RotationRandom.Y) * deltaTime;
        }
        particle.Rotation3D.Z = particle.Rotation;
    }

    private static float Evaluate(MinMaxCurve curve, float time, float randomFactor) =>
        curve.Mode == MinMaxCurveMode.Random
            ? Maths.Lerp((float)curve.MinCurve.Evaluate(time), (float)curve.MaxCurve.Evaluate(time), randomFactor)
            : curve.Evaluate(time, null);
}
