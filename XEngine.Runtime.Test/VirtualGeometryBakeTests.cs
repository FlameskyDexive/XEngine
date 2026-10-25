// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Runtime.VirtualGeometry;
using XEngine.Runtime.VirtualGeometry.Generation;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>Tests for the virtual geometry bake pipeline (clusterize, simplify, hierarchy).</summary>
public class VirtualGeometryBakeTests
{
    /// <summary>A UV-grid tessellated plane: trisX*trisY*2 triangles, grid-aligned vertices.</summary>
    internal static Mesh BuildGridMesh(int trisX, int trisY)
        => BuildGridMesh(trisX, trisY, flat: true);

    /// <summary>Grid mesh variant; when <paramref name="flat"/> is false, z follows a smooth
    /// height field so QEM simplification accrues real geometric error.</summary>
    internal static Mesh BuildGridMesh(int trisX, int trisY, bool flat)
    {
        int vertsX = trisX + 1, vertsY = trisY + 1;
        var positions = new Float3[vertsX * vertsY];
        var normals = new Float3[vertsX * vertsY];
        var uvs = new Float2[vertsX * vertsY];
        for (int y = 0; y < vertsY; y++)
        {
            for (int x = 0; x < vertsX; x++)
            {
                int i = y * vertsX + x;
                float z = flat ? 0f : MathF.Sin(x * 0.35f) * MathF.Cos(y * 0.3f) * 1.5f;
                positions[i] = new Float3(x, y, z);
                normals[i] = new Float3(0, 0, 1);
                uvs[i] = new Float2(x / (float)trisX, y / (float)trisY);
            }
        }

        var indices = new uint[trisX * trisY * 6];
        int t = 0;
        for (int y = 0; y < trisY; y++)
        {
            for (int x = 0; x < trisX; x++)
            {
                int v0 = y * vertsX + x;
                int v1 = v0 + 1;
                int v2 = v0 + vertsX;
                int v3 = v2 + 1;
                indices[t++] = (uint)v0; indices[t++] = (uint)v2; indices[t++] = (uint)v1;
                indices[t++] = (uint)v1; indices[t++] = (uint)v2; indices[t++] = (uint)v3;
            }
        }

        var mesh = new Mesh
        {
            Vertices = positions,
            Normals = normals,
            UV = uvs,
            Indices = indices,
        };
        mesh.RecalculateBounds();
        return mesh;
    }

    // --- MeshClusterizer ---

    [Fact]
    public void Clusterize_RespectsTriangleCap()
    {
        var positions = new Float3[100];
        for (int i = 0; i < 100; i++)
            positions[i] = new Float3(i % 10, i / 10, (i * 7) % 13);

        var triangles = new uint[900 * 3];
        for (int t = 0; t < 900; t++)
        {
            triangles[t * 3] = (uint)(t % 97);
            triangles[t * 3 + 1] = (uint)((t * 3 + 1) % 97);
            triangles[t * 3 + 2] = (uint)((t * 7 + 2) % 97);
        }

        var clusters = MeshClusterizer.Clusterize(positions, triangles, 128);

        Assert.True(clusters.Count >= 8);
        foreach (var c in clusters)
            Assert.InRange(c.Triangles.Length / 3, 1, 128);
        int total = 0;
        foreach (var c in clusters) total += c.Triangles.Length / 3;
        Assert.Equal(900, total);
    }

    [Fact]
    public void Clusterize_ClusterBoundsContainReferencedVertices()
    {
        Mesh mesh = BuildGridMesh(20, 20);

        var clusters = MeshClusterizer.Clusterize(mesh.Vertices, mesh.Indices, 128);

        foreach (var c in clusters)
        {
            foreach (uint id in c.VertexIds)
            {
                Float3 p = mesh.Vertices[(int)id];
                Assert.InRange(p.X, c.Bounds.Min.X - 1e-4f, c.Bounds.Max.X + 1e-4f);
                Assert.InRange(p.Y, c.Bounds.Min.Y - 1e-4f, c.Bounds.Max.Y + 1e-4f);
            }
        }
    }

    // --- MeshSimplifier ---

