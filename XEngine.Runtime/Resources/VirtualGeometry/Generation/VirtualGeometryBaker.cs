// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.VirtualGeometry.Generation;

/// <summary>Bake options surfaced through importer settings.</summary>
public struct VirtualGeometryBakeOptions
{
    /// <summary>Maximum triangles per cluster. 128 matches Unity Virtual Geometry's meshlets.</summary>
    public int ClusterTriangles;
    /// <summary>Fraction of triangles kept per LOD level (0.5 = halve).</summary>
    public float SimplifyRatio;
    /// <summary>Hard cap on LOD levels.</summary>
    public int MaxLodLevels;
    /// <summary>Fraction of the mesh bounds extent a single level-0 collapse may deviate;
    /// the budget doubles each level as legitimate LOD deviation grows. Candidates beyond it
    /// are dropped so the mesh under-simplifies instead of baking a wild error.</summary>
    public float ErrorCutoffFraction;

    public static VirtualGeometryBakeOptions Default => new()
    {
        ClusterTriangles = 128,
        SimplifyRatio = 0.5f,
        MaxLodLevels = 16,
        ErrorCutoffFraction = 0.01f,
    };
}

/// <summary>
/// Builds a <see cref="VirtualGeometryData"/> from a source mesh (Nanite-style whole-mesh
/// cascade): each LOD level simplifies the previous level's entire mesh with QEM (only the
/// mesh's true boundary is locked — no internal group borders, which would fragment the
/// surface into progressively smaller locked patches), then re-clusterizes the result. Fine
/// clusters link to the coarser cluster that covers them, forming a strict tree.
/// </summary>
public static class VirtualGeometryBaker
{
    private sealed class BuildCluster
    {
        public Float3[] Verts = Array.Empty<Float3>();
        public float[] VertErrors = Array.Empty<float>();
        public int[] Origins = Array.Empty<int>(); // source-mesh vertex ids for attribute lookup
        public uint[] Indices = Array.Empty<uint>(); // local, 3 per triangle
        public int SubMesh;
        public float Error;
        public AABB Bounds;
        public int PackedIndex;
    }

    private sealed class BuildGroup
    {
        public int ClusterStart;
        public int ClusterCount;
        // Identity survives reordering on the next bake iteration and later submesh
        // blocks shifting the level's absolute start. Resolve only after packing.
        public BuildCluster ParentCluster = null!;
        public float Error;
        public AABB Bounds;
    }

    /// <summary>Working mesh for one level: welded vertices, triangles, carried error, and
    /// the source-mesh vertex each vertex inherited its attributes from.</summary>
    private sealed class LevelMesh
    {
        public Float3[] Positions = Array.Empty<Float3>();
        public int[] Triangles = Array.Empty<int>();
        public float[] VertexErrors = Array.Empty<float>();
        public int[] Origins = Array.Empty<int>();
    }

