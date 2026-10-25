// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

/// <summary>
/// Which stage of a contact or overlap a physics callback is reporting.
/// </summary>
public enum PhysicsEventPhase
{
    /// <summary>The first step in which the pair touches or overlaps.</summary>
    Enter,

    /// <summary>A step in which the pair keeps touching or overlapping, including the first.</summary>
    Stay,

    /// <summary>The step in which the pair stops touching or overlapping.</summary>
    Exit,
}

/// <summary>
/// Which physics callbacks a component implements, so a collider can skip overlap bookkeeping
/// entirely when nothing is listening.
/// </summary>
[Flags]
internal enum PhysicsCallbacks
{
    None = 0,

    TriggerEnter = 1 << 0,
    TriggerStay = 1 << 1,
    TriggerExit = 1 << 2,

    CollisionEnter = 1 << 3,
    CollisionStay = 1 << 4,
    CollisionExit = 1 << 5,

    AnyTrigger = TriggerEnter | TriggerStay | TriggerExit,
    AnyCollision = CollisionEnter | CollisionStay | CollisionExit,
    Any = AnyTrigger | AnyCollision,
}