    [Fact]
    public void Simplify_GridPlane_ReachesTargetAndKeepsBounds()
    {
        Mesh mesh = BuildGridMesh(30, 30); // 1800 triangles
        Float3[] pool = mesh.Vertices;
        var triangles = new int[mesh.Indices.Length];
        for (int i = 0; i < triangles.Length; i++) triangles[i] = (int)mesh.Indices[i];

        var result = MeshSimplifier.Simplify(pool, triangles, 450);

        Assert.True(result.Triangles.Length / 3 <= 450);
        Assert.True(result.Triangles.Length / 3 > 0);

        // A plane simplified by QEM stays a plane: all z within accumulated error (0 here —
        // coplanar collapses are free under plane quadrics).
        foreach (var p in result.Positions)
            Assert.True(Math.Abs(p.Z) < 1e-3f, $"vertex moved off plane: {p}");
        foreach (float e in result.VertexErrors)
            Assert.True(e < 1e-2f, $"coplanar simplification should stay near error-free, got {e}");
    }

    [Fact]
    public void Simplify_LocksBoundaryVertices()
    {
        Mesh mesh = BuildGridMesh(10, 10); // 200 triangles, border = grid edge
        var triangles = new int[mesh.Indices.Length];
        for (int i = 0; i < triangles.Length; i++) triangles[i] = (int)mesh.Indices[i];

        var result = MeshSimplifier.Simplify(mesh.Vertices, triangles, 50);

        // Corner vertices are on the boundary and must survive untouched.
        Assert.Contains(new Float3(0, 0, 0), result.Positions);
        Assert.Contains(new Float3(10, 0, 0), result.Positions);
        Assert.Contains(new Float3(0, 10, 0), result.Positions);
        Assert.Contains(new Float3(10, 10, 0), result.Positions);
    }

    [Fact]
    public void Simplify_NoPossibleCollapses_ReturnsInput()
    {
        // A single triangle: nothing collapsible.
        var result = MeshSimplifier.Simplify(
            [new Float3(0, 0, 0), new Float3(1, 0, 0), new Float3(0, 1, 0)],
            [0, 1, 2], targetTriangles: 0);

        Assert.Equal(3, result.Triangles.Length);
        Assert.Equal(3, result.Positions.Length);
    }

    [Fact]
    public void Simplify_AccumulatesInitialErrors()
    {
        Mesh mesh = BuildGridMesh(10, 10);
        var triangles = new int[mesh.Indices.Length];
        for (int i = 0; i < triangles.Length; i++) triangles[i] = (int)mesh.Indices[i];

        var initial = new float[mesh.Vertices.Length];
        Array.Fill(initial, 0.25f);

        var result = MeshSimplifier.Simplify(mesh.Vertices, triangles, 50, initial);

        foreach (float e in result.VertexErrors)
            Assert.True(e >= 0.25f - 1e-4f, $"error must not fall below the seeded floor, got {e}");
    }

    // --- VirtualGeometryBaker ---

