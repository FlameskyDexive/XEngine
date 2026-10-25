// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Jitter2.Collision.Shapes;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Static Unity-style facade over the current scene's <see cref="PhysicsWorld"/>.
/// </summary>
/// <remarks>
/// Forwards to <c>Scene.Current.Physics</c>. Prefer the instance APIs when you already have a
/// scene in hand (tests, tools, multi-scene work); this is the gameplay convenience surface.
/// </remarks>
public static class Physics
{
    /// <summary>The physics world of <see cref="Scene.Current"/>.</summary>
    /// <exception cref="InvalidOperationException">No scene is current.</exception>
    public static PhysicsWorld World => Scene.Current?.Physics
        ?? throw new InvalidOperationException("Physics queries need an active scene (Scene.Current).");

    public static bool QueriesHitTriggers
    {
        get => World.QueriesHitTriggers;
        set => World.QueriesHitTriggers = value;
    }

    public static float BounceThreshold
    {
        get => World.BounceThreshold;
        set => World.BounceThreshold = value;
    }

    public static PhysicsMaterial DefaultMaterial
    {
        get => World.DefaultMaterial;
        set => World.DefaultMaterial = value;
    }

    public static void IgnoreCollisionBetween(Rigidbody3D bodyA, Rigidbody3D bodyB)
        => PhysicsWorld.IgnoreCollisionBetween(bodyA, bodyB);

    public static void EnableCollisionBetween(Rigidbody3D bodyA, Rigidbody3D bodyB)
        => PhysicsWorld.EnableCollisionBetween(bodyA, bodyB);

    public static bool Raycast(Float3 origin, Float3 direction)
        => World.Raycast(origin, direction);

    public static bool Raycast(Float3 origin, Float3 direction, out RaycastHit hitInfo)
        => World.Raycast(origin, direction, out hitInfo);

    public static bool Raycast(Float3 origin, Float3 direction, float maxDistance)
        => World.Raycast(origin, direction, maxDistance);

    public static bool Raycast(Float3 origin, Float3 direction, float maxDistance, out RaycastHit hitInfo)
        => World.Raycast(origin, direction, maxDistance, out hitInfo);

    public static bool Raycast(Float3 origin, Float3 direction, float maxDistance, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
        => World.Raycast(origin, direction, maxDistance, layerMask, queryTriggerInteraction);

    public static bool Raycast(Float3 origin, Float3 direction, out RaycastHit hitInfo, float maxDistance, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
        => World.Raycast(origin, direction, out hitInfo, maxDistance, layerMask, queryTriggerInteraction);

    public static bool Linecast(Float3 start, Float3 end)
        => World.Linecast(start, end);

    public static bool Linecast(Float3 start, Float3 end, out RaycastHit hitInfo)
        => World.Linecast(start, end, out hitInfo);

    public static bool Linecast(Float3 start, Float3 end, out RaycastHit hitInfo, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
        => World.Linecast(start, end, out hitInfo, layerMask, queryTriggerInteraction);

    public static int RaycastAll(Float3 origin, Float3 direction, float maxDistance, Span<RaycastHit> results)
        => World.RaycastAll(origin, direction, maxDistance, results);

    public static int RaycastAll(Float3 origin, Float3 direction, float maxDistance, Span<RaycastHit> results,
        LayerMask layerMask, QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
        => World.RaycastAll(origin, direction, maxDistance, results, layerMask, queryTriggerInteraction);

    public static int RaycastAll(Float3 origin, Float3 direction, float maxDistance, List<RaycastHit> results,
        LayerMask layerMask, QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
        => World.RaycastAll(origin, direction, maxDistance, results, layerMask, queryTriggerInteraction);

    public static bool ShapeCast(RigidBodyShape shape, Quaternion orientation, Float3 origin, Float3 direction,
        float maxDistance, out ShapeCastHit hitInfo, LayerMask layerMask)
        => World.ShapeCast(shape, orientation, origin, direction, maxDistance, out hitInfo, layerMask);

    public static bool SphereCast(Float3 origin, float radius, Float3 direction, float maxDistance, out ShapeCastHit hitInfo)
        => World.SphereCast(origin, radius, direction, maxDistance, out hitInfo);

    public static bool ComputePenetration(Collider colliderA, Collider colliderB, out Float3 direction, out float distance)
        => World.ComputePenetration(colliderA, colliderB, out direction, out distance);

    public static Float3 ClosestPoint(Float3 point, Collider collider)
        => World.ClosestPoint(point, collider);

    public static bool ClosestPoint(Float3 point, float maxDistance, out RaycastHit hitInfo)
        => World.ClosestPoint(point, maxDistance, out hitInfo);
}
