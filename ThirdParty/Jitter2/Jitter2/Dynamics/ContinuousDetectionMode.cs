// XENGINE: local addition. Continuous collision detection modes, see ThirdParty/Jitter2/PATCHES.md.

using System.Runtime.CompilerServices;

namespace Jitter2.Dynamics;

/// <summary>
/// How much effort a body spends on not passing through obstacles within a single step.
/// </summary>
/// <remarks>
/// All non-discrete modes use the same machinery — <see cref="Collision.NarrowPhase"/>'s swept test
/// finds the time of impact and a contact is placed at the touch point, so the solver stops the body
/// short of the obstacle. The modes differ only in <em>which pairs</em> are worth sweeping, which is
/// what makes them a cost dial rather than a quality dial.
/// </remarks>
public enum ContinuousDetectionMode
{
    /// <summary>
    /// No swept test. Contacts appear only once shapes actually overlap.
    /// </summary>
    /// <remarks>
    /// The default, and correct for anything that cannot cross its own thickness in one step. A body
    /// that can will pass through thin geometry.
    /// </remarks>
    Discrete = 0,

    /// <summary>
    /// Swept against everything that is not dynamic.
    /// </summary>
    /// <remarks>
    /// Level geometry and kinematic platforms are what a fast body usually needs not to escape
    /// through, and they are also the cheap half of the problem: a static partner cannot itself be
    /// moving toward the body.
    /// </remarks>
    Continuous = 1,

    /// <summary>
    /// Swept against everything that is not dynamic, and against dynamic bodies that are themselves
    /// continuous.
    /// </summary>
    /// <remarks>
    /// Requiring both sides to opt in keeps the pair count down: a fast body among a thousand discrete
    /// crates sweeps against none of them.
    /// </remarks>
    ContinuousDynamic = 2,

    /// <summary>
    /// Swept against every partner, unconditionally.
    /// </summary>
    /// <remarks>
    /// The mode to reach for when a body must not tunnel through anything at all and the pair count is
    /// not the concern.
    /// </remarks>
    Speculative = 3,
}

internal static class ContinuousDetectionModeExtensions
{
    /// <summary>
    /// Whether a body in this mode wants a swept test against the given partner.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool WantsSweepAgainst(this ContinuousDetectionMode mode,
        MotionType partnerMotion, ContinuousDetectionMode partnerMode)
    {
        switch (mode)
        {
            case ContinuousDetectionMode.Speculative:
                return true;

            case ContinuousDetectionMode.Continuous:
                return partnerMotion != MotionType.Dynamic;

            case ContinuousDetectionMode.ContinuousDynamic:
                return partnerMotion != MotionType.Dynamic
                       || partnerMode is ContinuousDetectionMode.Continuous
                                      or ContinuousDetectionMode.ContinuousDynamic;

            default:
                return false;
        }
    }
}
