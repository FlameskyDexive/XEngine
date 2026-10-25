// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.Runtime.VirtualGeometry.Generation;

/// <summary>One cluster produced by <see cref="MeshClusterizer"/>: its triangles (edge-connected
/// where the source is connected) plus the unique input vertices they reference.</summary>
public struct RawCluster
{
    /// <summary>Cluster triangles in fill order, 3 pool vertex ids per triangle.</summary>
    public uint[] Triangles;
    public uint[] VertexIds;
    public AABB Bounds;
}

/// <summary>
/// Partitions a triangle set into spatially coherent, edge-connected clusters of at most
/// <c>maxTriangles</c> triangles each. Seeds follow Morton order of triangle centroids for
/// global locality, but clusters grow by edge adjacency (flood fill), because raw Morton
/// slicing is discontinuous at Z-order quadrant boundaries and would produce disconnected
/// patches — fatal for the group simplifier, which locks every disconnected border.
/// Import-time tooling — allocation discipline is not required here.
/// </summary>
public static class MeshClusterizer
{
    /// <param name="positions">Input vertex pool (may contain unused vertices).</param>
    /// <param name="triangles">Triangle list, 3 vertex ids per triangle.</param>
    /// <param name="maxTriangles">Maximum triangles per cluster (128 typical).</param>
    public static List<RawCluster> Clusterize(ReadOnlySpan<Float3> positions, ReadOnlySpan<uint> triangles, int maxTriangles = 128)
    {
        if (triangles.Length % 3 != 0)
            throw new ArgumentException("Triangle count must be a multiple of 3.", nameof(triangles));
        if (maxTriangles < 3)
            throw new ArgumentOutOfRangeException(nameof(maxTriangles));

        int triangleCount = triangles.Length / 3;
        var result = new List<RawCluster>(triangleCount / maxTriangles + 1);
        if (triangleCount == 0)
            return result;

        // Morton keys drive the seed order so consecutive clusters stay spatial neighbors.
        ComputeMortonBounds(positions, triangles, out Float3 mortonMin, out Float3 mortonScale);
        var seedOrder = new int[triangleCount];
        var keys = new uint[triangleCount];
        for (int t = 0; t < triangleCount; t++)
        {
            Float3 a = positions[(int)triangles[t * 3]];
            Float3 b = positions[(int)triangles[t * 3 + 1]];
            Float3 c = positions[(int)triangles[t * 3 + 2]];
            Float3 centroid = new((a.X + b.X + c.X) * (1f / 3f), (a.Y + b.Y + c.Y) * (1f / 3f), (a.Z + b.Z + c.Z) * (1f / 3f));
            keys[t] = MortonCode(centroid, mortonMin, mortonScale);
            seedOrder[t] = t;
        }
        Array.Sort(keys, seedOrder);

        // Edge -> incident triangles adjacency.
        var edgeTris = new Dictionary<long, List<int>>();
        for (int t = 0; t < triangleCount; t++)
        {
            if (triangles[t * 3] == triangles[t * 3 + 1] || triangles[t * 3 + 1] == triangles[t * 3 + 2]
                || triangles[t * 3] == triangles[t * 3 + 2])
                continue; // degenerate; it still forms its own "cluster" content below

            AddEdgeRef(edgeTris, triangles[t * 3], triangles[t * 3 + 1], t);
            AddEdgeRef(edgeTris, triangles[t * 3 + 1], triangles[t * 3 + 2], t);
            AddEdgeRef(edgeTris, triangles[t * 3 + 2], triangles[t * 3], t);
        }

        // Flood-fill growth from Morton-ordered seeds. The frontier carries over to the
        // next cluster when one fills up, so consecutive clusters share borders (keeping
        // groups connected for the simplifier) and clusters stay near fully packed.
        var assigned = new bool[triangleCount];
        var clusterTriangles = new List<int>(maxTriangles);
        var frontier = new Queue<int>(maxTriangles * 3);
        var seen = new HashSet<uint>(maxTriangles * 3);
        List<uint> scratchVertexIds = new(maxTriangles * 3);

        int nextSeed = 0;
        while (true)
        {
            // Refill the frontier from the Morton seed order when the previous cluster
            // consumed it entirely; leftover frontier carries over between clusters.
            while (frontier.Count == 0)
            {
                if (nextSeed >= triangleCount)
                    return result; // every triangle clustered
                int seed = seedOrder[nextSeed++];
                if (!assigned[seed])
                {
                    assigned[seed] = true;
                    frontier.Enqueue(seed);
                }
            }

            clusterTriangles.Clear();
            while (frontier.Count > 0 && clusterTriangles.Count < maxTriangles)
            {
                int t = frontier.Dequeue();
                clusterTriangles.Add(t);

                // Queue edge-neighbors; breadth-first growth keeps clusters compact.
                for (int e = 0; e < 3; e++)
                {
                    uint a = triangles[t * 3 + e], b = triangles[t * 3 + (e + 1) % 3];
                    if (!edgeTris.TryGetValue(EdgeKey(a, b), out var adj))
                        continue;
                    for (int i = 0; i < adj.Count; i++)
                    {
                        int n = adj[i];
                        if (!assigned[n])
                        {
                            assigned[n] = true;
                            frontier.Enqueue(n);
                        }
                    }
                }
            }

            // Emit the cluster: unique vertices in first-use order plus its triangles.
            seen.Clear();
            scratchVertexIds.Clear();
            Float3 min = new(float.MaxValue), max = new(float.MinValue);
            var outTriangles = new uint[clusterTriangles.Count * 3];
            for (int i = 0; i < clusterTriangles.Count; i++)
            {
                int t = clusterTriangles[i];
                for (int k = 0; k < 3; k++)
                {
                    uint id = triangles[t * 3 + k];
                    outTriangles[i * 3 + k] = id;
                    if (seen.Add(id))
                        scratchVertexIds.Add(id);
                    Float3 p = positions[(int)id];
                    min = Maths.Min(min, p);
                    max = Maths.Max(max, p);
                }
            }

            result.Add(new RawCluster
            {
                Triangles = outTriangles,
                VertexIds = scratchVertexIds.ToArray(),
                Bounds = new AABB(min, max),
            });
        }
    }

