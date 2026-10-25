// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>Shared friction/restitution for <see cref="Collider2D"/> shapes. One engine unit is one meter.</summary>
[CreateAssetMenu("Physics 2D/Physics Material 2D", Extension = ".physicmaterial2d", Order = 50)]
public sealed class PhysicsMaterial2D : ScriptableObject
{
    [SerializeField] private float friction = 0.4f;
    [SerializeField] private float restitution;

    public float Friction
    {
        get => friction;
        set => friction = Maths.Max(value, 0f);
    }

    public float Restitution
    {
        get => restitution;
        set => restitution = Maths.Clamp(value, 0f, 1f);
    }
}
