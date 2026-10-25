// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Terrain;

/// <summary>Which heightmap edge of a terrain tile.</summary>
public enum TerrainEdge
{
    Left,
    Right,
    Bottom,
    Top,
}

/// <summary>
/// Grouping, AABB edge matching, heightmap seam copy, and UV mapping for tiled terrains.
/// Unity-style: same <see cref="TerrainComponent.GroupingId"/> tiles that share an XZ edge become neighbors.
/// </summary>
public static class TerrainNeighboring
{
    /// <summary>World-space XZ AABB of a size×size tile under <paramref name="localToWorld"/>.</summary>
    public static void WorldAabb(Float4x4 localToWorld, float size, out Float3 min, out Float3 max)
    {
        Float3 c0 = Float4x4.TransformPoint(Float3.Zero, localToWorld);
        Float3 c1 = Float4x4.TransformPoint(new Float3(size, 0f, 0f), localToWorld);
        Float3 c2 = Float4x4.TransformPoint(new Float3(0f, 0f, size), localToWorld);
        Float3 c3 = Float4x4.TransformPoint(new Float3(size, 0f, size), localToWorld);
        min = new Float3(
            MathF.Min(MathF.Min(c0.X, c1.X), MathF.Min(c2.X, c3.X)),
            MathF.Min(MathF.Min(c0.Y, c1.Y), MathF.Min(c2.Y, c3.Y)),
            MathF.Min(MathF.Min(c0.Z, c1.Z), MathF.Min(c2.Z, c3.Z)));
        max = new Float3(
            MathF.Max(MathF.Max(c0.X, c1.X), MathF.Max(c2.X, c3.X)),
            MathF.Max(MathF.Max(c0.Y, c1.Y), MathF.Max(c2.Y, c3.Y)),
            MathF.Max(MathF.Max(c0.Z, c1.Z), MathF.Max(c2.Z, c3.Z)));
    }

    /// <summary>
    /// If B shares an XZ edge with A, <paramref name="edgeOnA"/> is the side of A that B occupies.
    /// </summary>
    public static bool TryMatchEdge(Float3 aMin, Float3 aMax, Float3 bMin, Float3 bMax, float epsilon, out TerrainEdge edgeOnA)
    {
        edgeOnA = TerrainEdge.Left;
        float eps = MathF.Max(epsilon, 1e-3f);
        bool zOverlap = aMin.Z < bMax.Z - eps && aMax.Z > bMin.Z + eps;
        bool xOverlap = aMin.X < bMax.X - eps && aMax.X > bMin.X + eps;

        if (zOverlap && MathF.Abs(bMax.X - aMin.X) <= eps)
        {
            edgeOnA = TerrainEdge.Left;
            return true;
        }

        if (zOverlap && MathF.Abs(bMin.X - aMax.X) <= eps)
        {
            edgeOnA = TerrainEdge.Right;
            return true;
        }

        if (xOverlap && MathF.Abs(bMax.Z - aMin.Z) <= eps)
        {
            edgeOnA = TerrainEdge.Bottom;
            return true;
        }

        if (xOverlap && MathF.Abs(bMin.Z - aMax.Z) <= eps)
        {
            edgeOnA = TerrainEdge.Top;
            return true;
        }

        return false;
    }

    public static TerrainEdge Opposite(TerrainEdge edge) => edge switch
    {
        TerrainEdge.Left => TerrainEdge.Right,
        TerrainEdge.Right => TerrainEdge.Left,
        TerrainEdge.Bottom => TerrainEdge.Top,
        _ => TerrainEdge.Bottom,
    };

    /// <summary>Copy one heightmap edge onto another, interpolating if resolutions differ.</summary>
    public static void CopySharedEdge(TerrainData from, TerrainEdge fromEdge, TerrainData to, TerrainEdge toEdge)
    {
        if (from?.Heights == null || to?.Heights == null)
            return;
        int fromRes = from.HeightmapResolution;
        int toRes = to.HeightmapResolution;
        if (fromRes < 2 || toRes < 2)
            return;

        for (int i = 0; i < toRes; i++)
        {
            float t = i / (float)(toRes - 1);
            int fromI = (int)MathF.Round(t * (fromRes - 1));
            to.SetHeight(EdgeX(toEdge, toRes, i), EdgeZ(toEdge, toRes, i),
                from.GetHeight(EdgeX(fromEdge, fromRes, fromI), EdgeZ(fromEdge, fromRes, fromI)));
        }
    }