    [Fact]
    public void Bake_GridMesh_BuildsValidHierarchy()
    {
        Mesh mesh = BuildGridMesh(40, 40, flat: false); // 3200 triangles, height field
        var data = VirtualGeometryBaker.Generate(mesh, VirtualGeometryBakeOptions.Default);

        Assert.True(data.Levels.Length >= 2, "a 3200-triangle mesh should produce multiple LOD levels");
        Assert.Equal(3200, data.SourceTriangleCount);

        // Level 0 is the exact source.
        Assert.Equal(3200, data.Levels[0].TriangleCount);
        for (int i = data.Levels[0].ClusterStart; i < data.Levels[0].ClusterStart + data.Levels[0].ClusterCount; i++)
            Assert.Equal(0f, data.Clusters[i].Error);

        // Every level gets coarser and every coarser cluster carries nonzero error.
        for (int l = 1; l < data.Levels.Length; l++)
        {
            Assert.True(data.Levels[l].TriangleCount < data.Levels[l - 1].TriangleCount,
                $"level {l} must have fewer triangles than level {l - 1}");
            for (int i = data.Levels[l].ClusterStart; i < data.Levels[l].ClusterStart + data.Levels[l].ClusterCount; i++)
                Assert.True(data.Clusters[i].Error > 0f, $"coarse cluster {i} at level {l} must carry error");
        }

        // Boundary locking/error limits may stop simplification before one cluster.
        // Every remaining root is part of the complete surface and must be retained.
        ref readonly var rootLevel = ref data.Levels[data.Levels.Length - 1];
        Assert.True(rootLevel.ClusterCount > 0);

        // Hierarchy links: every group's parent range points at real clusters on the next level.
        for (int l = 0; l < data.Levels.Length - 1; l++)
        {
            for (int g = data.Levels[l].GroupStart; g < data.Levels[l].GroupStart + data.Levels[l].GroupCount; g++)
            {
                var group = data.Groups[g];
                Assert.InRange(group.ParentCluster,
                    data.Levels[l + 1].ClusterStart,
                    data.Levels[l + 1].ClusterStart + data.Levels[l + 1].ClusterCount - 1);
                Assert.True(group.ParentClusterCount >= 1);
            }
        }

        // Parent clusters point back at child groups; leaves have none.
        for (int l = 0; l < data.Levels.Length; l++)
        {
            bool finerExists = l > 0;
            for (int i = data.Levels[l].ClusterStart; i < data.Levels[l].ClusterStart + data.Levels[l].ClusterCount; i++)
            {
                var cluster = data.Clusters[i];
                if (l == 0)
                    Assert.Equal(0, cluster.ChildGroupsCount);
                else
                    Assert.InRange(cluster.ChildGroupsStart, data.Levels[l - 1].GroupStart,
                        data.Levels[l - 1].GroupStart + data.Levels[l - 1].GroupCount - 1);
            }
        }

        // Descending the tree from the root must recover the full triangle count of level 0
        // (every level-0 triangle reachable through group links).
        Assert.Equal(data.Levels[0].TriangleCount, CountReachableTriangles(data));
    }

