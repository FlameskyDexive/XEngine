// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.VirtualGeometry;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering.VirtualGeometry;

/// <summary>One submesh's contiguous slice of a selection's packed index staging buffer.</summary>
public struct VgSubMeshRange
{
    public int Start;
    public int Count;
}

/// <summary>
/// Per-view cluster selection: depth-first traversal from the root clusters, descending
/// while a cluster's projected screen-space error exceeds the renderer's pixel-error
/// threshold. Visible cluster indices are packed per submesh into a caller-owned staging
/// array for a single scratch-index-buffer update. Allocation-free on the steady path.
/// </summary>
public static class VirtualGeometrySelector
{
    /// <param name="data">Baked cluster hierarchy.</param>
    /// <param name="world">Renderer local-to-world matrix.</param>
    /// <param name="maxWorldScale">Largest axis scale of <paramref name="world"/>, for bound radii.</param>
    /// <param name="viewer">Rendering view (camera or shadow cascade).</param>
    /// <param name="pixelError">Screen-space error threshold in pixels.</param>
    /// <param name="staging">Caller-owned index staging array, at least data.Indices.Length.</param>
    /// <param name="visibleScratch">Caller-owned scratch list of visible cluster ids.</param>
    /// <param name="ranges">Caller-owned per-submesh output ranges (sized to subMeshCount).</param>
    /// <param name="subMeshCount">Number of submeshes to emit ranges for.</param>
    /// <returns>Total indices written to staging.</returns>
    public static int Select(
        VirtualGeometryData data,
        in Float4x4 world,
        float maxWorldScale,
        in ViewerData viewer,
        float pixelError,
        uint[] staging,
        List<int> visibleScratch,
        VgSubMeshRange[] ranges,
        int subMeshCount)
    {
        visibleScratch.Clear();
        var clusters = data.Clusters;

        // Screen-space error factor: pixels of deviation per unit error at unit distance.
        // P[1,1] = cot(fovY/2) for perspective (2/height for orthographic — degrades gracefully).
        float proj11 = viewer.ProjectionMatrix[1, 1];
        float sseFactor = viewer.PixelHeight * 0.5f * proj11;
        float threshold = pixelError > 0f ? pixelError : 1f;

        // View basis culling in world space (ViewerData carries the camera axes).
        Float3 camPos = viewer.Position;
        Float3 camFwd = viewer.Forward;
        Float3 camRight = viewer.Right;
        Float3 camUp = viewer.Up;
        float tanHalfY = proj11 != 0f ? 1f / proj11 : 10f;
        float tanHalfX = viewer.ProjectionMatrix[0, 0] != 0f ? 1f / viewer.ProjectionMatrix[0, 0] : tanHalfXFallback(tanHalfY);

        // Roots = clusters of the coarsest level.
        ref readonly var rootLevel = ref data.Levels[data.Levels.Length - 1];
        _stack.Clear();
        for (int i = rootLevel.ClusterStart; i < rootLevel.ClusterStart + rootLevel.ClusterCount; i++)
            _stack.Add(i);

        while (_stack.Count > 0)
        {
            int clusterIndex = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            ref readonly var cluster = ref clusters[clusterIndex];

            Float3 center = Float4x4.TransformPoint(cluster.Bounds.Center, world);
            float radius = Float3.Length(cluster.Bounds.Size) * 0.5f * maxWorldScale;

            // Frustum test in view basis coordinates: reject clusters fully outside any side.
            Float3 rel = center - camPos;
            float z = Float3.Dot(rel, camFwd);
            float x = Float3.Dot(rel, camRight);
            float y = Float3.Dot(rel, camUp);
            float zt = z * tanHalfX + radius;
            float yt = z * tanHalfY + radius;
            if (z < -radius || x < -zt || x > zt || y < -yt || y > yt)
                continue;

            float sse = cluster.Error * sseFactor / Math.Max(z, 0.01f);
            if (sse <= threshold || cluster.ChildGroupsCount == 0)
            {
                visibleScratch.Add(clusterIndex);
                continue;
            }

            // Too coarse — descend into this cluster's child group(s).
            for (int g = 0; g < cluster.ChildGroupsCount; g++)
            {
                ref readonly var group = ref data.Groups[cluster.ChildGroupsStart + g];
                for (int c = group.ClusterStart; c < group.ClusterStart + group.ClusterCount; c++)
                    _stack.Add(c);
            }
        }

        // Pack indices per submesh so each material draws one contiguous range.
        uint[] indices = data.Indices;
        int used = 0;
        for (int s = 0; s < subMeshCount; s++)
            ranges[s] = default;

        for (int s = 0; s < subMeshCount; s++)
        {
            int start = used;
            for (int v = 0; v < visibleScratch.Count; v++)
            {
                ref readonly var cluster = ref clusters[visibleScratch[v]];
                if (cluster.SubMesh != s) continue;
                System.Array.Copy(indices, cluster.IndexStart, staging, used, cluster.IndexCount);
                used += cluster.IndexCount;
            }
            ranges[s] = new VgSubMeshRange { Start = start, Count = used - start };
        }

        return used;
    }

    private static float tanHalfXFallback(float tanHalfY) => tanHalfY * 1.7777f;

    // Traversal stack, reused across calls (selection is never re-entrant: main-thread only).
    private static readonly List<int> _stack = new(256);
}
