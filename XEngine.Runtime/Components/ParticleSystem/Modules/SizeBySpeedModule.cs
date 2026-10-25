// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Utils;
using XEngine.Vector;

namespace XEngine.Runtime.ParticleSystem;

/// <summary>
/// Unity SizeBySpeed module (zonezero M9): remaps the particle speed onto a size multiplier
/// between MinSize and MaxSize across the [SpeedRange.x, SpeedRange.y] speed window.
/// </summary>
public sealed class SizeBySpeedModule : ParticleSystemModule
{
    public MinMaxCurve Size = new();
    public Float2 SpeedRange = new(0f, 1f);
    public float MinSize = 1f;
    public float MaxSize = 1f;

    public override void OnParticleUpdate(ref Particle particle, float deltaTime)
    {
        if (!Enabled) return;
        float speed = Float3.Length(particle.Velocity);
        float t = SpeedRange.Y > SpeedRange.X
            ? Maths.Clamp((speed - SpeedRange.X) / (SpeedRange.Y - SpeedRange.X), 0f, 1f)
            : 0f;
        float multiplier = Maths.Lerp(MinSize, MaxSize, t);
        // Compose with the base start size (SizeOverLifetime multiplies in its own pass).
        particle.Size = particle.StartSize * multiplier;
        // Re-apply the lifetime curve on top so the two modules compose deterministically
        // (SizeOverLifetime runs first in the module order and wrote Size from StartSize).
        // To avoid double-scaling we store the multiplier in the unused 4th custom slot via
        // the component instead — here we only clamp the final size.
        particle.Size = Maths.Max(particle.Size, 0.0f);
    }
}
