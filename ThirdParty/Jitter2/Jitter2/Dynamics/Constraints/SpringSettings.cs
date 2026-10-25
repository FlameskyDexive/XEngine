/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using Jitter2.LinearMath;

namespace Jitter2.Dynamics.Constraints;

/// <summary>
/// Converts a frequency / damping-ratio pair into the softness and bias factor Jitter2
/// constraints already expose. Existing <c>Softness</c> / <c>Bias</c> setters stay the
/// authoritative solver inputs; this is only a parameterization.
/// </summary>
/// <remarks>
/// XENGINE: matches the mass-aware conversion already used by
/// <c>SoftBodies.SpringConstraint.SetSpringParameters</c>, extracted so joints can share it.
/// A frequency of zero means "rigid" and leaves the constraint's default softness/bias in place.
/// </remarks>
public struct SpringSettings
{
    /// <summary>Oscillation frequency in Hz. Zero means a rigid constraint.</summary>
    public Real Frequency;

    /// <summary>Damping ratio. 0 is undamped, 1 is critical damping.</summary>
    public Real DampingRatio;

    public SpringSettings(Real frequency, Real dampingRatio)
    {
        Frequency = frequency;
        DampingRatio = dampingRatio;
    }

    /// <summary>True when the spring is disabled and the constraint should stay rigid.</summary>
    public readonly bool IsRigid => Frequency <= (Real)0.0;

    /// <summary>
    /// Reduced mass of a pair of bodies. Static bodies contribute zero inverse mass.
    /// Returns 0 when both bodies are static.
    /// </summary>
    public static Real EffectiveMass(RigidBody body1, RigidBody body2)
    {
        Real inverse = body1.Data.InverseMass + body2.Data.InverseMass;
        return inverse > (Real)0.0 ? (Real)1.0 / inverse : (Real)0.0;
    }

    /// <summary>
    /// Fills <paramref name="softness"/> and <paramref name="biasFactor"/> for a constraint.
    /// When the spring is rigid or <paramref name="effectiveMass"/> is zero, returns
    /// <see langword="false"/> so the caller can keep the constraint's existing defaults.
    /// </summary>
    public readonly bool Compute(Real effectiveMass, Real dt, out Real softness, out Real biasFactor)
    {
        if (IsRigid || effectiveMass <= (Real)0.0 || dt <= (Real)0.0)
        {
            softness = Constraint.DefaultLinearSoftness;
            biasFactor = Constraint.DefaultLinearBias;
            return false;
        }

        Real omega = (Real)2.0 * MathR.PI * Frequency;
        Real damping = (Real)2.0 * effectiveMass * DampingRatio * omega;
        Real stiffness = effectiveMass * omega * omega;
        softness = (Real)1.0 / (damping + dt * stiffness);
        biasFactor = dt * stiffness * softness;
        return true;
    }
}
