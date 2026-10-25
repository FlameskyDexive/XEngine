// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// How two colliders' material coefficients are reduced to the single value their contact solves with.
/// </summary>
/// <remarks>
/// Declaration order matters: a pair has two modes and solves with one, resolved by taking the
/// numerically larger. So <see cref="Maximum"/> wins over <see cref="Multiply"/>, which wins over
/// <see cref="Minimum"/>, which wins over <see cref="Average"/>. The values match Unity's
/// <c>PhysicsMaterialCombine</c>, so numbers authored against Unity transfer directly.
/// </remarks>
public enum PhysicsMaterialCombine
{
    /// <summary>The arithmetic mean of the two coefficients.</summary>
    Average = 0,

    /// <summary>The smaller of the two coefficients.</summary>
    Minimum = 1,

    /// <summary>The product of the two coefficients.</summary>
    Multiply = 2,

    /// <summary>The larger of the two coefficients.</summary>
    Maximum = 3,
}

/// <summary>
/// Shared friction and bounciness for <see cref="Collider"/> shapes.
/// </summary>
/// <remarks>
/// Assign to <see cref="Collider.SharedMaterial"/>. A collider with no material inherits the friction and
/// restitution of its <see cref="Rigidbody3D"/>, or the project's default material for static geometry.
/// <para>
/// XEngine's contact solver uses a single Coulomb friction coefficient, so unlike Unity there is no
/// separate static friction. <see cref="Friction"/> is the sliding coefficient; the field is not split
/// rather than being split and half-ignored.
/// </para>
/// </remarks>
[CreateAssetMenu("Physics/Physics Material", Extension = ".physicmaterial", Order = 50)]
public sealed class PhysicsMaterial : ScriptableObject
{
    [SerializeField] private float friction = 0.6f;
    [SerializeField] private float bounciness;
    [SerializeField] private PhysicsMaterialCombine frictionCombine = PhysicsMaterialCombine.Average;
    [SerializeField] private PhysicsMaterialCombine bounceCombine = PhysicsMaterialCombine.Average;

    /// <summary>Resistance to sliding. 0 slides freely; values above 1 are permitted and grip hard.</summary>
    public float Friction
    {
        get => friction;
        set => friction = Maths.Max(value, 0f);
    }

    /// <summary>
    /// Fraction of approach speed returned on impact, from 0 (no bounce) to 1 (lossless).
    /// </summary>
    /// <remarks>
    /// A contact only bounces once its approach speed exceeds
    /// <see cref="PhysicsWorld.BounceThreshold"/>, which is what stops a bouncy body from jittering
    /// forever instead of coming to rest.
    /// </remarks>
    public float Bounciness
    {
        get => bounciness;
        set => bounciness = Maths.Clamp(value, 0f, 1f);
    }

    /// <summary>How <see cref="Friction"/> combines with the other collider's.</summary>
    public PhysicsMaterialCombine FrictionCombine
    {
        get => frictionCombine;
        set => frictionCombine = value;
    }

    /// <summary>How <see cref="Bounciness"/> combines with the other collider's.</summary>
    public PhysicsMaterialCombine BounceCombine
    {
        get => bounceCombine;
        set => bounceCombine = value;
    }
}
