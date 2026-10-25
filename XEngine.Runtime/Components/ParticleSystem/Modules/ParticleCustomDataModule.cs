// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Vector;

namespace XEngine.Runtime.ParticleSystem.Modules;

/// <summary>
/// Custom vector stream evaluated over normalized particle lifetime. Renderer stream
/// selection controls packing: Custom1XY occupies UV0.zw alongside the regular UV0.xy.
/// </summary>
[Serializable]
public sealed class ParticleCustomDataModule : ParticleSystemModule
{
    public int Custom1ComponentCount = 4;
    public MinMaxCurve Custom1X = new(0f);
    public MinMaxCurve Custom1Y = new(0f);
    public MinMaxCurve Custom1Z = new(0f);
    public MinMaxCurve Custom1W = new(0f);
    public bool Custom1ToUV0ZW;

    public override void OnParticleSpawn(ref Particle particle, Random random) =>
        OnParticleUpdate(ref particle, 0f);

    public override void OnParticleUpdate(ref Particle particle, float deltaTime)
    {
        if (!Enabled)
        {
            particle.Custom1 = Float4.Zero;
            return;
        }

        float time = Math.Clamp(particle.NormalizedLifetime, 0f, 1f);
        particle.Custom1 = new Float4(
            Custom1ComponentCount > 0 ? Evaluate(Custom1X, time, particle.RandomSeed, 0u) : 0f,
            Custom1ComponentCount > 1 ? Evaluate(Custom1Y, time, particle.RandomSeed, 1u) : 0f,
            Custom1ComponentCount > 2 ? Evaluate(Custom1Z, time, particle.RandomSeed, 2u) : 0f,
            Custom1ComponentCount > 3 ? Evaluate(Custom1W, time, particle.RandomSeed, 3u) : 0f);
    }

    private static float Evaluate(MinMaxCurve curve, float time, uint seed, uint component)
    {
        if (curve.Mode != MinMaxCurveMode.Random)
            return curve.Evaluate(time, null);

        // Random-between-curves chooses a stable value per particle/component. It must
        // not consume a new random number each frame and flicker between the curves.
        uint hash = seed ^ (component + 1u) * 0x9e3779b9u;
        hash ^= hash >> 16;
        hash *= 0x7feb352du;
        hash ^= hash >> 15;
        float factor = (hash & 0x00ffffffu) * (1f / 16777215f);
        return Maths.Lerp((float)curve.MinCurve.Evaluate(time), (float)curve.MaxCurve.Evaluate(time), factor);
    }
}
