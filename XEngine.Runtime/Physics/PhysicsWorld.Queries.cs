// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.LinearMath;

using XEngine.Vector;

namespace XEngine.Runtime;

public partial class PhysicsWorld
{
    /// <summary>
    /// Whether a query with this interaction should see trigger colliders.
    /// </summary>
    internal bool HitsTriggers(QueryTriggerInteraction interaction) => interaction switch
    {
        QueryTriggerInteraction.Ignore => false,
        QueryTriggerInteraction.Collide => true,
        _ => QueriesHitTriggers,
    };

    /// <summary>
    /// Shared pre-filter for shape-level queries: trigger policy, then layer mask.
    /// </summary>
    internal static bool AllowQueryShape(RigidBodyShape shape, LayerMask layerMask, bool hitTriggers)
    {
        if (!hitTriggers && shape.IsSensor) return false;
        return !ColliderBinding.TryGetLayer(shape, out int layer) || layerMask.HasLayer(layer);
    }

    internal bool RaycastInternal(Float3 origin, Float3 direction, float maxDistance, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction, bool allowNonShape, out RaycastHit hitInfo)
    {
        hitInfo = default;
        if (maxDistance < 0f) return false;
        if (!ValidateQuery(origin, direction, maxDistance, nameof(RaycastInternal))) return false;

        if (AutoSyncTransforms) SyncTransforms();

        EnterQuery(nameof(RaycastInternal));
        try
        {
            direction = Float3.Normalize(direction);
            var jOrigin = new JVector(origin.X, origin.Y, origin.Z);
            var jDirection = new JVector(direction.X, direction.Y, direction.Z);

            var handler = new ClosestRayHandler
            {
                Origin = origin,
                Direction = direction,
                LayerMask = layerMask,
                HitTriggers = HitsTriggers(queryTriggerInteraction),
                AllowNonShape = allowNonShape,
            };
            float maxLambda = maxDistance;
            World.DynamicTree.RayCast(jOrigin, jDirection, ref maxLambda, ref handler);
            hitInfo = handler.Hit;
            return handler.Hit.Hit;
        }
        finally
        {
            ExitQuery();
        }
    }

    /// <summary>
    /// Casts a ray from <paramref name="start"/> to <paramref name="end"/>. Matches Unity's <c>Physics.Linecast</c>.
    /// </summary>
    public bool Linecast(Float3 start, Float3 end)
        => Linecast(start, end, out _, LayerMask.Everything);

    /// <summary>
    /// Casts a ray from <paramref name="start"/> to <paramref name="end"/> and returns the closest hit.
    /// </summary>
    public bool Linecast(Float3 start, Float3 end, out RaycastHit hitInfo)
        => Linecast(start, end, out hitInfo, LayerMask.Everything);

