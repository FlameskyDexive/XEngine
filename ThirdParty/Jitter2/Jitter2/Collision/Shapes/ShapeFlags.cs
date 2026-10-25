/*
 * Jitter2 Physics Library
 * (c) Thorben Linneweber and contributors
 * SPDX-License-Identifier: MIT
 */

using System;

namespace Jitter2.Collision.Shapes;

/// <summary>
/// Behavioural flags for a <see cref="Shape"/>.
/// </summary>
/// <remarks>
/// XENGINE: collected into one field so the narrow phase can decide with a single read and branch
/// whether a shape needs special handling. A shape with <see cref="None"/> takes the same path it
/// would without any of these features.
/// </remarks>
[Flags]
public enum ShapeFlags
{
    /// <summary>
    /// No special handling. Collisions generate contacts and are solved normally.
    /// </summary>
    None = 0,

    /// <summary>
    /// The shape detects overlaps but never generates contacts, so it applies no forces and is not
    /// seen by the solver. See <see cref="Shape.IsSensor"/>.
    /// </summary>
    Sensor = 1 << 0,

    /// <summary>
    /// Pairs involving the shape are tracked and reported through the world's pair-event stream.
    /// See <see cref="Shape.ReportsPairEvents"/>.
    /// </summary>
    ReportsPairEvents = 1 << 1,
}
