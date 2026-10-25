// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>Result of a Physics2D raycast. Written into caller <c>Span</c> buffers (NonAlloc).</summary>
public struct RaycastHit2D
{
    public bool Hit;
    public Float2 Point;
    public Float2 Normal;
    public float Fraction;
    public float Distance;
    public Rigidbody2D? Rigidbody;
    public Collider2D? Collider;
}