    /// <summary>
    /// Casts a ray from <paramref name="start"/> to <paramref name="end"/> with layer filtering.
    /// </summary>
    public bool Linecast(Float3 start, Float3 end, out RaycastHit hitInfo, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
    {
        Float3 delta = end - start;
        float distance = Float3.Length(delta);
        if (distance <= 1e-8f)
        {
            hitInfo = default;
            return false;
        }

        return RaycastInternal(start, delta / distance, distance, layerMask, queryTriggerInteraction,
            allowNonShape: false, out hitInfo);
    }

    /// <summary>
    /// Collects every ray hit out to <paramref name="maxDistance"/> into <paramref name="results"/>,
    /// sorted near-to-far. Returns the number written, which is at most <paramref name="results"/>.Length.
    /// </summary>
    public int RaycastAll(Float3 origin, Float3 direction, float maxDistance, Span<RaycastHit> results)
        => RaycastAll(origin, direction, maxDistance, results, LayerMask.Everything);

    /// <summary>
    /// Collects every ray hit out to <paramref name="maxDistance"/> into <paramref name="results"/>,
    /// sorted near-to-far. Returns the number written.
    /// </summary>
    public int RaycastAll(Float3 origin, Float3 direction, float maxDistance, Span<RaycastHit> results,
        LayerMask layerMask, QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
    {
        int count = RaycastAll(origin, direction, maxDistance, _scratchRayHits, layerMask, queryTriggerInteraction);
        int written = count < results.Length ? count : results.Length;
        for (int i = 0; i < written; i++)
            results[i] = _scratchRayHits[i];
        return written;
    }

    /// <summary>
    /// Collects every ray hit into <paramref name="results"/>, which is cleared first.
    /// </summary>
    public int RaycastAll(Float3 origin, Float3 direction, float maxDistance, List<RaycastHit> results)
        => RaycastAll(origin, direction, maxDistance, results, LayerMask.Everything);

    /// <summary>
    /// Collects every ray hit into <paramref name="results"/>, which is cleared first.
    /// </summary>
    public int RaycastAll(Float3 origin, Float3 direction, float maxDistance, List<RaycastHit> results,
        LayerMask layerMask, QueryTriggerInteraction queryTriggerInteraction = QueryTriggerInteraction.UseGlobal)
    {
        ArgumentNullException.ThrowIfNull(results);
        results.Clear();
        if (maxDistance < 0f) return 0;
        if (AutoSyncTransforms) SyncTransforms();

        EnterQuery(nameof(RaycastAll));
        try
        {
            direction = Float3.Normalize(direction);
            var jOrigin = new JVector(origin.X, origin.Y, origin.Z);
            var jDirection = new JVector(direction.X, direction.Y, direction.Z);

            var handler = new CollectRayListHandler
            {
                Origin = origin,
                Direction = direction,
                LayerMask = layerMask,
                HitTriggers = HitsTriggers(queryTriggerInteraction),
                Results = results,
            };
            float maxLambda = maxDistance;
            World.DynamicTree.RayCast(jOrigin, jDirection, ref maxLambda, ref handler);
            if (results.Count > 1)
                results.Sort(RayHitDistanceComparer.Instance);
            return results.Count;
        }
        finally
        {
            ExitQuery();
        }
    }

    /// <summary>
    /// Computes the minimum translation that would separate <paramref name="colliderA"/> from
    /// <paramref name="colliderB"/>. Matches Unity's <c>Physics.ComputePenetration</c>.
    /// </summary>
    /// <param name="direction">World-space direction to move A so it no longer overlaps B.</param>
    /// <param name="distance">Distance to move A along <paramref name="direction"/>.</param>
    /// <returns><c>false</c> when the colliders are not overlapping or have no registered shapes.</returns>
    public bool ComputePenetration(Collider colliderA, Collider colliderB, out Float3 direction, out float distance)
    {
        direction = default;
        distance = 0f;
        if (colliderA == null || colliderB == null || colliderA == colliderB) return false;
        if (AutoSyncTransforms) SyncTransforms();

        RigidBodyShape[] shapesA = colliderA.Shapes;
        RigidBodyShape[] shapesB = colliderB.Shapes;
        if (shapesA.Length == 0 || shapesB.Length == 0) return false;

        float best = 0f;
        JVector bestNormal = default;
        bool found = false;

        for (int i = 0; i < shapesA.Length; i++)
        {
            RigidBodyShape a = shapesA[i];
            if (a.RigidBody == null) continue;
            for (int j = 0; j < shapesB.Length; j++)
            {
                RigidBodyShape b = shapesB[j];
                if (b.RigidBody == null) continue;

                bool overlaps = NarrowPhase.MprEpa(
                    a, b,
                    a.RigidBody.Data.Orientation, b.RigidBody.Data.Orientation,
                    a.RigidBody.Data.Position, b.RigidBody.Data.Position,
                    out _, out _, out JVector normal, out float penetration);

                if (!overlaps || penetration <= 0f) continue;
                if (!found || penetration > best)
                {
                    found = true;
                    best = penetration;
                    bestNormal = normal;
                }
            }
        }

        if (!found) return false;
        direction = new Float3(bestNormal.X, bestNormal.Y, bestNormal.Z);
        distance = best;
        return true;
    }

    /// <summary>
    /// Closest point on <paramref name="collider"/> to <paramref name="point"/>. If the point is
    /// inside the collider, <paramref name="point"/> itself is returned. Matches Unity's
    /// <c>Collider.ClosestPoint</c>.
    /// </summary>
    public Float3 ClosestPoint(Float3 point, Collider collider)
    {
        if (collider == null) return point;
        if (AutoSyncTransforms) SyncTransforms();

        RigidBodyShape[] shapes = collider.Shapes;
        if (shapes.Length == 0) return point;

        var jPoint = new JVector(point.X, point.Y, point.Z);
        float best = float.MaxValue;
        Float3 bestPoint = point;

        for (int i = 0; i < shapes.Length; i++)
        {
            RigidBodyShape shape = shapes[i];
            bool separated = shape.Distance(
                SupportPrimitives.CreatePoint(),
                JQuaternion.Identity,
                jPoint,
                out _, out JVector pointOnShape, out _, out float distance);

            if (!separated)
                return point;

            if (distance < best)
            {
                best = distance;
                bestPoint = new Float3(pointOnShape.X, pointOnShape.Y, pointOnShape.Z);
            }
        }

        return bestPoint;
    }

    /// <summary>
    /// Closest point on any collider to <paramref name="point"/>, within <paramref name="maxDistance"/>.
    /// </summary>
    public bool ClosestPoint(Float3 point, float maxDistance, out RaycastHit hitInfo)
        => ClosestPoint(point, maxDistance, LayerMask.Everything, QueryTriggerInteraction.UseGlobal, out hitInfo);

    /// <summary>
    /// Closest point on any collider matching <paramref name="layerMask"/> to <paramref name="point"/>.
    /// </summary>
    public bool ClosestPoint(Float3 point, float maxDistance, LayerMask layerMask,
        QueryTriggerInteraction queryTriggerInteraction, out RaycastHit hitInfo)
    {
        hitInfo = default;
        if (maxDistance < 0f) return false;
        if (AutoSyncTransforms) SyncTransforms();

        var handler = new ClosestPointHandler
        {
            Point = point,
            LayerMask = layerMask,
            HitTriggers = HitsTriggers(queryTriggerInteraction),
        };
        float bound = maxDistance;
        World.DynamicTree.FindNearest(
            SupportPrimitives.CreatePoint(),
            JQuaternion.Identity,
            new JVector(point.X, point.Y, point.Z),
            ref bound, ref handler);
        hitInfo = handler.Hit;
        return handler.Hit.Hit;
    }

    private static void FillRaycastHit(ref RaycastHit hit, IDynamicTreeProxy proxy, in JVector normal,
        float lambda, Float3 origin, Float3 direction)
    {
        hit.SetFromJitterResult(new DynamicTree.RayCastResult
        {
            Entity = proxy,
            Normal = normal,
            Lambda = lambda,
        }, origin, direction);
    }

    private static void FillShapeCastHit(ref ShapeCastHit hit, IDynamicTreeProxy proxy,
        in JVector pointA, in JVector pointB, in JVector normal, float fraction, float sweepLength)
    {
        var shape = proxy as RigidBodyShape;
        Rigidbody3D rigidbody = ColliderBinding.GetRigidbody(shape);
        Collider collider = ColliderBinding.GetCollider(shape);
        hit = new ShapeCastHit
        {
            Hit = true,
            Fraction = fraction,
            Distance = fraction * sweepLength,
            Normal = -(new Float3(normal.X, normal.Y, normal.Z)),
            Point = new Float3(pointA.X, pointA.Y, pointA.Z),
            HitPoint = new Float3(pointB.X, pointB.Y, pointB.Z),
            Rigidbody = rigidbody,
            Shape = shape,
            Collider = collider,
            Transform = collider?.GameObject?.Transform ?? rigidbody?.GameObject?.Transform,
        };
    }

    /// <summary>
    /// Rejects query inputs that would take NaN or infinity into the solver. Jitter's support maps use
    /// <c>Math.Sign</c> on the search direction, which throws outright on NaN, so a single poisoned
    /// caller would otherwise crash the frame from deep inside the narrow phase. Reporting no hit and
    /// naming the query gives the caller something to act on instead. Ported from upstream 9aeb182cf.
    /// </summary>
    internal static bool ValidateQuery(Float3 origin, Float3 direction, float maxDistance, string query)
    {
        if (IsFinite(origin) && IsFinite(direction) && float.IsFinite(maxDistance) && maxDistance >= 0f
            && Float3.LengthSquared(direction) > float.Epsilon)
            return true;

        Debug.LogErrorOnce($"Physics.InvalidQuery.{query}",
            $"[Physics] {query} was called with a NaN/Infinity origin, direction or maxDistance (or a zero direction), and was ignored.");
        return false;

        static bool IsFinite(Float3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }

    private sealed class RayHitDistanceComparer : IComparer<RaycastHit>
    {
        public static readonly RayHitDistanceComparer Instance = new();
        public int Compare(RaycastHit x, RaycastHit y) => x.Distance.CompareTo(y.Distance);
    }

    private struct ClosestRayHandler : IRayHitHandler
    {
        public Float3 Origin;
        public Float3 Direction;
        public LayerMask LayerMask;
        public bool HitTriggers;
        public bool AllowNonShape;
        public RaycastHit Hit;

        public bool AllowTest(IDynamicTreeProxy proxy)
        {
            if (proxy is RigidBodyShape shape)
                return AllowQueryShape(shape, LayerMask, HitTriggers);
            // Layer-masked queries respect terrain (upstream d96d36ca1): the terrain proxy is not a
            // RigidBodyShape, but it carries its GameObject's layer and participates in the mask.
            if (proxy is TerrainHeightmapProxy terrain)
                return LayerMask.HasLayer(terrain.LayerIndex);
            return AllowNonShape;
        }

        public void OnHit(IDynamicTreeProxy proxy, in JVector normal, float hitLambda, ref float maxLambda)
        {
            FillRaycastHit(ref Hit, proxy, normal, hitLambda, Origin, Direction);
            maxLambda = hitLambda;
        }
    }

    private struct CollectRayListHandler : IRayHitHandler
    {
        public Float3 Origin;
        public Float3 Direction;
        public LayerMask LayerMask;
        public bool HitTriggers;
        public List<RaycastHit> Results;

        public bool AllowTest(IDynamicTreeProxy proxy)
        {
            if (proxy is RigidBodyShape shape)
                return AllowQueryShape(shape, LayerMask, HitTriggers);
            if (proxy is TerrainHeightmapProxy terrain)
                return LayerMask.HasLayer(terrain.LayerIndex);
            return false;
        }

        public void OnHit(IDynamicTreeProxy proxy, in JVector normal, float hitLambda, ref float maxLambda)
        {
            RaycastHit hit = default;
            FillRaycastHit(ref hit, proxy, normal, hitLambda, Origin, Direction);
            Results.Add(hit);
        }
    }

    private struct ClosestSweepHandler : ISweepHitHandler
    {
        public LayerMask LayerMask;
        public bool HitTriggers;
        public RigidBodyShape QueryShape;
        public float SweepLength;
        public ShapeCastHit Hit;

        public bool AllowTest(IDynamicTreeProxy proxy)
        {
            if (ReferenceEquals(proxy, QueryShape)) return false;
            return proxy is RigidBodyShape shape && AllowQueryShape(shape, LayerMask, HitTriggers);
        }

        public void OnHit(IDynamicTreeProxy proxy, in JVector pointA, in JVector pointB, in JVector normal,
            float hitLambda, ref float maxLambda)
        {
            FillShapeCastHit(ref Hit, proxy, pointA, pointB, normal, hitLambda, SweepLength);
            maxLambda = hitLambda;
        }
    }

    private struct CollectSweepListHandler : ISweepHitHandler
    {
        public LayerMask LayerMask;
        public bool HitTriggers;
        public RigidBodyShape QueryShape;
        public float SweepLength;
        public List<ShapeCastHit> Hits;

        public bool AllowTest(IDynamicTreeProxy proxy)
        {
            if (ReferenceEquals(proxy, QueryShape)) return false;
            return proxy is RigidBodyShape shape && AllowQueryShape(shape, LayerMask, HitTriggers);
        }

        public void OnHit(IDynamicTreeProxy proxy, in JVector pointA, in JVector pointB, in JVector normal,
            float hitLambda, ref float maxLambda)
        {
            ShapeCastHit hit = default;
            FillShapeCastHit(ref hit, proxy, pointA, pointB, normal, hitLambda, SweepLength);
            Hits.Add(hit);
        }
    }

    private struct ClosestPointHandler : INearestHitHandler
    {
        public Float3 Point;
        public LayerMask LayerMask;
        public bool HitTriggers;
        public RaycastHit Hit;

        public bool AllowTest(IDynamicTreeProxy proxy)
            => proxy is RigidBodyShape shape && AllowQueryShape(shape, LayerMask, HitTriggers);

        public void OnHit(IDynamicTreeProxy proxy, in JVector pointA, in JVector pointB, in JVector normal,
            float hitDistance, ref float maxDistance)
        {
            var shape = proxy as RigidBodyShape;
            Hit = new RaycastHit
            {
                Hit = true,
                Distance = hitDistance,
                Point = new Float3(pointB.X, pointB.Y, pointB.Z),
                Normal = new Float3(normal.X, normal.Y, normal.Z),
                Shape = shape,
                Rigidbody = ColliderBinding.GetRigidbody(shape),
                Collider = ColliderBinding.GetCollider(shape),
            };
            Hit.Transform = Hit.Collider?.GameObject?.Transform ?? Hit.Rigidbody?.GameObject?.Transform;
            maxDistance = hitDistance;
        }
    }
}
