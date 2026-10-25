// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

using Xunit;

namespace XEngine.Runtime.Test.Navigation;

/// <summary>
/// M0 vendor gate: bake a Recast navmesh from an in-memory triangle plane and query a path.
/// Uses DotRecast APIs directly (no XEngine wrapper yet).
/// </summary>
public sealed class DotRecastSmokeTests
{
    private const int WalkableArea = 63;
    private const int WalkFlag = 1;
    private const int VertsPerPoly = 6;

    [Fact]
    public void BakePlane_ThenFindPath_Succeeds()
    {
        RcSampleInputGeomProvider geom = CreatePlane(size: 20f);
        RcBuilderResult recast = Bake(geom);
        Assert.NotNull(recast.Mesh);
        Assert.True(recast.Mesh.npolys > 0, "expected at least one polygon on a 20x20 plane");

        DtNavMesh navMesh = ToNavMesh(geom, recast);
        var query = new DtNavMeshQuery(navMesh);
        var filter = new DtQueryDefaultFilter();
        var extents = new RcVec3f(2f, 4f, 2f);

        DtStatus startStatus = query.FindNearestPoly(new RcVec3f(2f, 0f, 2f), extents, filter,
            out long startRef, out RcVec3f startPt, out _);
        DtStatus endStatus = query.FindNearestPoly(new RcVec3f(18f, 0f, 18f), extents, filter,
            out long endRef, out RcVec3f endPt, out _);

        Assert.True(startStatus.Succeeded(), $"FindNearestPoly start failed: {startStatus}");
        Assert.True(endStatus.Succeeded(), $"FindNearestPoly end failed: {endStatus}");
        Assert.True(startRef != 0, "start poly ref should be valid");
        Assert.True(endRef != 0, "end poly ref should be valid");

        Span<long> corridor = stackalloc long[64];
        DtStatus pathStatus = query.FindPath(startRef, endRef, startPt, endPt, filter, corridor, out int pathCount, 64);
        Assert.True(pathStatus.Succeeded(), $"FindPath failed: {pathStatus}");
        Assert.True(pathCount >= 1, "path should contain at least one poly");

        Span<DtStraightPath> straight = stackalloc DtStraightPath[16];
        DtStatus straightStatus = query.FindStraightPath(startPt, endPt, corridor, pathCount, straight, out int straightCount, 16, 0);
        Assert.True(straightStatus.Succeeded(), $"FindStraightPath failed: {straightStatus}");
        Assert.True(straightCount >= 2, $"straight path should have start and end, got {straightCount}");

        float travel = 0f;
        for (int i = 1; i < straightCount; i++)
        {
            travel += RcVec3f.Distance(straight[i - 1].pos, straight[i].pos);
        }

        Assert.True(travel > 10f, $"path length {travel} is too short for a 2,2 → 18,18 query");
        Assert.InRange(straight[straightCount - 1].pos.X, 16f, 20f);
        Assert.InRange(straight[straightCount - 1].pos.Z, 16f, 20f);
    }

    private static RcSampleInputGeomProvider CreatePlane(float size)
    {
        // Recast-native winding: CCW when viewed from +Y so the face normal is +Y.
        float[] verts =
        [
            0f, 0f, 0f,
            size, 0f, 0f,
            size, 0f, size,
            0f, 0f, size,
        ];
        int[] faces =
        [
            0, 3, 1,
            1, 3, 2,
        ];
        return new RcSampleInputGeomProvider(verts, faces);
    }

    private static RcBuilderResult Bake(IRcInputGeomProvider geom)
    {
        var walkable = new RcAreaModification(WalkableArea);
        var cfg = new RcConfig(
            RcPartition.WATERSHED,
            cellSize: 0.3f, cellHeight: 0.2f,
            agentMaxSlope: 45f, agentHeight: 2f, agentRadius: 0.6f, agentMaxClimb: 0.9f,
            regionMinSize: 8, regionMergeSize: 20,
            edgeMaxLen: 12f, edgeMaxError: 1.3f,
            vertsPerPoly: VertsPerPoly,
            detailSampleDist: 6f, detailSampleMaxError: 1f,
            filterLowHangingObstacles: true, filterLedgeSpans: true, filterWalkableLowHeightSpans: true,
            walkable, buildMeshDetail: true);
        var bcfg = new RcBuilderConfig(cfg, geom.GetMeshBoundsMin(), geom.GetMeshBoundsMax());
        return new RcBuilder().Build(geom, bcfg, keepInterResults: false);
    }

    private static DtNavMesh ToNavMesh(IRcInputGeomProvider geom, RcBuilderResult recast)
    {
        RcPolyMesh pmesh = recast.Mesh;
        for (int i = 0; i < pmesh.npolys; i++)
            pmesh.flags[i] = WalkFlag;

        var option = new DtNavMeshCreateParams
        {
            verts = pmesh.verts,
            vertCount = pmesh.nverts,
            polys = pmesh.polys,
            polyAreas = pmesh.areas,
            polyFlags = pmesh.flags,
            polyCount = pmesh.npolys,
            nvp = pmesh.nvp,
            walkableHeight = 2f,
            walkableRadius = 0.6f,
            walkableClimb = 0.9f,
            bmin = pmesh.bmin,
            bmax = pmesh.bmax,
            cs = 0.3f,
            ch = 0.2f,
            buildBvTree = true,
        };

        RcPolyMeshDetail? dmesh = recast.MeshDetail;
        if (dmesh != null)
        {
            option.detailMeshes = dmesh.meshes;
            option.detailVerts = dmesh.verts;
            option.detailVertsCount = dmesh.nverts;
            option.detailTris = dmesh.tris;
            option.detailTriCount = dmesh.ntris;
        }

        DtMeshData meshData = DtNavMeshBuilder.CreateNavMeshData(option);
        Assert.NotNull(meshData);

        var navMesh = new DtNavMesh();
        DtStatus status = navMesh.Init(meshData, VertsPerPoly, 0);
        Assert.True(status.Succeeded(), $"DtNavMesh.Init failed: {status}");
        return navMesh;
    }
}