    [Fact]
    public void Bake_TinyMesh_SingleLevel()
    {
        Mesh mesh = BuildGridMesh(4, 4); // 32 triangles → 1 cluster
        var data = VirtualGeometryBaker.Generate(mesh, VirtualGeometryBakeOptions.Default);

        Assert.Single(data.Levels);
        Assert.Single(data.Clusters);
        Assert.Equal(32, data.Levels[0].TriangleCount);
        Assert.Empty(data.Groups);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    public void Bake_Heightfield_EveryLodPreservesProjectedSurface(int size)
    {
        Mesh mesh = BuildGridMesh(size, size, flat: false);
        var data = VirtualGeometryBaker.Generate(mesh, VirtualGeometryBakeOptions.Default);
        // The simplifier locks the perimeter. Every full LOD must still tile the same
        // signed XY area, including coarse clusters that received no centroid assignment.
        for (int level = 0; level < data.Levels.Length; level++)
        {
            ref readonly var lod = ref data.Levels[level];
            double area = 0;
            for (int c = lod.ClusterStart; c < lod.ClusterStart + lod.ClusterCount; c++)
            {
                ref readonly var cluster = ref data.Clusters[c];
                for (int t = cluster.IndexStart; t < cluster.IndexStart + cluster.IndexCount; t += 3)
                {
                    Float3 a = data.Positions[data.Indices[t]];
                    Float3 b = data.Positions[data.Indices[t + 1]];
                    Float3 d = data.Positions[data.Indices[t + 2]];
                    area += ((double)b.X - a.X) * ((double)d.Y - a.Y)
                        - ((double)b.Y - a.Y) * ((double)d.X - a.X);
                }
            }
            double expected = -2.0 * size * size;
            Assert.True(Math.Abs(area - expected) < Math.Abs(expected) * 0.001,
                $"LOD {level} lost projected surface: signed twice-area {area}, expected {expected}");
        }
    }

    [Fact]
    public void Bake_MultiSubMesh_TagsClustersPerSubMesh()
    {
        Mesh mesh = BuildGridMesh(8, 8);
        // Split into two submeshes: first half / second half of the index buffer.
        mesh.SetSubMesh(0, new SubMeshDescriptor(0, mesh.Indices.Length / 2));
        mesh.SetSubMesh(1, new SubMeshDescriptor(mesh.Indices.Length / 2, mesh.Indices.Length / 2));

        var data = VirtualGeometryBaker.Generate(mesh, VirtualGeometryBakeOptions.Default);

        Assert.Equal(2, data.SubMeshCount);
        bool sawZero = false, sawOne = false;
        for (int l = 0; l < data.Levels.Length; l++)
            for (int i = data.Levels[l].ClusterStart; i < data.Levels[l].ClusterStart + data.Levels[l].ClusterCount; i++)
            {
                Assert.InRange(data.Clusters[i].SubMesh, 0, 1);
                if (data.Clusters[i].SubMesh == 0) sawZero = true;
                else sawOne = true;
            }
        Assert.True(sawZero && sawOne);
    }

    [Fact]
    public void Bake_MultiSubMesh_ParentLinksKeepIdentityAfterPacking()
    {
        Mesh source = BuildGridMesh(20, 20, flat: false);
        var positions = new Float3[source.Vertices.Length * 2];
        var indices = new uint[source.Indices.Length * 2];
        for (int v = 0; v < source.Vertices.Length; v++)
        {
            positions[v] = source.Vertices[v];
            positions[v + source.Vertices.Length] = source.Vertices[v] + new Float3(100, 0, 0);
        }
        for (int i = 0; i < source.Indices.Length; i++)
        {
            indices[i] = source.Indices[i];
            indices[i + source.Indices.Length] = source.Indices[i] + (uint)source.Vertices.Length;
        }
        var mesh = new Mesh { Vertices = positions, Indices = indices };
        mesh.SetSubMesh(0, new SubMeshDescriptor(0, source.Indices.Length));
        mesh.SetSubMesh(1, new SubMeshDescriptor(source.Indices.Length, source.Indices.Length));
        mesh.RecalculateBounds();
        var data = VirtualGeometryBaker.Generate(mesh, VirtualGeometryBakeOptions.Default);
        Assert.True(data.Levels.Length >= 3);
        for (int g = 0; g < data.Groups.Length; g++)
        {
            var group = data.Groups[g];
            var parent = data.Clusters[group.ParentCluster];
            Assert.Equal(g, parent.ChildGroupsStart);
            Assert.Equal(1, parent.ChildGroupsCount);
            for (int c = group.ClusterStart; c < group.ClusterStart + group.ClusterCount; c++)
                Assert.Equal(data.Clusters[c].SubMesh, parent.SubMesh);
        }
        Assert.Equal(data.SourceTriangleCount, CountReachableTriangles(data));
    }

    private static int CountReachableTriangles(VirtualGeometryData data)
    {
        // BFS from root clusters down through groups; sum leaf-most clusters reached.
        int total = 0;
        var stack = new System.Collections.Generic.List<int>();
        ref readonly var root = ref data.Levels[data.Levels.Length - 1];
        for (int i = root.ClusterStart; i < root.ClusterStart + root.ClusterCount; i++)
            stack.Add(i);

        while (stack.Count > 0)
        {
            int clusterIndex = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            var cluster = data.Clusters[clusterIndex];
            if (cluster.ChildGroupsCount == 0)
            {
                total += cluster.IndexCount / 3;
                continue;
            }
            for (int g = 0; g < cluster.ChildGroupsCount; g++)
            {
                var group = data.Groups[cluster.ChildGroupsStart + g];
                for (int c = 0; c < group.ClusterCount; c++)
                    stack.Add(group.ClusterStart + c);
            }
        }
        return total;
    }

    // --- VirtualGeometryFeatureSpec ---

    [Fact]
    public void FeatureSpec_DisabledByDefault_ReturnsNull()
    {
        var settings = EchoObject.NewCompound();
        new VirtualGeometryFeatureSpec().PopulateDefaults(settings);

        var feature = new VirtualGeometryFeatureSpec().TryGenerate(BuildGridMesh(8, 8), settings);

        Assert.Null(feature);
    }

    [Fact]
    public void FeatureSpec_Enabled_GeneratesFeature()
    {
        var settings = EchoObject.NewCompound();
        new VirtualGeometryFeatureSpec().PopulateDefaults(settings);
        settings[VirtualGeometryFeatureSpec.KeyRoot][VirtualGeometryFeatureSpec.Key_Enabled] = new EchoObject(true);

        var feature = new VirtualGeometryFeatureSpec().TryGenerate(BuildGridMesh(16, 16), settings);

        Assert.NotNull(feature);
        var vg = Assert.IsType<VirtualGeometryData>(feature);
        Assert.True(vg.Levels.Length >= 1);
    }
}
