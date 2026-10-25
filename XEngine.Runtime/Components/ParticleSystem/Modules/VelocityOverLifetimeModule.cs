// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Vector;

namespace XEngine.Runtime.ParticleSystem.Modules;

/// <summary>
/// Controls particle velocity over their lifetime.
/// </summary>
[Serializable]
public class VelocityOverLifetimeModule : ParticleSystemModule
{
    public AnimationCurve VelocityX = new([new KeyFrame(0f, 0f), new KeyFrame(1f, 0f)]);
    public AnimationCurve VelocityY = new([new KeyFrame(0f, 0f), new KeyFrame(1f, 0f)]);
    public AnimationCurve VelocityZ = new([new KeyFrame(0f, 0f), new KeyFrame(1f, 0f)]);
    public bool UseMinMaxCurves;
    public MinMaxCurve LinearX = new(0f);
    public MinMaxCurve LinearY = new(0f);
    public MinMaxCurve LinearZ = new(0f);
    public MinMaxCurve OrbitalX = new(0f);
    public MinMaxCurve OrbitalY = new(0f);
    public MinMaxCurve OrbitalZ = new(0f);
    public MinMaxCurve OrbitalOffsetX = new(0f);
    public MinMaxCurve OrbitalOffsetY = new(0f);
    public MinMaxCurve OrbitalOffsetZ = new(0f);
    public MinMaxCurve Radial = new(0f);
    public MinMaxCurve SpeedModifier = new(1f);
    public SimulationSpace Space = SimulationSpace.Local;

    [NonSerialized] private Float4x4 _simulationToModule = Float4x4.Identity;
    [NonSerialized] private Float4x4 _moduleToSimulation = Float4x4.Identity;

    internal void ConfigureSpace(Float4x4 localToWorld, Float4x4 worldToLocal, SimulationSpace simulationSpace)
    {
        if (Space == SimulationSpace.Local)
        {
            _simulationToModule = simulationSpace == SimulationSpace.World ? worldToLocal : Float4x4.Identity;
            _moduleToSimulation = simulationSpace == SimulationSpace.World ? localToWorld : Float4x4.Identity;
        }
        else
        {
            // World-space velocity uses world axes around the emitter origin.
            Float3 origin = new Float3(localToWorld.c3.X, localToWorld.c3.Y, localToWorld.c3.Z);
            _simulationToModule = Float4x4.CreateTranslation(-origin) *
                (simulationSpace == SimulationSpace.Local ? localToWorld : Float4x4.Identity);
            _moduleToSimulation = (simulationSpace == SimulationSpace.Local ? worldToLocal : Float4x4.Identity) *
                Float4x4.CreateTranslation(origin);
        }
    }

    public override void OnParticleUpdate(ref Particle particle, float deltaTime)
    {
        // This module contributes velocity, not acceleration. Remove last frame's
        // contribution before evaluating the new value so constant curves do not ramp.
        particle.Velocity -= particle.LifetimeVelocity;
        particle.LifetimeVelocity = Float3.Zero;
        if (!Enabled) return;

        float normalizedTime = particle.NormalizedLifetime;
        Float3 velocity;
        float speedModifier = 1f;
        if (UseMinMaxCurves)
        {
            uint seed = particle.RandomSeed;
            velocity = new Float3(Evaluate(LinearX, normalizedTime, seed, 0),
                Evaluate(LinearY, normalizedTime, seed, 1), Evaluate(LinearZ, normalizedTime, seed, 2));
            Float3 angular = new Float3(Evaluate(OrbitalX, normalizedTime, seed, 3),
                Evaluate(OrbitalY, normalizedTime, seed, 4), Evaluate(OrbitalZ, normalizedTime, seed, 5));
            Float3 offset = new Float3(Evaluate(OrbitalOffsetX, normalizedTime, seed, 6),
                Evaluate(OrbitalOffsetY, normalizedTime, seed, 7), Evaluate(OrbitalOffsetZ, normalizedTime, seed, 8));
            Float4 position4 = _simulationToModule * new Float4(particle.Position, 1f);
            Float3 relative = new Float3(position4.X, position4.Y, position4.Z) - offset;
            if (angular != Float3.Zero && deltaTime > 0f)
            {
                // Integrate orbital rotation as a rotation, not tangent-velocity Euler
                // steps, which artificially grow the radius on every frame.
                Float3 rotated = Quaternion.FromEuler(angular * (deltaTime * Maths.Rad2Deg)) * relative;
                velocity += (rotated - relative) / deltaTime;
            }
            float radial = Evaluate(Radial, normalizedTime, seed, 9);
            if (radial != 0f)
                velocity += Float3.NormalizeSafe(new Float3(position4.X, position4.Y, position4.Z), Float3.Zero) * radial;
            speedModifier = Evaluate(SpeedModifier, normalizedTime, seed, 10);
        }
        else
            velocity = new Float3((float)VelocityX.Evaluate(normalizedTime),
                (float)VelocityY.Evaluate(normalizedTime), (float)VelocityZ.Evaluate(normalizedTime));

        Float4 simulationVelocity = _moduleToSimulation * new Float4(velocity, 0f);
        Float3 combined = (particle.Velocity + new Float3(simulationVelocity.X, simulationVelocity.Y, simulationVelocity.Z)) * speedModifier;
        particle.LifetimeVelocity = combined - particle.Velocity;
        particle.Velocity = combined;
    }

    private static float Evaluate(MinMaxCurve curve, float time, uint seed, uint component)
    {
        if (curve.Mode != MinMaxCurveMode.Random)
            return curve.Evaluate(time, null);
        uint hash = seed ^ (component + 1u) * 0x9e3779b9u;
        hash ^= hash >> 16;
        hash *= 0x7feb352du;
        hash ^= hash >> 15;
        return Maths.Lerp((float)curve.MinCurve.Evaluate(time), (float)curve.MaxCurve.Evaluate(time),
            (hash & 0x00ffffffu) * (1f / 16777215f));
    }
}