    public static Float2 WorldToUv(Float3 world, Float4x4 worldToLocal, float size)
    {
        Float3 local = Float4x4.TransformPoint(world, worldToLocal);
        float s = MathF.Max(size, 1e-3f);
        return new Float2(local.X / s, local.Z / s);
    }

    public static Float3 UvToWorld(Float2 uv, Float4x4 localToWorld, float size)
        => Float4x4.TransformPoint(new Float3(uv.X * size, 0f, uv.Y * size), localToWorld);

    public static bool CircleOverlapsAabb(Float3 center, float radius, Float3 min, Float3 max)
    {
        float cx = Math.Clamp(center.X, min.X, max.X);
        float cz = Math.Clamp(center.Z, min.Z, max.Z);
        float dx = center.X - cx;
        float dz = center.Z - cz;
        return dx * dx + dz * dz <= radius * radius;
    }

    /// <summary>Rebuild neighbor pointers for enabled, auto-connecting tiles that share a grouping id.</summary>
    public static void Reconnect(IReadOnlyList<TerrainComponent> terrains)
    {
        for (int i = 0; i < terrains.Count; i++)
        {
            TerrainComponent? t = terrains[i];
            if (t == null || t.IsDisposed)
                continue;
            t.ClearNeighbors();
        }

        for (int i = 0; i < terrains.Count; i++)
        {
            TerrainComponent a = terrains[i];
            if (a == null || a.IsDisposed || !a.AllowAutoConnect)
                continue;
            a.GetWorldAabb(out Float3 aMin, out Float3 aMax);
            float eps = MathF.Max(0.05f, a.TerrainSize * 0.002f);

            for (int j = i + 1; j < terrains.Count; j++)
            {
                TerrainComponent b = terrains[j];
                if (b == null || b.IsDisposed || !b.AllowAutoConnect)
                    continue;
                if (a.GroupingId != b.GroupingId)
                    continue;
                b.GetWorldAabb(out Float3 bMin, out Float3 bMax);
                if (!TryMatchEdge(aMin, aMax, bMin, bMax, eps, out TerrainEdge edgeOnA))
                    continue;
                a.AssignNeighbor(edgeOnA, b);
                b.AssignNeighbor(Opposite(edgeOnA), a);
            }
        }
    }

    public static void PushSeams(TerrainComponent terrain)
    {
        var data = terrain.Data.Res;
        if (data == null)
            return;
        PushOne(terrain.LeftNeighbor, TerrainEdge.Left, TerrainEdge.Right, data);
        PushOne(terrain.RightNeighbor, TerrainEdge.Right, TerrainEdge.Left, data);
        PushOne(terrain.BottomNeighbor, TerrainEdge.Bottom, TerrainEdge.Top, data);
        PushOne(terrain.TopNeighbor, TerrainEdge.Top, TerrainEdge.Bottom, data);
    }

    private static void PushOne(TerrainComponent? neighbor, TerrainEdge fromEdge, TerrainEdge toEdge, TerrainData from)
    {
        var to = neighbor?.Data.Res;
        if (to == null)
            return;
        CopySharedEdge(from, fromEdge, to, toEdge);
        neighbor!.InvalidateGrassCache();
    }

    private static int EdgeX(TerrainEdge edge, int res, int i) => edge switch
    {
        TerrainEdge.Left => 0,
        TerrainEdge.Right => res - 1,
        _ => i,
    };

    private static int EdgeZ(TerrainEdge edge, int res, int i) => edge switch
    {
        TerrainEdge.Bottom => 0,
        TerrainEdge.Top => res - 1,
        _ => i,
    };
}
