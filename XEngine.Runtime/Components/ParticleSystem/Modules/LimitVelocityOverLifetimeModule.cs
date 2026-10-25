// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Utils;
using XEngine.Vector;

namespace XEngine.Runtime.ParticleSystem;

/// <summary>
/// Unity LimitVelocityOverLifetime module (Shuriken serialized name ClampVelocityModule):
/// clamps particle speed to a limit curve sampled at normalized lifetime, with an exponential
/// excess decay (Dampen) and a linear Drag. Separate-axes mode clamps each component.
/// </summary>
public sealed class LimitVelocityOverLifetimeModule : ParticleSystemModule
{
    public bool SeparateAxes = false;
    public MinMaxCurve LimitMagnitude = new(1.0f);
    public MinMaxCurve LimitX = new(1.0f);
    public MinMaxCurve LimitY = new(1.0f);
    public MinMaxCurve LimitZ = new(1.0f);
    /// <summary>0..1 — fraction of the speed excess removed per second (×8 gain so the slider
    /// feels like Unity's dampen; exponential so the approach is stable at any dt).</summary>
    public float Dampen = 1.0f;
    /// <summary>Linear velocity drag per second.</summary>
    public float Drag = 0.0f;

    public override void OnParticleUpdate(ref Particle particle, float deltaTime)
    {
        if (!Enabled) return;

        if (SeparateAxes)
        {
            float t = particle.NormalizedLifetime;
            particle.Velocity = new Float3(
                ClampAxis(particle.Velocity.X, LimitX.Evaluate(t, null), Dampen, deltaTime),
                ClampAxis(particle.Velocity.Y, LimitY.Evaluate(t, null), Dampen, deltaTime),
                ClampAxis(particle.Velocity.Z, LimitZ.Evaluate(t, null), Dampen, deltaTime));
        }
        else
        {
            float speed = Float3.Length(particle.Velocity);
            if (speed > 0.0001f)
            {
                float limit = LimitMagnitude.Evaluate(particle.NormalizedLifetime, null);
                if (speed > limit)
                {
                    float excessDecay = MathF.Exp(-Dampen * 8f * deltaTime);
                    float newSpeed = limit + (speed - limit) * excessDecay;
                    particle.Velocity *= newSpeed / speed;
                }
            }
        }

        if (Drag > 0f)
            particle.Velocity *= Maths.Max(0f, 1f - Drag * deltaTime);
    }

    private static float ClampAxis(float value, float limit, float dampen, float deltaTime)
    {
        if (value > limit)
        {
            float decay = MathF.Exp(-dampen * 8f * deltaTime);
            value = limit + (value - limit) * decay;
        }
        else if (value < -limit)
        {
            float decay = MathF.Exp(-dampen * 8f * deltaTime);
            value = -limit + (value + limit) * decay;
        }
        return value;
    }
}

/// <summary>
/// Unity InheritVelocity module: adds a fraction of the emitter's frame velocity to freshly
/// spawned particles. The owning component writes <see cref="EmitterVelocity"/> (world-space
/// m/s, transform position delta per Update); curves sample at spawn via EvaluateInitial.
/// </summary>
public sealed class InheritVelocityModule : ParticleSystemModule
{
    public MinMaxCurve Curve = new(0.0f);
    /// <summary>World-space emitter velocity, written by the component every Update.</summary>
    public Float3 EmitterVelocity;

    public void OnParticleSpawn(ref Particle particle, Random random)
    {
        if (!Enabled) return;
        float multiplier = Curve.EvaluateInitial(random);
        particle.Velocity += EmitterVelocity * multiplier;
    }
}
