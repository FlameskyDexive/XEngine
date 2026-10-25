// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.TileCache;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

/// <summary>
/// Collects scene geometry and runs the Recast build pipeline into a <see cref="NavMeshData"/> asset.
/// Heavy Recast work is CPU-bound; callers that must stay off the frame loop should hop with XTask.
/// </summary>
public static class NavMeshBuilder
{
    public static NavMeshInputGeometry Collect(Scene scene, NavMeshBuildSettings settings, LayerMask layers, Func<GameObject, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(settings);

        var geom = new NavMeshInputGeometry();
        foreach (GameObject go in scene.AllObjects)
        {
            if (!go.EnabledInHierarchy) continue;
            if (settings.CollectStaticOnly && !go.IsStatic) continue;
            if (!layers.HasLayer(go.LayerIndex)) continue;
            if (include != null && !include(go)) continue;

            if (NavMeshGeometryCollectors.TryCollect(go, geom))
                continue;

            if (settings.CollectGeometry == NavMeshCollectGeometry.PhysicsColliders)
            {
                MeshCollider? collider = go.GetComponent<MeshCollider>();
                Mesh? colliderMesh = collider?.Mesh.Res;
                if (colliderMesh != null)
                    geom.AddMesh(colliderMesh.Vertices, colliderMesh.Indices, go.Transform.LocalToWorldMatrix);
                continue;
            }

            MeshRenderer? renderer = go.GetComponent<MeshRenderer>();
            Mesh? mesh = renderer?.Mesh.Res;
            if (mesh != null)
                geom.AddMesh(mesh.Vertices, mesh.Indices, go.Transform.LocalToWorldMatrix);
        }

        CollectOffMeshLinks(scene, geom);
        return geom;
    }

    public static NavMeshData Build(Scene scene, NavMeshBuildSettings? settings = null, LayerMask? layers = null, Func<GameObject, bool>? include = null)
    {
        settings ??= new NavMeshBuildSettings();
        NavMeshInputGeometry geom = Collect(scene, settings, layers ?? LayerMask.Everything, include);
        return Build(geom, settings);
    }

    public static NavMeshData Build(Float3[] vertices, uint[] indices, NavMeshBuildSettings? settings = null)
    {
        settings ??= new NavMeshBuildSettings();
        var geom = new NavMeshInputGeometry();
        geom.AddMesh(vertices, indices, Float4x4.Identity);
        return Build(geom, settings);
    }

    public static NavMeshData Build(NavMeshInputGeometry geom, NavMeshBuildSettings settings)
    {
        ArgumentNullException.ThrowIfNull(geom);
        ArgumentNullException.ThrowIfNull(settings);
        if (geom.TriangleCount <= 0)
            throw new InvalidOperationException("NavMesh bake collected no triangles.");

        bool carve = settings.EnableCarving && settings.TileSize > 0;
        RcConfig cfg = CreateConfig(settings);
        if (carve)
        {
            DtTileCache cache = NavMeshTileCache.Build(geom, settings, cfg);
            NavMeshData carved = NavMeshData.FromRuntime(cache.GetNavMesh(), settings, geom);
            carved.AttachTileCache(cache, NavMeshTileCache.Write(cache));
            return carved;
        }

        var recast = new RcBuilder();
        IList<RcBuilderResult> results;
        if (settings.TileSize > 0)
        {
            results = recast.BuildTiles(geom, cfg, keepInterResults: false, buildAll: true,
                Environment.ProcessorCount + 1, Task.Factory);
        }
        else
        {
            var bcfg = new RcBuilderConfig(cfg, geom.GetMeshBoundsMin(), geom.GetMeshBoundsMax());
            results = [recast.Build(geom, bcfg, keepInterResults: false)];
        }

        DtNavMesh navMesh = AssembleNavMesh(geom, settings, results);
        return NavMeshData.FromRuntime(navMesh, settings, geom);
    }

    public static NavMeshData Update(Scene scene, NavMeshData existing, AABB dirtyBounds, LayerMask? layers = null, Func<GameObject, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        NavMeshInputGeometry geom = Collect(scene, existing.Settings, layers ?? LayerMask.Everything, include);
        return Update(geom, existing, dirtyBounds);
    }