    public static VirtualGeometryData Generate(Mesh mesh, VirtualGeometryBakeOptions options)
    {
        if (mesh.MeshTopology != Topology.Triangles)
            throw new InvalidOperationException($"Virtual geometry requires triangle-list meshes, {mesh.Name ?? "mesh"} uses {mesh.MeshTopology}.");

        Float3[] srcPositions = mesh.Vertices;
        uint[] srcIndices = mesh.Indices;
        int subMeshCount = mesh.SubMeshCount;

        if (srcPositions.Length == 0 || srcIndices.Length == 0)
            throw new InvalidOperationException($"Virtual geometry requires a non-empty mesh, {mesh.Name ?? "mesh"} has none.");

        var levels = new List<List<BuildCluster>>();
        var groupsPerLevel = new List<List<BuildGroup>>();
        int sourceTriangles = 0;

        float meshExtent = Float3.Length(mesh.bounds.Max - mesh.bounds.Min);
        float baseCutoff = meshExtent * options.ErrorCutoffFraction;

        for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
        {
            SubMeshDescriptor desc = mesh.GetSubMesh(subMesh);
            if (desc.Topology != Topology.Triangles)
                throw new InvalidOperationException($"Virtual geometry requires triangle submeshes, submesh {subMesh} uses {desc.Topology}.");
            sourceTriangles += desc.IndexCount / 3;

            // Level 0 works straight off the submesh's source triangles.
            var levelMesh = ExtractLevelMesh(srcPositions, srcIndices, desc);
            List<BuildCluster>? clusters = null;

            int level = 0;
            while (true)
            {
                clusters ??= ClusterizeLevel(levelMesh, subMesh, options);
                if (clusters.Count == 0) break;

                if (level + 1 >= options.MaxLodLevels || clusters.Count <= 1)
                {
                    CommitLevel(levels, groupsPerLevel, level, clusters, _emptyGroups);
                    break;
                }

                // Simplify the whole level; the error budget doubles per level, mirroring how
                // legitimate LOD deviation grows as geometry halves.
                float levelCutoff = baseCutoff * (1 << Math.Min(level, 10));
                float maxErrorSq = levelCutoff * levelCutoff;
                int target = Math.Max(1, (int)Math.Round(levelMesh.Triangles.Length / 3 * options.SimplifyRatio));

                SimplifiedMesh simplified = MeshSimplifier.Simplify(
                    levelMesh.Positions, levelMesh.Triangles, target, levelMesh.VertexErrors, maxErrorSq);

                // Near the locked mesh boundary QEM slows to a crawl; grinding out a few
                // percent per level bakes exploding errors for no useful LOD step. Stop when
                // the level halves no better than 90%.
                if (simplified.Triangles.Length >= levelMesh.Triangles.Length * 0.9f)
                {
                    CommitLevel(levels, groupsPerLevel, level, clusters, _emptyGroups);
                    break;
                }

                LevelMesh nextMesh = ToNextLevelMesh(simplified, levelMesh);

                // Clusterize the coarser mesh, then assign each fine cluster to the coarse
                // cluster that covers it — groups are exactly those assignments.
                List<BuildCluster> coarseClusters = ClusterizeLevel(nextMesh, subMesh, options);
                List<BuildGroup> groups = AssignGroups(clusters, coarseClusters);

                // Groups reference fine-cluster ranges; commit the fine level first.
                CommitLevel(levels, groupsPerLevel, level, clusters, groups);
                clusters = coarseClusters;
                levelMesh = nextMesh;
                level++;
            }
        }

        return Pack(mesh, srcPositions, levels, groupsPerLevel, subMeshCount, sourceTriangles);
    }

    private static LevelMesh ExtractLevelMesh(Float3[] srcPositions, uint[] srcIndices, SubMeshDescriptor desc)
    {
        var level = new LevelMesh
        {
            Positions = new Float3[srcPositions.Length],
            Triangles = new int[desc.IndexCount],
            VertexErrors = new float[srcPositions.Length],
            Origins = new int[srcPositions.Length],
        };
        Array.Copy(srcPositions, level.Positions, srcPositions.Length);
        for (int v = 0; v < srcPositions.Length; v++) level.Origins[v] = v;
        for (int i = 0; i < desc.IndexCount; i++)
            level.Triangles[i] = (int)srcIndices[desc.IndexStart + i];
        return level;
    }

    private static LevelMesh ToNextLevelMesh(SimplifiedMesh simplified, LevelMesh parent)
    {
        var next = new LevelMesh
        {
            Positions = simplified.Positions,
            Triangles = simplified.Triangles,
            VertexErrors = simplified.VertexErrors,
            Origins = new int[simplified.Positions.Length],
        };
        for (int v = 0; v < simplified.Positions.Length; v++)
            next.Origins[v] = parent.Origins[simplified.OriginVertices[v]];
        return next;
    }

