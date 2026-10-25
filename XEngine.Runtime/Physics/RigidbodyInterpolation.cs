// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime;

/// <summary>
/// How a <see cref="Rigidbody3D"/> smooths its Transform between fixed physics steps.
/// </summary>
/// <remarks>
/// Physics advances at <see cref="Time.FixedDeltaTime"/> while frames are presented at whatever rate
/// the display runs at. Without smoothing, a body's Transform holds still for the frames between two
/// steps and then jumps, which reads as stutter at any refresh rate that is not an exact multiple of
/// the fixed rate. Values match Unity's <c>RigidbodyInterpolation</c>.
/// </remarks>
public enum RigidbodyInterpolation
{
    /// <summary>
    /// The Transform is the simulated pose, updated only when a step happens.
    /// </summary>
    /// <remarks>
    /// The default, and the right choice for a body nothing watches closely — no per-body state, and
    /// the Transform and the simulation never disagree.
    /// </remarks>
    None = 0,

    /// <summary>
    /// The Transform is interpolated between the two most recent simulated poses.
    /// </summary>
    /// <remarks>
    /// Smooth and never overshoots, at the cost of the Transform trailing the simulation by up to one
    /// fixed step. The usual choice for anything the player follows, a camera target above all.
    /// </remarks>
    Interpolate = 1,

    /// <summary>
    /// The Transform is extrapolated forward from the latest simulated pose using current velocity.
    /// </summary>
    /// <remarks>
    /// No lag, but it predicts through collisions it cannot know about, so a body reaching a wall
    /// visibly penetrates it and then snaps back. Prefer <see cref="Interpolate"/> unless the lag
    /// itself is the problem.
    /// </remarks>
    Extrapolate = 2,
}