    public static NavMeshData Update(NavMeshInputGeometry geom, NavMeshData existing, AABB dirtyBounds)
    {
        ArgumentNullException.ThrowIfNull(geom);
        ArgumentNullException.ThrowIfNull(existing);
        if (existing.Settings.EnableCarving && existing.RuntimeTileCache != null)
        {
            RcConfig cacheCfg = CreateConfig(existing.Settings);
            DtTileCache cache = NavMeshTileCache.Build(geom, existing.Settings, cacheCfg);
            existing.AttachTileCache(cache, NavMeshTileCache.Write(cache));
            existing.RefreshFromRuntime();
            NavMeshWorld.Instance.RefreshQuery();
            return existing;
        }

        DtNavMesh mesh = existing.RuntimeMesh;
        ref readonly DtNavMeshParams navParams = ref mesh.GetParams();
        float tileW = MathF.Max(1e-3f, navParams.tileWidth);
        float tileH = MathF.Max(1e-3f, navParams.tileHeight);
        RcVec3f orig = navParams.orig;
        float pad = existing.Settings.AgentRadius + existing.Settings.CellSize;
        int minX = (int)MathF.Floor((dirtyBounds.Min.X - pad - orig.X) / tileW);
        int maxX = (int)MathF.Floor((dirtyBounds.Max.X + pad - orig.X) / tileW);
        int minZ = (int)MathF.Floor((dirtyBounds.Min.Z - pad - orig.Z) / tileH);
        int maxZ = (int)MathF.Floor((dirtyBounds.Max.Z + pad - orig.Z) / tileH);

        RcConfig cfg = CreateConfig(existing.Settings);
        RcVec3f bmin = geom.GetMeshBoundsMin();
        RcVec3f bmax = geom.GetMeshBoundsMax();
        var recast = new RcBuilder();
        var progress = new RcAtomicInteger(0);
        for (int tz = minZ; tz <= maxZ; tz++)
        {
            for (int tx = minX; tx <= maxX; tx++)
            {
                if (tx < 0 || tz < 0) continue;
                RcBuilderResult result = recast.BuildTile(geom, cfg, bmin, bmax, tx, tz, progress, 1, false);
                DtMeshData? md = result.Mesh != null && result.Mesh.npolys > 0
                    ? CreateTile(geom, existing.Settings, result)
                    : null;
                for (int layer = 0; layer < 8; layer++)
                {
                    long tileRef = mesh.GetTileRefAt(tx, tz, layer);
                    if (tileRef != 0)
                        mesh.RemoveTile(tileRef);
                }

                if (md != null)
                    mesh.AddTile(md, 0, 0, out _);
            }
        }

        existing.RefreshFromRuntime();
        NavMeshWorld.Instance.RefreshQuery();
        return existing;
    }

    internal static RcConfig CreateConfig(NavMeshBuildSettings settings)
    {
        var walkable = new RcAreaModification(NavMeshAreas.Walkable);
        bool useTiles = settings.TileSize > 0;
        int border = useTiles ? RcConfig.CalcBorder(settings.AgentRadius, settings.CellSize) : 0;
        return useTiles
            ? new RcConfig(
                true, settings.TileSize, settings.TileSize, border,
                settings.Partition,
                settings.CellSize, settings.CellHeight,
                settings.AgentMaxSlope, settings.AgentHeight, settings.AgentRadius, settings.AgentMaxClimb,
                settings.MinRegionSize * settings.MinRegionSize * settings.CellSize * settings.CellSize,
                settings.MergedRegionSize * settings.MergedRegionSize * settings.CellSize * settings.CellSize,
                settings.EdgeMaxLen, settings.EdgeMaxError,
                settings.VertsPerPoly,
                settings.DetailSampleDist, settings.DetailSampleMaxError,
                settings.FilterLowHangingObstacles, settings.FilterLedgeSpans, settings.FilterWalkableLowHeightSpans,
                walkable, settings.BuildMeshDetail)
            : new RcConfig(
                settings.Partition,
                settings.CellSize, settings.CellHeight,
                settings.AgentMaxSlope, settings.AgentHeight, settings.AgentRadius, settings.AgentMaxClimb,
                settings.MinRegionSize, settings.MergedRegionSize,
                settings.EdgeMaxLen, settings.EdgeMaxError,
                settings.VertsPerPoly,
                settings.DetailSampleDist, settings.DetailSampleMaxError,
                settings.FilterLowHangingObstacles, settings.FilterLedgeSpans, settings.FilterWalkableLowHeightSpans,
                walkable, settings.BuildMeshDetail);
    }