    private static List<BuildCluster> ClusterizeLevel(LevelMesh levelMesh, int subMesh, VirtualGeometryBakeOptions options)
    {
        var triangles = new uint[levelMesh.Triangles.Length];
        for (int i = 0; i < triangles.Length; i++) triangles[i] = (uint)levelMesh.Triangles[i];
        List<RawCluster> raw = MeshClusterizer.Clusterize(levelMesh.Positions, triangles, options.ClusterTriangles);

        var result = new List<BuildCluster>(raw.Count);
        foreach (var r in raw)
        {
            int n = r.VertexIds.Length;
            var cluster = new BuildCluster
            {
                Verts = new Float3[n],
                VertErrors = new float[n],
                Origins = new int[n],
                Indices = new uint[r.Triangles.Length],
                SubMesh = subMesh,
                Bounds = r.Bounds,
            };
            var local = new Dictionary<uint, uint>(n);
            for (int i = 0; i < n; i++)
            {
                uint id = r.VertexIds[i];
                cluster.Verts[i] = levelMesh.Positions[id];
                cluster.VertErrors[i] = levelMesh.VertexErrors[id];
                cluster.Origins[i] = levelMesh.Origins[id];
                local[id] = (uint)i;
            }
            for (int t = 0; t < r.Triangles.Length; t += 3)
            {
                cluster.Indices[t] = local[r.Triangles[t]];
                cluster.Indices[t + 1] = local[r.Triangles[t + 1]];
                cluster.Indices[t + 2] = local[r.Triangles[t + 2]];
            }
            cluster.Error = 0f;
            foreach (float e in cluster.VertErrors) cluster.Error = Math.Max(cluster.Error, e);
            result.Add(cluster);
        }
        return result;
    }

    /// <summary>
    /// Assigns every fine cluster to the coarse cluster whose bounds best cover it (centroid
    /// containment, falling back to nearest bounds center), then reorders the fine clusters
    /// so each assignment set is contiguous. Every fine cluster lands in exactly one group,
    /// keeping the hierarchy a strict tree.
    /// </summary>
    private static List<BuildGroup> AssignGroups(List<BuildCluster> fineClusters, List<BuildCluster> coarseClusters)
    {
        var coarseCenters = new Float3[coarseClusters.Count];
        for (int p = 0; p < coarseClusters.Count; p++)
            coarseCenters[p] = coarseClusters[p].Bounds.Center;

        int[] assignment = new int[fineClusters.Count];
        for (int c = 0; c < fineClusters.Count; c++)
        {
            Float3 center = fineClusters[c].Bounds.Center;
            int best = -1;
            float bestDistSq = float.MaxValue;
            for (int p = 0; p < coarseClusters.Count; p++)
            {
                // Containment wins outright; otherwise nearest center.
                if (coarseClusters[p].Bounds.Contains(center))
                {
                    float d = Float3.LengthSquared(coarseCenters[p] - center);
                    if (d < bestDistSq) { bestDistSq = d; best = p; }
                }
                else
                {
                    float d = Float3.LengthSquared(coarseCenters[p] - center);
                    // Slight bias toward containment: distance counts double when outside.
                    d *= 2f;
                    if (d < bestDistSq) { bestDistSq = d; best = p; }
                }
            }
            assignment[c] = best;
        }

        // Clusters partition the actual coarse triangles; a cluster without a centroid
        // assignment is still part of the surface. Give every parent a child by moving
        // the nearest fine cluster from a parent that has more than one. Simplification
        // reduces triangle count, so there are at least as many fine clusters as parents.
        var childCounts = new int[coarseClusters.Count];
        for (int c = 0; c < assignment.Length; c++) childCounts[assignment[c]]++;
        for (int p = 0; p < coarseClusters.Count; p++)
        {
            if (childCounts[p] != 0) continue;
            int best = -1;
            float bestDistSq = float.MaxValue;
            for (int c = 0; c < fineClusters.Count; c++)
            {
                if (childCounts[assignment[c]] <= 1) continue;
                float distance = Float3.LengthSquared(fineClusters[c].Bounds.Center - coarseCenters[p]);
                if (distance < bestDistSq) { bestDistSq = distance; best = c; }
            }
            if (best < 0)
                throw new InvalidOperationException("Coarse virtual geometry has more clusters than the finer level.");
            childCounts[assignment[best]]--;
            assignment[best] = p;
            childCounts[p]++;
        }

        // Reorder fine clusters grouped by parent; coarse order keeps spatial coherence.
        var order = new List<int>(fineClusters.Count);
        var seen = new bool[fineClusters.Count];
        for (int p = 0; p < coarseClusters.Count; p++)
        {
            for (int c = 0; c < fineClusters.Count; c++)
            {
                if (!seen[c] && assignment[c] == p)
                {
                    seen[c] = true;
                    order.Add(c);
                }
            }
        }
        for (int c = 0; c < fineClusters.Count; c++)
            if (!seen[c]) order.Add(c); // safety: unassigned (should not happen)

        var permuted = new List<BuildCluster>(fineClusters.Count);
        foreach (int idx in order) permuted.Add(fineClusters[idx]);
        fineClusters.Clear();
        fineClusters.AddRange(permuted);

        var groups = new List<BuildGroup>();
        int i = 0;
        while (i < fineClusters.Count)
        {
            int parent = assignment[order[i]];
            int start = i;
            float maxError = 0f;
            Float3 min = new(float.MaxValue), max = new(float.MinValue);
            while (i < fineClusters.Count && assignment[order[i]] == parent)
            {
                BuildCluster c = fineClusters[i];
                maxError = Math.Max(maxError, c.Error);
                min = Maths.Min(min, c.Bounds.Min);
                max = Maths.Max(max, c.Bounds.Max);
                i++;
            }

            if (parent < 0) continue; // unassigned safety: leave those clusters groupless

            groups.Add(new BuildGroup
            {
                ClusterStart = start,
                ClusterCount = i - start,
                ParentCluster = coarseClusters[parent],
                Error = maxError,
                Bounds = new AABB(min, max),
            });
        }

        return groups;
    }

