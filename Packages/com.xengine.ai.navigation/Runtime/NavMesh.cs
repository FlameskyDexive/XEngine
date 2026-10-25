// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using DotRecast.Core.Numerics;
using DotRecast.Detour;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

/// <summary>Static query API over the currently loaded <see cref="NavMeshData"/> set.</summary>
public static class NavMesh
{
    public static void AddNavMeshData(NavMeshData data) => NavMeshWorld.Instance.Add(data);

    public static void RemoveNavMeshData(NavMeshData data) => NavMeshWorld.Instance.Remove(data);

    public static void RemoveAllNavMeshData() => NavMeshWorld.Instance.Clear();

    public static NavMeshData BuildNavMesh(Scene scene, NavMeshBuildSettings? settings = null, LayerMask? layers = null)
        => NavMeshBuilder.Build(scene, settings, layers);

    public static NavMeshData UpdateNavMesh(Scene scene, NavMeshData data, AABB dirtyBounds, LayerMask? layers = null)
        => NavMeshBuilder.Update(scene, data, dirtyBounds, layers);

    public static NavMeshCrowd Crowd => NavMeshWorld.Instance.Crowd;

    public static int LoadedCount => NavMeshWorld.Instance.LoadedCount;

    public static bool HasNavMesh => NavMeshWorld.Instance.Query != null;

    public static float GetAreaCost(int area) => NavMeshWorld.Instance.GetAreaCost(area);

    public static void SetAreaCost(int area, float cost) => NavMeshWorld.Instance.SetAreaCost(area, cost);

    public static bool SamplePosition(Float3 sourcePosition, out NavMeshHit hit, float maxDistance)
    {
        hit = default;
        DtNavMeshQuery? query = NavMeshWorld.Instance.Query;
        if (query == null) return false;

        RcVec3f center = NavMeshConvert.ToRecast(sourcePosition);
        RcVec3f extents = NavMeshWorld.Instance.QueryExtents;
        if (maxDistance > 0f)
        {
            float y = MathF.Max(extents.Y, maxDistance);
            extents = new RcVec3f(maxDistance, y, maxDistance);
        }

        DtStatus status = query.FindNearestPoly(center, extents, NavMeshWorld.Instance.Filter,
            out long polyRef, out RcVec3f nearest, out _);
        if (status.Failed() || polyRef == 0) return false;

        Float3 point = NavMeshConvert.ToEngine(nearest);
        float dist = Float3.Distance(sourcePosition, point);
        if (maxDistance > 0f && dist > maxDistance) return false;

        hit.Position = point;
        hit.Normal = Float3.UnitY;
        hit.Distance = dist;
        hit.Hit = true;
        return true;
    }

    public static bool CalculatePath(Float3 sourcePosition, Float3 targetPosition, NavMeshPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        path.Clear();

        DtNavMeshQuery? query = NavMeshWorld.Instance.Query;
        if (query == null) return false;

        RcVec3f extents = NavMeshWorld.Instance.QueryExtents;
        IDtQueryFilter filter = NavMeshWorld.Instance.Filter;
        RcVec3f start = NavMeshConvert.ToRecast(sourcePosition);
        RcVec3f end = NavMeshConvert.ToRecast(targetPosition);

        DtStatus startStatus = query.FindNearestPoly(start, extents, filter, out long startRef, out RcVec3f startPt, out _);
        DtStatus endStatus = query.FindNearestPoly(end, extents, filter, out long endRef, out RcVec3f endPt, out _);
        if (startStatus.Failed() || endStatus.Failed() || startRef == 0 || endRef == 0)
            return false;

        long[] corridor = NavMeshWorld.Instance.Corridor;
        DtStatus pathStatus = query.FindPath(startRef, endRef, startPt, endPt, filter, corridor, out int pathCount, corridor.Length);
        if (pathStatus.Failed() || pathCount <= 0) return false;

        DtStraightPath[] straight = NavMeshWorld.Instance.Straight;
        DtStatus straightStatus = query.FindStraightPath(startPt, endPt, corridor.AsSpan(0, pathCount), pathCount,
            straight, out int straightCount, straight.Length, 0);
        if (straightStatus.Failed() || straightCount <= 0) return false;

        Float3[] scratch = NavMeshWorld.Instance.CornerScratch;
        if (scratch.Length < straightCount)
            scratch = NavMeshWorld.Instance.GrowCornerScratch(straightCount);

        for (int i = 0; i < straightCount; i++)
            scratch[i] = NavMeshConvert.ToEngine(straight[i].pos);

        bool partial = pathStatus.IsPartial();
        path.SetCorners(scratch.AsSpan(0, straightCount),
            partial ? NavMeshPathStatus.PathPartial : NavMeshPathStatus.PathComplete);
        return true;
    }

    public static bool Raycast(Float3 sourcePosition, Float3 targetPosition, out NavMeshHit hit)
    {
        hit = default;
        DtNavMeshQuery? query = NavMeshWorld.Instance.Query;
        if (query == null) return false;

        RcVec3f extents = NavMeshWorld.Instance.QueryExtents;
        IDtQueryFilter filter = NavMeshWorld.Instance.Filter;
        RcVec3f start = NavMeshConvert.ToRecast(sourcePosition);
        RcVec3f end = NavMeshConvert.ToRecast(targetPosition);
        DtStatus startStatus = query.FindNearestPoly(start, extents, filter, out long startRef, out RcVec3f startPt, out _);
        if (startStatus.Failed() || startRef == 0) return false;

        long[] corridor = NavMeshWorld.Instance.Corridor;
        DtStatus status = query.Raycast(startRef, startPt, end, filter, out float t, out RcVec3f normal, corridor, out _, corridor.Length);
        if (status.Failed()) return false;

        if (t >= 1f)
        {
            hit.Position = targetPosition;
            hit.Normal = NavMeshConvert.ToEngine(normal);
            hit.Distance = Float3.Distance(sourcePosition, targetPosition);
            hit.Hit = false;
            return false;
        }

        Float3 point = sourcePosition + (targetPosition - sourcePosition) * t;
        hit.Position = point;
        hit.Normal = NavMeshConvert.ToEngine(normal);
        hit.Distance = Float3.Distance(sourcePosition, point);
        hit.Hit = true;
        return true;
    }

    public static bool FindClosestEdge(Float3 sourcePosition, out NavMeshHit hit)
    {
        hit = default;
        DtNavMeshQuery? query = NavMeshWorld.Instance.Query;
        if (query == null) return false;

        RcVec3f extents = NavMeshWorld.Instance.QueryExtents;
        IDtQueryFilter filter = NavMeshWorld.Instance.Filter;
        RcVec3f center = NavMeshConvert.ToRecast(sourcePosition);
        DtStatus nearest = query.FindNearestPoly(center, extents, filter, out long startRef, out RcVec3f startPt, out _);
        if (nearest.Failed() || startRef == 0) return false;

        DtStatus status = query.FindDistanceToWall(startRef, startPt, extents.X * 4f, filter,
            out float dist, out RcVec3f hitPos, out RcVec3f hitNormal);
        if (status.Failed()) return false;

        hit.Position = NavMeshConvert.ToEngine(hitPos);
        hit.Normal = NavMeshConvert.ToEngine(hitNormal);
        hit.Distance = dist;
        hit.Hit = true;
        return true;
    }
}
