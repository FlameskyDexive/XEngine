// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.TileCache;

using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

/// <summary>Per-process loaded navmeshes, reusable query scratch, and registered agents/links.</summary>
internal sealed class NavMeshWorld
{
    public static readonly NavMeshWorld Instance = new();

    private readonly List<NavMeshData> _loaded = [];
    private readonly List<NavMeshAgent> _agents = [];
    private readonly List<OffMeshLink> _links = [];
    private readonly NavMeshCrowd _crowd = new();
    private readonly float[] _areaCosts = CreateDefaultCosts();
    private DtQueryDefaultFilter _filter = new();
    private readonly long[] _corridor = new long[256];
    private readonly DtStraightPath[] _straight = new DtStraightPath[64];
    private Float3[] _cornerScratch = new Float3[64];
    private DtNavMeshQuery? _query;
    private DtNavMesh? _mesh;
    private DtTileCache? _tileCache;

    public DtNavMesh? Mesh => _mesh;
    public DtNavMeshQuery? Query => _query;
    public DtTileCache? TileCache => _tileCache;
    public NavMeshCrowd Crowd => _crowd;
    public IDtQueryFilter Filter => _filter;
    public long[] Corridor => _corridor;
    public DtStraightPath[] Straight => _straight;
    public Float3[] CornerScratch => _cornerScratch;
    public IReadOnlyList<NavMeshAgent> Agents => _agents;
    public int LoadedCount => _loaded.Count;

    public Float3[] GrowCornerScratch(int minLength)
    {
        if (_cornerScratch.Length < minLength)
            _cornerScratch = new Float3[minLength];
        return _cornerScratch;
    }

    public RcVec3f QueryExtents
    {
        get
        {
            float radius = 2f;
            float height = 4f;
            if (_loaded.Count > 0)
            {
                NavMeshBuildSettings s = _loaded[0].Settings;
                radius = MathF.Max(2f, s.AgentRadius * 4f);
                height = MathF.Max(4f, s.AgentHeight * 2f);
            }

            return new RcVec3f(radius, height, radius);
        }
    }

    public void Add(NavMeshData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        for (int i = 0; i < _loaded.Count; i++)
        {
            if (ReferenceEquals(_loaded[i], data)) return;
        }

        _loaded.Add(data);
        Bind(data.RuntimeMesh, data.RuntimeTileCache);
    }

    public void Remove(NavMeshData data)
    {
        if (!_loaded.Remove(data)) return;
        if (_loaded.Count == 0)
        {
            _mesh = null;
            _query = null;
            _tileCache = null;
            _crowd.Clear();
            return;
        }

        NavMeshData remaining = _loaded[^1];
        Bind(remaining.RuntimeMesh, remaining.RuntimeTileCache);
    }

    public void Clear()
    {
        _loaded.Clear();
        _mesh = null;
        _query = null;
        _tileCache = null;
        _crowd.Clear();
    }

    public void RegisterAgent(NavMeshAgent agent)
    {
        if (agent == null) return;
        for (int i = 0; i < _agents.Count; i++)
        {
            if (ReferenceEquals(_agents[i], agent)) return;
        }

        _agents.Add(agent);
    }

    public void UnregisterAgent(NavMeshAgent agent) => _agents.Remove(agent);

    public void RegisterLink(OffMeshLink link)
    {
        if (link == null) return;
        for (int i = 0; i < _links.Count; i++)
        {
            if (ReferenceEquals(_links[i], link)) return;
        }

        _links.Add(link);
    }

    public void UnregisterLink(OffMeshLink link) => _links.Remove(link);

    public bool TryGetAutoOffMeshLink(Float3 position, Float3 destination, float maxDistance, out OffMeshLinkData data)
    {
        data = default;
        float best = maxDistance;
        OffMeshLink? bestLink = null;
        bool reverse = false;
        float destDist = Dist2D(position, destination);

        for (int i = 0; i < _links.Count; i++)
        {
            OffMeshLink link = _links[i];
            if (link == null || !link.Activated || !link.EnabledInHierarchy) continue;

            Float3 start = link.StartWorld;
            Float3 end = link.EndWorld;
            float dStart = Dist2D(position, start);
            if (dStart < best && Dist2D(destination, end) + 0.5f < destDist)
            {
                best = dStart;
                bestLink = link;
                reverse = false;
            }

            if (!link.Bidirectional) continue;
            float dEnd = Dist2D(position, end);
            if (dEnd < best && Dist2D(destination, start) + 0.5f < destDist)
            {
                best = dEnd;
                bestLink = link;
                reverse = true;
            }
        }

        if (bestLink == null) return false;
        data.Valid = true;
        data.Activated = true;
        data.OffMeshLink = bestLink;
        data.StartPos = reverse ? bestLink.EndWorld : bestLink.StartWorld;
        data.EndPos = reverse ? bestLink.StartWorld : bestLink.EndWorld;
        return true;
    }

    public float GetAreaCost(int area) => _areaCosts[ClampArea(area)];

    public void SetAreaCost(int area, float cost)
    {
        _areaCosts[ClampArea(area)] = cost;
        _filter = new DtQueryDefaultFilter(0xffff, 0, _areaCosts);
    }

    public long AddObstacle(NavMeshObstacle obstacle)
    {
        if (_tileCache == null || obstacle == null) return 0;
        AABB bounds = obstacle.WorldBounds;
        long refs;
        if (obstacle.Shape == NavMeshObstacleShape.Capsule)
        {
            refs = _tileCache.AddObstacle(NavMeshConvert.ToRecast(obstacle.Transform.Position),
                MathF.Max(0.05f, obstacle.Radius), MathF.Max(0.1f, obstacle.Height));
        }
        else
        {
            float yaw = obstacle.Transform.EulerAngles.Y * MathF.PI / 180f;
            if (MathF.Abs(yaw) < 0.01f)
            {
                refs = _tileCache.AddBoxObstacle(NavMeshConvert.ToRecast(bounds.Min), NavMeshConvert.ToRecast(bounds.Max));
            }
            else
            {
                Float3 center = obstacle.Transform.Position;
                Float3 extents = obstacle.Size * 0.5f;
                refs = _tileCache.AddBoxObstacle(NavMeshConvert.ToRecast(center), NavMeshConvert.ToRecast(extents), yaw);
            }
        }

        NavMeshTileCache.Pump(_tileCache);
        RefreshQuery();
        return refs;
    }

    public void RemoveObstacle(long refs)
    {
        if (_tileCache == null || refs == 0) return;
        _tileCache.RemoveObstacle(refs);
        NavMeshTileCache.Pump(_tileCache);
        RefreshQuery();
    }

    public void RefreshQuery()
    {
        if (_mesh != null)
            _query = new DtNavMeshQuery(_mesh);
    }

    private static int ClampArea(int area) => area < 0 ? 0 : (area > 63 ? 63 : area);

    private static float Dist2D(Float3 a, Float3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static float[] CreateDefaultCosts()
    {
        var costs = new float[64];
        for (int i = 0; i < costs.Length; i++)
            costs[i] = 1f;
        costs[NavMeshAreas.Water] = 8f;
        costs[NavMeshAreas.Jump] = 1.5f;
        return costs;
    }

    private void Bind(DtNavMesh mesh, DtTileCache? tileCache = null)
    {
        _mesh = mesh;
        _tileCache = tileCache;
        _query = new DtNavMeshQuery(mesh);
        float radius = 0.6f;
        if (_loaded.Count > 0)
            radius = MathF.Max(0.6f, _loaded[0].Settings.AgentRadius);
        _crowd.Bind(mesh, radius);
    }
}
