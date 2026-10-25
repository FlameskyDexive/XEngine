// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.ParticleSystem.Modules;

/// <summary>
/// Controls particle size over their lifetime.
/// </summary>
[Serializable]
public class SizeOverLifetimeModule : ParticleSystemModule
{
    public AnimationCurve SizeCurve = new([new KeyFrame(0f, 1f), new KeyFrame(1f, 1f)]);
    public bool SeparateAxes;
    public AnimationCurve SizeYCurve = new([new KeyFrame(0f, 1f), new KeyFrame(1f, 1f)]);
    public AnimationCurve SizeZCurve = new([new KeyFrame(0f, 1f), new KeyFrame(1f, 1f)]);

    public override void OnParticleUpdate(ref Particle particle, float deltaTime)
    {
        if (!Enabled) return;

        float normalizedTime = particle.NormalizedLifetime;
        float sizeMultiplier = (float)SizeCurve.Evaluate(normalizedTime);

        particle.Size = particle.StartSize * sizeMultiplier;
        particle.Size3D = SeparateAxes
            ? new XEngine.Vector.Float3(particle.StartSize3D.X * sizeMultiplier,
                particle.StartSize3D.Y * (float)SizeYCurve.Evaluate(normalizedTime),
                particle.StartSize3D.Z * (float)SizeZCurve.Evaluate(normalizedTime))
            : particle.StartSize3D * sizeMultiplier;
    }
}
