// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

/// <summary>
/// World-space degrees of freedom a <see cref="Rigidbody3D"/> is not allowed to move along.
/// </summary>
/// <remarks>
/// The bit values match Unity's <c>RigidbodyConstraints</c>, including its unused low bit, so that
/// authored values and inspector bitmasks carry over unchanged.
/// </remarks>
[Flags]
public enum RigidbodyConstraints
{
    None = 0,

    FreezePositionX = 1 << 1,
    FreezePositionY = 1 << 2,
    FreezePositionZ = 1 << 3,

    FreezeRotationX = 1 << 4,
    FreezeRotationY = 1 << 5,
    FreezeRotationZ = 1 << 6,

    FreezePosition = FreezePositionX | FreezePositionY | FreezePositionZ,
    FreezeRotation = FreezeRotationX | FreezeRotationY | FreezeRotationZ,
    FreezeAll = FreezePosition | FreezeRotation,
}
