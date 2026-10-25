// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime;

/// <summary>
/// How hard a <see cref="Rigidbody3D"/> works to avoid passing through obstacles within one step.
/// </summary>
/// <remarks>
/// A body moving faster than its own thickness per step can start a step on one side of a wall and end
/// it on the other, with no overlap at either sample point for the solver to react to. The continuous
/// modes close that gap with a swept test that finds where the two shapes first touch and places a
/// contact there. Values match Unity's <c>CollisionDetectionMode</c>.
/// </remarks>
public enum CollisionDetectionMode
{
    /// <summary>
    /// No swept test; contacts appear only once shapes overlap.
    /// </summary>
    /// <remarks>The default, and enough for anything that does not move a large fraction of its own
    /// size per step.</remarks>
    Discrete = 0,

    /// <summary>
    /// Swept against static and kinematic colliders; discrete against other rigidbodies.
    /// </summary>
    /// <remarks>The usual choice for a projectile: level geometry is what it must not escape through,
    /// and leaving dynamic partners discrete keeps the cost down.</remarks>
    Continuous = 1,

    /// <summary>
    /// Swept against static and kinematic colliders, and against rigidbodies that are themselves
    /// <see cref="Continuous"/> or <see cref="ContinuousDynamic"/>.
    /// </summary>
    /// <remarks>Both sides have to opt in, so a fast body among many discrete objects still sweeps
    /// against none of them.</remarks>
    ContinuousDynamic = 2,

    /// <summary>
    /// Swept against every partner, unconditionally.
    /// </summary>
    ContinuousSpeculative = 3,
}
