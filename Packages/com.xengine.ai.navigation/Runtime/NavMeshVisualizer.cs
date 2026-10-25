// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using DotRecast.Detour;

using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

/// <summary>Submits cached navmesh geometry to the per-frame gizmo pass.</summary>
public static class NavMeshVisualizer
{
    public static void Draw(DtNavMesh? mesh, NavMeshDrawMode mode, int triangleBudget = 20000)
    {
        if (mesh == null) return;
        Draw(NavMeshGeometry.Extract(mesh, triangleBudget), mode);
    }

    public static void Draw(NavMeshTriangulation tri, NavMeshDrawMode mode)
    {
        if (tri.Vertices.Length == 0 && tri.TileBoundaryLines.Length == 0) return;

        switch (mode)
        {
            case NavMeshDrawMode.DetailWire:
                DrawWire(tri);
                break;
            case NavMeshDrawMode.Tiles:
                Debug.DrawLines(tri.TileBoundaryLines, ReadOnlySpan<int>.Empty, new Color(1f, 0.85f, 0.2f, 0.95f));
                break;
            case NavMeshDrawMode.OffMesh:
                Debug.DrawLines(tri.OffMeshLines, ReadOnlySpan<int>.Empty, new Color(1f, 0.3f, 0.9f, 1f));
                break;
            default:
                DrawAreas(tri);
                break;
        }
    }

    public static void DrawPath(ReadOnlySpan<Float3> corners, Color color)
    {
        if (corners.Length < 2) return;
        for (int i = 1; i < corners.Length; i++)
            Debug.DrawLine(corners[i - 1], corners[i], color);
        Debug.DrawSphere(corners[0], 0.15f, color, 8);
        Debug.DrawSphere(corners[corners.Length - 1], 0.15f, color, 8);
    }

    private static void DrawAreas(NavMeshTriangulation tri)
    {
        int triCount = tri.Indices.Length / 3;
        int n = Math.Min(triCount, tri.Areas.Length);
        int runStart = 0;
        while (runStart < n)
        {
            int area = tri.Areas[runStart];
            int runEnd = runStart + 1;
            while (runEnd < n && tri.Areas[runEnd] == area)
                runEnd++;
            Color fill = NavMeshGeometry.AreaColor(area, 0.32f);
            ReadOnlySpan<int> slice = tri.Indices.AsSpan(runStart * 3, (runEnd - runStart) * 3);
            Debug.DrawTriangles(tri.Vertices, slice, fill);
            runStart = runEnd;
        }

        DrawWire(tri, new Color(0.05f, 0.15f, 0.2f, 0.55f));
        if (tri.TileBoundaryLines.Length > 0)
            Debug.DrawLines(tri.TileBoundaryLines, ReadOnlySpan<int>.Empty, new Color(1f, 0.85f, 0.2f, 0.7f));
        if (tri.OffMeshLines.Length > 0)
            Debug.DrawLines(tri.OffMeshLines, ReadOnlySpan<int>.Empty, new Color(1f, 0.35f, 0.9f, 1f));
    }

    private static void DrawWire(NavMeshTriangulation tri, Color? color = null)
    {
        Color c = color ?? new Color(0.15f, 0.7f, 0.9f, 0.85f);
        int triCount = tri.Indices.Length / 3;
        if (triCount <= 0) return;
        var lines = new Float3[triCount * 6];
        int w = 0;
        for (int i = 0; i < triCount; i++)
        {
            int i0 = tri.Indices[i * 3];
            int i1 = tri.Indices[i * 3 + 1];
            int i2 = tri.Indices[i * 3 + 2];
            if ((uint)i0 >= (uint)tri.Vertices.Length || (uint)i1 >= (uint)tri.Vertices.Length || (uint)i2 >= (uint)tri.Vertices.Length)
                continue;
            lines[w++] = tri.Vertices[i0];
            lines[w++] = tri.Vertices[i1];
            lines[w++] = tri.Vertices[i1];
            lines[w++] = tri.Vertices[i2];
            lines[w++] = tri.Vertices[i2];
            lines[w++] = tri.Vertices[i0];
        }

        if (w > 0)
            Debug.DrawLines(lines.AsSpan(0, w), ReadOnlySpan<int>.Empty, c);
    }
}