    private static void CommitLevel(List<List<BuildCluster>> levels, List<List<BuildGroup>> groupsPerLevel,
        int level, List<BuildCluster> clusters, List<BuildGroup> groups)
    {
        while (levels.Count <= level) levels.Add(new List<BuildCluster>());
        while (groupsPerLevel.Count <= level) groupsPerLevel.Add(new List<BuildGroup>());

        int blockBase = levels[level].Count;
        levels[level].AddRange(clusters);

        foreach (BuildGroup g in groups)
            g.ClusterStart += blockBase; // level-local → block-local → absolute in Pack

        groupsPerLevel[level].AddRange(groups);
    }

    private static readonly List<BuildGroup> _emptyGroups = new();

    private static VirtualGeometryData Pack(Mesh mesh, Float3[] srcPositions,
        List<List<BuildCluster>> levels, List<List<BuildGroup>> groupsPerLevel,
        int subMeshCount, int sourceTriangles)
    {
        var data = new VirtualGeometryData();

        var allClusters = new List<VirtualGeometryCluster>();
        var allGroups = new List<VirtualGeometryClusterGroup>();
        var lodLevels = new List<VirtualGeometryLodLevel>();

        var positions = new List<Float3>();
        var indices = new List<uint>();

        Float3[]? srcNormals = mesh.HasNormals ? mesh.Normals : null;
        Float4[]? srcTangents = mesh.HasTangents ? mesh.Tangents : null;
        Float2[]? srcUv = mesh.HasUV ? mesh.UV : null;
        Float2[]? srcUv2 = mesh.HasUV2 ? mesh.UV2 : null;
        Color[]? srcColors = null;
        if (mesh.HasColors) srcColors = mesh.Colors;
        else if (mesh.HasColors32)
        {
            Color32[] c32 = mesh.Colors32;
            srcColors = new Color[c32.Length];
            for (int i = 0; i < c32.Length; i++) srcColors[i] = c32[i];
        }

        List<Float3>? normals = srcNormals != null ? new List<Float3>() : null;
        List<Float4>? tangents = srcTangents != null ? new List<Float4>() : null;
        List<Float2>? uv = srcUv != null ? new List<Float2>() : null;
        List<Float2>? uv2 = srcUv2 != null ? new List<Float2>() : null;
        List<Color>? colors = srcColors != null ? new List<Color>() : null;

        for (int level = 0; level < levels.Count; level++)
        {
            int clusterStart = allClusters.Count;
            int groupStart = allGroups.Count;

            for (int c = 0; c < levels[level].Count; c++)
            {
                BuildCluster bc = levels[level][c];
                int vertexStart = positions.Count;
                int indexStart = indices.Count;

                for (int v = 0; v < bc.Verts.Length; v++)
                {
                    int origin = bc.Origins[v];
                    positions.Add(bc.Verts[v]);
                    normals?.Add(srcNormals![origin]);
                    tangents?.Add(srcTangents![origin]);
                    uv?.Add(srcUv![origin]);
                    uv2?.Add(srcUv2![origin]);
                    colors?.Add(srcColors![origin]);
                }
                for (int t = 0; t < bc.Indices.Length; t++)
                    indices.Add((uint)(vertexStart + bc.Indices[t]));

                bc.PackedIndex = allClusters.Count;
                allClusters.Add(new VirtualGeometryCluster
                {
                    Bounds = bc.Bounds,
                    Error = bc.Error,
                    IndexStart = indexStart,
                    IndexCount = bc.Indices.Length,
                    VertexStart = vertexStart,
                    VertexCount = bc.Verts.Length,
                    SubMesh = bc.SubMesh,
                });
            }

            for (int g = 0; g < groupsPerLevel[level].Count; g++)
            {
                BuildGroup bg = groupsPerLevel[level][g];
                allGroups.Add(new VirtualGeometryClusterGroup
                {
                    Bounds = bg.Bounds,
                    Error = bg.Error,
                    ClusterStart = bg.ClusterStart,
                    ClusterCount = bg.ClusterCount,
                    ParentCluster = -1, // resolved after all levels have been packed
                    ParentClusterCount = 1,
                });
            }

            int triangleCount = 0;
            for (int c = 0; c < levels[level].Count; c++)
                triangleCount += levels[level][c].Indices.Length / 3;

            lodLevels.Add(new VirtualGeometryLodLevel
            {
                ClusterStart = clusterStart,
                ClusterCount = levels[level].Count,
                GroupStart = groupStart,
                GroupCount = groupsPerLevel[level].Count,
                TriangleCount = triangleCount,
            });
        }

        // All cluster identities now have final absolute indices. Resolve links after
        // every level and submesh is packed, so no earlier ordering leaks into the data.
        for (int level = 0; level < levels.Count; level++)
        {
            int levelGroupBase = lodLevels[level].GroupStart;
            int levelClusterBase = lodLevels[level].ClusterStart;

            for (int g = 0; g < groupsPerLevel[level].Count; g++)
            {
                BuildGroup bg = groupsPerLevel[level][g];
                int groupAbsolute = levelGroupBase + g;
                bg.ClusterStart += levelClusterBase;
                int parentIndex = bg.ParentCluster.PackedIndex;

                var packed = allGroups[levelGroupBase + g];
                packed.ClusterStart = bg.ClusterStart;
                packed.ParentCluster = parentIndex;
                allGroups[levelGroupBase + g] = packed;

                // Point the parent cluster (level+1) at this group.
                var parent = allClusters[parentIndex];
                parent.ChildGroupsStart = groupAbsolute;
                parent.ChildGroupsCount = 1;
                allClusters[parentIndex] = parent;
            }
        }

        data.Positions = positions.ToArray();
        data.Indices = indices.ToArray();
        data.Clusters = allClusters.ToArray();
        data.Groups = allGroups.ToArray();
        data.Levels = lodLevels.ToArray();
        if (normals != null) data.Normals = normals.ToArray();
        if (tangents != null) data.Tangents = tangents.ToArray();
        if (uv != null) data.UV = uv.ToArray();
        if (uv2 != null) data.UV2 = uv2.ToArray();
        if (colors != null) data.Colors = colors.ToArray();
        data.Bounds = mesh.bounds;
        data.SubMeshCount = subMeshCount;
        data.SourceTriangleCount = sourceTriangles;
        data.SourceVertexCount = srcPositions.Length;
        return data;
    }
}