    internal static DtNavMesh AssembleNavMesh(IRcInputGeomProvider geom, NavMeshBuildSettings settings, IList<RcBuilderResult> results)
    {
        var meshData = new List<DtMeshData>(results.Count);
        for (int i = 0; i < results.Count; i++)
        {
            RcBuilderResult result = results[i];
            if (result.Mesh == null || result.Mesh.npolys <= 0) continue;
            DtMeshData? md = CreateTile(geom, settings, result);
            if (md != null) meshData.Add(md);
        }

        if (meshData.Count == 0)
            throw new InvalidOperationException("Recast produced no walkable polygons.");

        if (settings.TileSize <= 0 && meshData.Count == 1)
        {
            var solo = new DtNavMesh();
            DtStatus status = solo.Init(meshData[0], settings.VertsPerPoly, 0);
            if (status.Failed())
                throw new InvalidOperationException($"DtNavMesh.Init failed: {status}");
            return solo;
        }

        RcVec3f bmin = geom.GetMeshBoundsMin();
        RcVec3f bmax = geom.GetMeshBoundsMax();
        RcRecast.CalcGridSize(bmin, bmax, settings.CellSize, out int gw, out int gh);
        int tileSize = Math.Max(settings.TileSize, 1);
        int tw = (gw + tileSize - 1) / tileSize;
        int th = (gh + tileSize - 1) / tileSize;
        int tileBits = Math.Min(DtUtils.Ilog2(DtUtils.NextPow2(Math.Max(1, tw * th))), 14);
        int polyBits = 22 - tileBits;

        var navParams = new DtNavMeshParams
        {
            orig = bmin,
            tileWidth = tileSize * settings.CellSize,
            tileHeight = tileSize * settings.CellSize,
            maxTiles = 1 << tileBits,
            maxPolys = 1 << polyBits,
        };
        var tiled = new DtNavMesh();
        tiled.Init(navParams, settings.VertsPerPoly);
        for (int i = 0; i < meshData.Count; i++)
            tiled.AddTile(meshData[i], 0, 0, out _);
        return tiled;
    }

    private static DtMeshData? CreateTile(IRcInputGeomProvider geom, NavMeshBuildSettings settings, RcBuilderResult result)
    {
        RcPolyMesh pmesh = result.Mesh;
        for (int i = 0; i < pmesh.npolys; i++)
            pmesh.flags[i] = NavMeshAreas.FlagWalk;

        var option = new DtNavMeshCreateParams
        {
            verts = pmesh.verts,
            vertCount = pmesh.nverts,
            polys = pmesh.polys,
            polyAreas = pmesh.areas,
            polyFlags = pmesh.flags,
            polyCount = pmesh.npolys,
            nvp = pmesh.nvp,
            walkableHeight = settings.AgentHeight,
            walkableRadius = settings.AgentRadius,
            walkableClimb = settings.AgentMaxClimb,
            bmin = pmesh.bmin,
            bmax = pmesh.bmax,
            cs = settings.CellSize,
            ch = settings.CellHeight,
            buildBvTree = true,
            tileX = result.TileX,
            tileZ = result.TileZ,
        };

        RcPolyMeshDetail? dmesh = result.MeshDetail;
        if (dmesh != null)
        {
            option.detailMeshes = dmesh.meshes;
            option.detailVerts = dmesh.verts;
            option.detailVertsCount = dmesh.nverts;
            option.detailTris = dmesh.tris;
            option.detailTriCount = dmesh.ntris;
        }

        List<RcOffMeshConnection> links = geom.GetOffMeshConnections();
        option.offMeshConCount = links.Count;
        if (links.Count > 0)
        {
            option.offMeshConVerts = new float[links.Count * 6];
            option.offMeshConRad = new float[links.Count];
            option.offMeshConDir = new int[links.Count];
            option.offMeshConAreas = new int[links.Count];
            option.offMeshConFlags = new int[links.Count];
            option.offMeshConUserID = new int[links.Count];
            for (int i = 0; i < links.Count; i++)
            {
                RcOffMeshConnection link = links[i];
                for (int j = 0; j < 6; j++)
                    option.offMeshConVerts[6 * i + j] = link.verts[j];
                option.offMeshConRad[i] = link.radius;
                option.offMeshConDir[i] = link.bidir ? 1 : 0;
                option.offMeshConAreas[i] = link.area;
                option.offMeshConFlags[i] = link.flags;
            }
        }

        return DtNavMeshBuilder.CreateNavMeshData(option);
    }

    private static void CollectOffMeshLinks(Scene scene, NavMeshInputGeometry geom)
    {
        foreach (GameObject go in scene.AllObjects)
        {
            if (!go.EnabledInHierarchy) continue;
            OffMeshLink? link = go.GetComponent<OffMeshLink>();
            if (link == null || !link.Activated) continue;
            geom.AddOffMeshConnection(
                NavMeshConvert.ToRecast(link.StartWorld),
                NavMeshConvert.ToRecast(link.EndWorld),
                MathF.Max(0.05f, link.Radius),
                link.Bidirectional,
                link.Area,
                NavMeshAreas.FlagWalk | NavMeshAreas.FlagJump);
        }
    }
}
