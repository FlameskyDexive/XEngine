// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime;

/// <summary>How a <see cref="Rigidbody2D"/> participates in the Box2D simulation.</summary>
public enum RigidbodyType2D
{
    Dynamic = 0,
    Kinematic = 1,
    Static = 2
}
