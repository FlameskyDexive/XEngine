// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using DotRecast.Detour;

using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

/// <summary>CPU triangulation extracted from a Detour navmesh for gizmos, export, and previews.</summary>
public sealed class NavMeshTriangulation
{
    public Float3[] Vertices = [];
    public int[] Indices = [];
    public int[] Areas = [];
    public Float3[] TileBoundaryLines = [];
    public Float3[] OffMeshLines = [];
    public AABB Bounds;
}

public enum NavMeshDrawMode
{
    Areas = 0,
    DetailWire = 1,
    Tiles = 2,
    OffMesh = 3,
}

/// <summary>Extracts detail triangles, tile edges, and off-mesh links from <see cref="DtNavMesh"/>.</summary>
public static class NavMeshGeometry
{
    public static NavMeshTriangulation Extract(DtNavMesh navMesh, int triangleBudget = 20000)
    {
        ArgumentNullException.ThrowIfNull(navMesh);
        var verts = new List<Float3>(256);
        var indices = new List<int>(256);
        var areas = new List<int>(64);
        var tileLines = new List<Float3>(32);
        var offMesh = new List<Float3>(8);
        AABB bounds = default;
        bool hasBounds = false;
        int budget = Math.Max(1, triangleBudget);

        int maxTiles = navMesh.GetMaxTiles();
        for (int t = 0; t < maxTiles; t++)
        {
            DtMeshTile tile = navMesh.GetTile(t);
            DtMeshHeader? header = tile?.data?.header;
            if (header == null || header.polyCount <= 0) continue;

            AddTileBoundary(tile.data, tileLines, ref bounds, ref hasBounds);

            DtMeshData data = tile.data;
            for (int p = 0; p < header.polyCount; p++)
            {
                DtPoly poly = data.polys[p];
                if (poly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION)
                {
                    if (poly.vertCount >= 2)
                    {
                        Float3 a = PolyVertex(data, poly.verts[0]);
                        Float3 b = PolyVertex(data, poly.verts[1]);
                        offMesh.Add(a);
                        offMesh.Add(b);
                        Encapsulate(ref bounds, ref hasBounds, a);
                        Encapsulate(ref bounds, ref hasBounds, b);
                    }
                    continue;
                }

                int area = poly.GetArea();
                int added = AppendPolyTriangles(data, p, poly, verts, indices, budget - areas.Count);
                for (int i = 0; i < added; i++)
                    areas.Add(area);
                if (areas.Count >= budget)
                    break;
            }

            if (data.offMeshCons != null)
            {
                for (int i = 0; i < data.offMeshCons.Length; i++)
                {
                    DtOffMeshConnection con = data.offMeshCons[i];
                    if (con?.pos == null || con.pos.Length < 2) continue;
                    Float3 a = NavMeshConvert.ToEngine(con.pos[0]);
                    Float3 b = NavMeshConvert.ToEngine(con.pos[1]);
                    offMesh.Add(a);
                    offMesh.Add(b);
                }
            }

            if (areas.Count >= budget)
                break;
        }

        return new NavMeshTriangulation
        {
            Vertices = verts.ToArray(),
            Indices = indices.ToArray(),
            Areas = areas.ToArray(),
            TileBoundaryLines = tileLines.ToArray(),
            OffMeshLines = offMesh.ToArray(),
            Bounds = hasBounds ? bounds : default,
        };
    }

    public static Color AreaColor(int area, float alpha = 0.35f)
    {
        return area switch
        {
            NavMeshAreas.Water => new Color(0.15f, 0.35f, 0.95f, alpha),
            NavMeshAreas.Road => new Color(0.55f, 0.55f, 0.5f, alpha),
            NavMeshAreas.Jump => new Color(0.95f, 0.2f, 0.85f, alpha),
            NavMeshAreas.Walkable => new Color(0.2f, 0.85f, 0.95f, alpha),
            _ => new Color(0.25f, 0.8f, 0.55f, alpha),
        };
    }

    private static int AppendPolyTriangles(DtMeshData data, int polyIndex, DtPoly poly, List<Float3> verts, List<int> indices, int remaining)
    {
        if (remaining <= 0) return 0;
        if (data.detailMeshes != null && polyIndex < data.detailMeshes.Length && data.detailTris != null)
        {
            DtPolyDetail pd = data.detailMeshes[polyIndex];
            int count = Math.Min((int)pd.triCount, remaining);
            for (int i = 0; i < count; i++)
            {
                int baseTri = (pd.triBase + i) * 4;
                Float3 a = DetailVertex(data, poly, pd, data.detailTris[baseTri]);
                Float3 b = DetailVertex(data, poly, pd, data.detailTris[baseTri + 1]);
                Float3 c = DetailVertex(data, poly, pd, data.detailTris[baseTri + 2]);
                int vi = verts.Count;
                verts.Add(a);
                verts.Add(b);
                verts.Add(c);
                indices.Add(vi);
                indices.Add(vi + 1);
                indices.Add(vi + 2);
            }
            return count;
        }

        if (poly.vertCount < 3) return 0;
        int fan = Math.Min(poly.vertCount - 2, remaining);
        Float3 v0 = PolyVertex(data, poly.verts[0]);
        for (int i = 0; i < fan; i++)
        {
            Float3 v1 = PolyVertex(data, poly.verts[i + 1]);
            Float3 v2 = PolyVertex(data, poly.verts[i + 2]);
            int vi = verts.Count;
            verts.Add(v0);
            verts.Add(v1);
            verts.Add(v2);
            indices.Add(vi);
            indices.Add(vi + 1);
            indices.Add(vi + 2);
        }
        return fan;
    }

    private static Float3 DetailVertex(DtMeshData data, DtPoly poly, DtPolyDetail pd, int index)
    {
        if (index < poly.vertCount)
            return PolyVertex(data, poly.verts[index]);
        int di = (pd.vertBase + index - poly.vertCount) * 3;
        return new Float3(data.detailVerts[di], data.detailVerts[di + 1], data.detailVerts[di + 2]);
    }

    private static Float3 PolyVertex(DtMeshData data, int vertIndex)
    {
        int i = vertIndex * 3;
        return new Float3(data.verts[i], data.verts[i + 1], data.verts[i + 2]);
    }

    private static void AddTileBoundary(DtMeshData data, List<Float3> lines, ref AABB bounds, ref bool hasBounds)
    {
        DtMeshHeader header = data.header;
        Float3 min = NavMeshConvert.ToEngine(header.bmin);
        Float3 max = NavMeshConvert.ToEngine(header.bmax);
        Encapsulate(ref bounds, ref hasBounds, min);
        Encapsulate(ref bounds, ref hasBounds, max);
        float y = min.Y + 0.05f;
        Float3 a = new(min.X, y, min.Z);
        Float3 b = new(max.X, y, min.Z);
        Float3 c = new(max.X, y, max.Z);
        Float3 d = new(min.X, y, max.Z);
        lines.Add(a); lines.Add(b);
        lines.Add(b); lines.Add(c);
        lines.Add(c); lines.Add(d);
        lines.Add(d); lines.Add(a);
    }

    private static void Encapsulate(ref AABB bounds, ref bool hasBounds, Float3 p)
    {
        if (!hasBounds)
        {
            bounds = new AABB(p, p);
            hasBounds = true;
            return;
        }

        bounds.Encapsulate(p);
    }
}
