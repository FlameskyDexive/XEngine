// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime;

/// <summary>Contact or trigger pair delivered from the reused Physics2D event queue.</summary>
public struct Collision2D
{
    public Collider2D? Collider;
    public Collider2D? OtherCollider;
    public Rigidbody2D? Rigidbody;
    public Rigidbody2D? OtherRigidbody;
}
