// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Vector;

namespace XEngine.Runtime.ParticleSystem.Modules;

/// <summary>Accelerates particles with authored force curves in local or world axes.</summary>
[Serializable]
public class ForceOverLifetimeModule : ParticleSystemModule
{
    public MinMaxCurve ForceX = new(0f);
    public MinMaxCurve ForceY = new(0f);
    public MinMaxCurve ForceZ = new(0f);
    public SimulationSpace Space = SimulationSpace.Local;
    public bool RandomizeEachFrame;

    [NonSerialized] private Quaternion _moduleToSimulation = Quaternion.Identity;

    internal void ConfigureSpace(Quaternion emitterRotation, SimulationSpace simulationSpace)
    {
        // Forces have authored acceleration units. Emitter scale must not change
        // their magnitude when the module's axes differ from simulation axes.
        _moduleToSimulation = Space == simulationSpace ? Quaternion.Identity
            : Space == SimulationSpace.Local ? emitterRotation : Quaternion.Inverse(emitterRotation);
    }

    public override void OnParticleUpdate(ref Particle particle, float deltaTime)
    {
        if (!Enabled || deltaTime <= 0f) return;

        float time = particle.NormalizedLifetime;
        uint step = RandomizeEachFrame ? ++particle.ForceStep : 0u;
        Float3 force = new(Evaluate(ForceX, time, particle.RandomSeed, step, 0u),
            Evaluate(ForceY, time, particle.RandomSeed, step, 1u),
            Evaluate(ForceZ, time, particle.RandomSeed, step, 2u));
        particle.Velocity += (_moduleToSimulation * force) * deltaTime;
    }

    private static float Evaluate(MinMaxCurve curve, float time, uint seed, uint step, uint axis)
    {
        if (curve.Mode != MinMaxCurveMode.Random)
            return curve.Evaluate(time, null);

        uint hash = seed ^ (axis + 1u) * 0x9e3779b9u ^ step * 0x85ebca6bu;
        hash ^= hash >> 16;
        hash *= 0x7feb352du;
        hash ^= hash >> 15;
        return Maths.Lerp((float)curve.MinCurve.Evaluate(time), (float)curve.MaxCurve.Evaluate(time),
            (hash & 0x00ffffffu) * (1f / 16777215f));
    }
}
