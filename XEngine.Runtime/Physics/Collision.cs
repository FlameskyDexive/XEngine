// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// One point of contact between two colliders, as reported to <c>OnCollision*</c>.
/// </summary>
public readonly struct ContactPoint
{
    /// <summary>The position of the contact, in world space.</summary>
    public readonly Float3 Point;

    /// <summary>
    /// The surface normal at the contact, in world space, pointing from
    /// <see cref="OtherCollider"/> towards <see cref="ThisCollider"/>.
    /// </summary>
    public readonly Float3 Normal;

    /// <summary>
    /// How far the colliders overlap along <see cref="Normal"/>. Zero or negative when they are only
    /// touching.
    /// </summary>
    public readonly float Separation;

    /// <summary>The collider receiving the callback.</summary>
    public readonly Collider ThisCollider;

    /// <summary>The collider being touched.</summary>
    public readonly Collider OtherCollider;

    internal ContactPoint(Float3 point, Float3 normal, float separation, Collider thisCollider, Collider otherCollider)
    {
        Point = point;
        Normal = normal;
        Separation = separation;
        ThisCollider = thisCollider;
        OtherCollider = otherCollider;
    }
}

/// <summary>
/// Describes a collision, as passed to <see cref="MonoBehaviour.OnCollisionEnter"/> and its siblings.
/// </summary>
/// <remarks>
/// Instances are pooled and reused across callbacks, so do not hold on to one past the end of the
/// callback that received it. Copy out the values you need instead.
/// </remarks>
public sealed class Collision
{
    private ContactPoint[] _contacts = new ContactPoint[4];
    private int _contactCount;

    /// <summary>The collider that was hit.</summary>
    public Collider Collider { get; private set; }

    /// <summary>The rigidbody of the collider that was hit, or null if it is static geometry.</summary>
    public Rigidbody3D Rigidbody { get; private set; }

    /// <summary>The transform of the collider that was hit.</summary>
    public Transform Transform => Collider?.GameObject?.Transform;

    /// <summary>The GameObject of the collider that was hit.</summary>
    public GameObject GameObject => Collider?.GameObject;

    /// <summary>The number of contact points in this collision.</summary>
    public int ContactCount => _contactCount;

    /// <summary>
    /// The relative velocity of the two colliders at the moment of impact, in world space.
    /// </summary>
    public Float3 RelativeVelocity { get; private set; }

    /// <summary>The total impulse applied to resolve this collision, in world space.</summary>
    public Float3 Impulse { get; private set; }

    /// <summary>The contact points of this collision.</summary>
    public ReadOnlySpan<ContactPoint> Contacts => new(_contacts, 0, _contactCount);

    /// <summary>
    /// Gets a contact point by index.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">If the index is outside <see cref="ContactCount"/>.</exception>
    public ContactPoint GetContact(int index)
    {
        if ((uint)index >= (uint)_contactCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _contacts[index];
    }

    internal void Reset(Collider collider, Rigidbody3D rigidbody, Float3 relativeVelocity, Float3 impulse)
    {
        Collider = collider;
        Rigidbody = rigidbody;
        RelativeVelocity = relativeVelocity;
        Impulse = impulse;
        _contactCount = 0;
    }

    /// <summary>
    /// Set after the contact points are walked, because the total impulse is their sum.
    /// </summary>
    internal void SetImpulse(Float3 impulse) => Impulse = impulse;

    internal void AddContact(in ContactPoint contact)
    {
        if (_contactCount == _contacts.Length)
            Array.Resize(ref _contacts, _contacts.Length * 2);
        _contacts[_contactCount++] = contact;
    }
}
