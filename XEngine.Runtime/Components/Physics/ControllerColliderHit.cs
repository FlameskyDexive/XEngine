// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Information about a hit produced by <see cref="CharacterController.Move"/>.
/// Matches Unity's <c>ControllerColliderHit</c>.
/// </summary>
public struct ControllerColliderHit
{
    public CharacterController Controller;
    public Collider Collider;
    public Rigidbody3D Rigidbody;
    public Transform Transform;
    public GameObject GameObject;
    public Float3 Point;
    public Float3 Normal;
    public Float3 MoveDirection;
    public float MoveLength;
}
