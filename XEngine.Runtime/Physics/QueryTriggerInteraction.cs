// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime;

/// <summary>
/// Per-query override for whether a ray, sweep or overlap should see trigger colliders.
/// Matches Unity's <c>QueryTriggerInteraction</c>.
/// </summary>
public enum QueryTriggerInteraction
{
    /// <summary>Use <see cref="PhysicsWorld.QueriesHitTriggers"/>.</summary>
    UseGlobal = 0,

    /// <summary>Skip trigger colliders, regardless of the world setting.</summary>
    Ignore = 1,

    /// <summary>Include trigger colliders, regardless of the world setting.</summary>
    Collide = 2,
}
