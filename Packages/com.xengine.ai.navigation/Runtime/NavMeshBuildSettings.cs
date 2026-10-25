// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using DotRecast.Recast;

namespace XEngine.Runtime.Navigation;

/// <summary>Which scene geometry a bake should rasterize.</summary>
public enum NavMeshCollectGeometry
{
    RenderMeshes = 0,
    PhysicsColliders = 1,
}

/// <summary>Recast bake parameters plus the agent that the resulting mesh is sized for.</summary>
public sealed class NavMeshBuildSettings
{
    public int AgentTypeId = 0;
    public float AgentRadius = 0.5f;
    public float AgentHeight = 2f;
    public float AgentMaxSlope = 45f;
    public float AgentMaxClimb = 0.4f;

    public float CellSize = 0.3f;
    public float CellHeight = 0.2f;
    public int TileSize = 64;
    public RcPartition Partition = RcPartition.WATERSHED;

    public int MinRegionSize = 8;
    public int MergedRegionSize = 20;
    public float EdgeMaxLen = 12f;
    public float EdgeMaxError = 1.3f;
    public int VertsPerPoly = 6;
    public float DetailSampleDist = 6f;
    public float DetailSampleMaxError = 1f;

    public bool FilterLowHangingObstacles = true;
    public bool FilterLedgeSpans = true;
    public bool FilterWalkableLowHeightSpans = true;
    public bool BuildMeshDetail = true;
    public bool EnableCarving = false;

    public NavMeshCollectGeometry CollectGeometry = NavMeshCollectGeometry.RenderMeshes;
    public bool CollectStaticOnly = true;

    public int ComputeHash()
    {
        var h = new HashCode();
        h.Add(AgentTypeId);
        h.Add(AgentRadius);
        h.Add(AgentHeight);
        h.Add(AgentMaxSlope);
        h.Add(AgentMaxClimb);
        h.Add(CellSize);
        h.Add(CellHeight);
        h.Add(TileSize);
        h.Add((int)Partition);
        h.Add(MinRegionSize);
        h.Add(MergedRegionSize);
        h.Add(EdgeMaxLen);
        h.Add(EdgeMaxError);
        h.Add(VertsPerPoly);
        h.Add(DetailSampleDist);
        h.Add(DetailSampleMaxError);
        h.Add(CollectGeometry);
        h.Add(CollectStaticOnly);
        h.Add(EnableCarving);
        return h.ToHashCode();
    }
}