    private static void AddEdgeRef(Dictionary<long, List<int>> edgeTris, uint a, uint b, int t)
    {
        long key = EdgeKey(a, b);
        if (!edgeTris.TryGetValue(key, out var list))
        {
            list = new List<int>(2);
            edgeTris[key] = list;
        }
        list.Add(t);
    }

    private static long EdgeKey(uint a, uint b)
        => a < b ? ((long)a << 32) | b : ((long)b << 32) | a;

    private static void ComputeMortonBounds(ReadOnlySpan<Float3> positions, ReadOnlySpan<uint> triangles, out Float3 min, out Float3 scale)
    {
        Float3 minV = new(float.MaxValue), maxV = new(float.MinValue);
        for (int i = 0; i < triangles.Length; i++)
        {
            Float3 p = positions[(int)triangles[i]];
            minV = Maths.Min(minV, p);
            maxV = Maths.Max(maxV, p);
        }

        min = minV;
        Float3 extent = maxV - minV;
        scale = new Float3(
            extent.X > 1e-20f ? 1023f / extent.X : 0f,
            extent.Y > 1e-20f ? 1023f / extent.Y : 0f,
            extent.Z > 1e-20f ? 1023f / extent.Z : 0f);
    }

    private static uint MortonCode(in Float3 p, in Float3 min, in Float3 scale)
    {
        uint x = (uint)Math.Clamp((int)((p.X - min.X) * scale.X), 0, 1023);
        uint y = (uint)Math.Clamp((int)((p.Y - min.Y) * scale.Y), 0, 1023);
        uint z = (uint)Math.Clamp((int)((p.Z - min.Z) * scale.Z), 0, 1023);
        return (Part1By1(z) << 2) | (Part1By1(y) << 1) | Part1By1(x);
    }

    private static uint Part1By1(uint x)
    {
        x &= 0x0000ffff;
        x = (x | (x << 8)) & 0x00FF00FF;
        x = (x | (x << 4)) & 0x0F0F0F0F;
        x = (x | (x << 2)) & 0x33333333;
        x = (x | (x << 1)) & 0x55555555;
        return x;
    }
}
