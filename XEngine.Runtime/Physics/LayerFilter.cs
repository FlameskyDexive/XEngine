// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Jitter2.Collision;
using Jitter2.Collision.Shapes;

namespace XEngine.Runtime;

public class LayerFilter : IBroadPhaseFilter
{
    private readonly struct Pair : IEquatable<Pair>
    {
        private readonly Rigidbody3D _a, _b;

        public Pair(Rigidbody3D shapeA, Rigidbody3D shapeB)
        {
            this._a = shapeA;
            this._b = shapeB;
        }

        public bool Equals(Pair other)
        {
            return _a.Equals(other._a) && _b.Equals(other._b);
        }

        public override bool Equals(object? obj)
        {
            return obj is Pair other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(_a, _b);
        }
    }

    // Per-world instance state (upstream 8e8ff9c59): a process-wide set made an ignore or
    // constraint-collision pair registered in one scene silently affect every other scene.
    private readonly HashSet<Pair> _ignore = [];
    private readonly Dictionary<Pair, int> _constraintCollide = [];

    internal void IgnoreCollisionBetween(Rigidbody3D bodyA, Rigidbody3D bodyB)
    {
        if (bodyA.IsNotValid() || bodyB.IsNotValid()) return;
        if (bodyA == bodyB) return;

        if (bodyB.InstanceID < bodyA.InstanceID) (bodyA, bodyB) = (bodyB, bodyA);
        _ignore.Add(new Pair(bodyA, bodyB));
    }

    internal void EnableCollisionBetween(Rigidbody3D bodyA, Rigidbody3D bodyB)
    {
        if (bodyA.IsNotValid() || bodyB.IsNotValid()) return;
        if (bodyA == bodyB) return;
        if (bodyB.InstanceID < bodyA.InstanceID) (bodyA, bodyB) = (bodyB, bodyA);
        _ignore.Remove(new Pair(bodyA, bodyB));
    }

    internal void AddConstraintCollision(Rigidbody3D bodyA, Rigidbody3D bodyB)
    {
        if (!TryOrderedPair(bodyA, bodyB, out Pair pair)) return;
        _constraintCollide.TryGetValue(pair, out int count);
        _constraintCollide[pair] = count + 1;
    }

    internal void RemoveConstraintCollision(Rigidbody3D bodyA, Rigidbody3D bodyB)
    {
        if (!TryOrderedPair(bodyA, bodyB, out Pair pair)) return;
        if (!_constraintCollide.TryGetValue(pair, out int count)) return;
        if (count <= 1) _constraintCollide.Remove(pair);
        else _constraintCollide[pair] = count - 1;
    }

    internal bool AllowsConstraintCollision(Rigidbody3D? bodyA, Rigidbody3D? bodyB)
        => TryOrderedPair(bodyA, bodyB, out Pair pair) && _constraintCollide.ContainsKey(pair);

    private static bool TryOrderedPair(Rigidbody3D? bodyA, Rigidbody3D? bodyB, out Pair pair)
    {
        pair = default;
        if (bodyA.IsNotValid() || bodyB.IsNotValid() || bodyA == bodyB) return false;
        if (bodyB.InstanceID < bodyA.InstanceID) (bodyA, bodyB) = (bodyB, bodyA);
        pair = new Pair(bodyA, bodyB);
        return true;
    }

    public bool Filter(IDynamicTreeProxy proxyA, IDynamicTreeProxy proxyB)
    {
        if (proxyA is RigidBodyShape rbsA && proxyB is RigidBodyShape rbsB)
        {
            // Things with constraints dont collide against eachother. (TODO: This should be toggleable)
            // No LINQ (PR0001): manual scan — Filter is a per-pair physics hot path.
            // Triggers are exempt: a trigger generates no contacts for a joint to fight over, and Unity
            // reports trigger events regardless of any joint between the bodies.
            var bodyA = rbsA.RigidBody;
            var bodyB = rbsB.RigidBody;
            bool isTriggerPair = rbsA.IsSensor || rbsB.IsSensor;
            if (!isTriggerPair)
            {
                if (HasConstraintBetween(bodyA, bodyB) || HasConstraintBetween(bodyB, bodyA))
                {
                    Rigidbody3D? jointA = ColliderBinding.Of(rbsA)?.Rigidbody
                        ?? (bodyA.Tag as Rigidbody3D.RigidBodyUserData)?.Rigidbody;
                    Rigidbody3D? jointB = ColliderBinding.Of(rbsB)?.Rigidbody
                        ?? (bodyB.Tag as Rigidbody3D.RigidBodyUserData)?.Rigidbody;
                    if (!AllowsConstraintCollision(jointA, jointB))
                        return false;
                }
            }

            // Layers come from the per-shape binding where available: a body can carry colliders from
            // several GameObjects, and Unity resolves the layer per collider rather than per body.
            // Shapes registered without a collider fall back to the body tag.
            ColliderBinding? bindingA = ColliderBinding.Of(rbsA);
            ColliderBinding? bindingB = ColliderBinding.Of(rbsB);

            var udA = bodyA.Tag as Rigidbody3D.RigidBodyUserData;
            var udB = bodyB.Tag as Rigidbody3D.RigidBodyUserData;

            if (bindingA == null && udA == null) return true;
            if (bindingB == null && udB == null) return true;

            bool isIgnored = false;
            Rigidbody3D rbA = bindingA?.Rigidbody ?? udA?.Rigidbody;
            Rigidbody3D rbB = bindingB?.Rigidbody ?? udB?.Rigidbody;
            if (rbA.IsValid() && rbB.IsValid())
            {
                // Order by InstanceID to match how IgnoreCollisionBetween stores the pair.
                if (rbB.InstanceID < rbA.InstanceID) (rbA, rbB) = (rbB, rbA);
                isIgnored = _ignore.Contains(new Pair(rbA, rbB));
            }

            int layerA = bindingA?.Layer ?? udA!.Layer;
            int layerB = bindingB?.Layer ?? udB!.Layer;
            bool canCollide = CollisionMatrix.GetLayerCollision(layerA, layerB);

            return canCollide && !isIgnored;
        }

        // If not both RigidBodyShapes, let other filters handle it (e.g., terrain collision)
        return true;
    }

    /// <summary>True when <paramref name="host"/> has a constraint whose other body is <paramref name="other"/>. No LINQ (PR0001).</summary>
    private static bool HasConstraintBetween(Jitter2.Dynamics.RigidBody host, Jitter2.Dynamics.RigidBody other)
    {
        foreach (var conn in host.Constraints)
        {
            if (conn.Body1 == other || conn.Body2 == other)
                return true;
        }
        return false;
    }
}
