// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Crowd;

using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

/// <summary>Mapped Recast crowd parameters (radius / height / speed / acceleration / avoidance / priority).</summary>
public struct NavMeshCrowdParams
{
    public float Radius;
    public float Height;
    public float MaxSpeed;
    public float Acceleration;
    public float Avoidance;
    public float Priority;

    public static NavMeshCrowdParams Default => new()
    {
        Radius = 0.5f,
        Height = 2f,
        MaxSpeed = 3.5f,
        Acceleration = 8f,
        Avoidance = 1f,
        Priority = 2f,
    };

    public static NavMeshCrowdParams FromAgent(NavMeshAgent agent) => new()
    {
        Radius = agent.Radius,
        Height = agent.Height,
        MaxSpeed = agent.Speed,
        Acceleration = agent.Acceleration,
        Avoidance = 1f,
        Priority = 2f,
    };
}

/// <summary>Thin <see cref="DtCrowd"/> wrapper bound to the currently loaded navmesh.</summary>
public sealed class NavMeshCrowd
{
    private DtCrowd? _crowd;
    private float _maxRadius = 0.6f;

    public DtCrowd? Runtime => _crowd;
    public int ActiveCount => _crowd?.GetActiveAgents().Count ?? 0;

    public void Bind(DtNavMesh? mesh, float maxAgentRadius = 0.6f)
    {
        _maxRadius = MathF.Max(0.1f, maxAgentRadius);
        if (mesh == null)
        {
            _crowd = null;
            return;
        }

        _crowd = new DtCrowd(new DtCrowdConfig(_maxRadius), mesh);
    }

    public void Clear() => _crowd = null;

    public DtCrowdAgent AddAgent(Float3 position, NavMeshCrowdParams parameters)
    {
        DtCrowd crowd = Require();
        return crowd.AddAgent(NavMeshConvert.ToRecast(position), ToDt(parameters));
    }

    public void RemoveAgent(DtCrowdAgent agent)
    {
        if (_crowd == null || agent == null) return;
        _crowd.RemoveAgent(agent);
    }

    public bool SetDestination(DtCrowdAgent agent, Float3 destination)
    {
        DtCrowd crowd = Require();
        DtNavMeshQuery query = crowd.GetNavMeshQuery();
        RcVec3f extents = crowd.GetQueryExtents();
        IDtQueryFilter filter = crowd.GetFilter(0);
        DtStatus status = query.FindNearestPoly(NavMeshConvert.ToRecast(destination), extents, filter,
            out long polyRef, out RcVec3f nearest, out _);
        if (status.Failed() || polyRef == 0) return false;
        return crowd.RequestMoveTarget(agent, polyRef, nearest);
    }

    public void Update(float deltaTime)
    {
        if (_crowd == null) return;
        _crowd.Update(MathF.Max(0f, deltaTime), null!);
    }

    public Float3 GetPosition(DtCrowdAgent agent) => NavMeshConvert.ToEngine(agent.npos);

    public Float3 GetVelocity(DtCrowdAgent agent) => NavMeshConvert.ToEngine(agent.vel);

    private DtCrowd Require()
        => _crowd ?? throw new InvalidOperationException("NavMeshCrowd has no loaded navmesh.");

    private static DtCrowdAgentParams ToDt(NavMeshCrowdParams p)
    {
        float radius = MathF.Max(0.05f, p.Radius);
        return new DtCrowdAgentParams
        {
            radius = radius,
            height = MathF.Max(0.1f, p.Height),
            maxSpeed = MathF.Max(0f, p.MaxSpeed),
            maxAcceleration = MathF.Max(0f, p.Acceleration),
            collisionQueryRange = radius * 12f,
            pathOptimizationRange = radius * 30f,
            separationWeight = MathF.Max(0f, p.Priority) * MathF.Max(0.1f, p.Avoidance),
            obstacleAvoidanceType = 0,
            queryFilterType = 0,
            updateFlags = DtCrowdAgentUpdateFlags.DT_CROWD_ANTICIPATE_TURNS
                | DtCrowdAgentUpdateFlags.DT_CROWD_OBSTACLE_AVOIDANCE
                | DtCrowdAgentUpdateFlags.DT_CROWD_SEPARATION
                | DtCrowdAgentUpdateFlags.DT_CROWD_OPTIMIZE_VIS
                | DtCrowdAgentUpdateFlags.DT_CROWD_OPTIMIZE_TOPO,
        };
    }
}
