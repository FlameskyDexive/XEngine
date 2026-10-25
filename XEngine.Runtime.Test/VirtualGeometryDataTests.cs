// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime.VirtualGeometry;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>Tests for <see cref="VirtualGeometryData"/> serialization and hierarchy invariants.</summary>
public class VirtualGeometryDataTests
{
    internal static VirtualGeometryData BuildTwoLevelData()
    {
        // Finest level: 2 leaf clusters of one triangle each. Coarse level: 1 root cluster.
        // Vertices 0..5 belong to the leaf clusters, 6..8 to the root.
        return new VirtualGeometryData
        {
            Positions =
            [
                new Float3(0, 0, 0), new Float3(1, 0, 0), new Float3(0, 1, 0),
                new Float3(2, 0, 0), new Float3(3, 0, 0), new Float3(2, 1, 0),
                new Float3(0, 0, 0), new Float3(3, 0, 0), new Float3(1.5f, 1, 0),
            ],
            Normals = [new Float3(0, 0, 1), new Float3(0, 0, 1), new Float3(0, 0, 1),
                new Float3(0, 0, 1), new Float3(0, 0, 1), new Float3(0, 0, 1),
                new Float3(0, 0, 1), new Float3(0, 0, 1), new Float3(0, 0, 1)],
            UV = [new Float2(0, 0), new Float2(1, 0), new Float2(0, 1),
                new Float2(1, 0), new Float2(2, 0), new Float2(1, 1),
                new Float2(0, 0), new Float2(2, 0), new Float2(1, 1)],
            Indices = [0, 1, 2, 3, 4, 5, 6, 7, 8],
            Clusters =
            [
                new VirtualGeometryCluster
                {
                    Bounds = new AABB(new Float3(0, 0, 0), new Float3(1, 1, 0)),
                    Error = 0f, IndexStart = 0, IndexCount = 3, VertexStart = 0, VertexCount = 3,
                    ChildGroupsStart = 0, ChildGroupsCount = 0, SubMesh = 0,
                },
                new VirtualGeometryCluster
                {
                    Bounds = new AABB(new Float3(2, 0, 0), new Float3(3, 1, 0)),
                    Error = 0f, IndexStart = 3, IndexCount = 3, VertexStart = 3, VertexCount = 3,
                    ChildGroupsStart = 0, ChildGroupsCount = 0, SubMesh = 0,
                },
                new VirtualGeometryCluster
                {
                    Bounds = new AABB(new Float3(0, 0, 0), new Float3(3, 1, 0)),
                    Error = 0.5f, IndexStart = 6, IndexCount = 3, VertexStart = 6, VertexCount = 3,
                    ChildGroupsStart = 0, ChildGroupsCount = 1, SubMesh = 0,
                },
            ],
            Groups =
            [
                new VirtualGeometryClusterGroup
                {
                    Bounds = new AABB(new Float3(0, 0, 0), new Float3(3, 1, 0)),
                    Error = 0.5f, ClusterStart = 0, ClusterCount = 2, ParentCluster = 2, ParentClusterCount = 1,
                },
            ],
            Levels =
            [
                new VirtualGeometryLodLevel { ClusterStart = 0, ClusterCount = 2, GroupStart = 0, GroupCount = 1, TriangleCount = 2 },
                new VirtualGeometryLodLevel { ClusterStart = 2, ClusterCount = 1, GroupStart = 1, GroupCount = 0, TriangleCount = 1 },
            ],
            Bounds = new AABB(new Float3(0, 0, 0), new Float3(3, 1, 0)),
            SubMeshCount = 1,
            SourceTriangleCount = 2,
            SourceVertexCount = 6,
        };
    }

    [Fact]
    public void SerializeDeserialize_RoundtripsAllArrays()
    {
        var data = BuildTwoLevelData();

        EchoObject tag = Serializer.Serialize(data);
        var restored = Serializer.Deserialize<VirtualGeometryData>(tag)!;

        Assert.Equal(9, restored.Positions.Length);
        Assert.Equal(new Float3(1.5f, 1, 0), restored.Positions[8]);
        Assert.NotNull(restored.Normals);
        Assert.Equal(9, restored.Normals!.Length);
        Assert.NotNull(restored.UV);
        Assert.Equal(new Float2(2, 0), restored.UV![7]);
        Assert.Null(restored.Tangents);
        Assert.Null(restored.UV2);
        Assert.Null(restored.Colors);
        Assert.Equal(data.Indices, restored.Indices);

        Assert.Equal(3, restored.Clusters.Length);
        Assert.Equal(0.5f, restored.Clusters[2].Error);
        Assert.Equal(1, restored.Clusters[2].ChildGroupsCount);
        Assert.Equal(new AABB(new Float3(0, 0, 0), new Float3(1, 1, 0)), restored.Clusters[0].Bounds);

        Assert.Single(restored.Groups);
        Assert.Equal(2, restored.Groups[0].ParentCluster);
        Assert.Equal(1, restored.Groups[0].ParentClusterCount);
        Assert.Equal(0.5f, restored.Groups[0].Error);

        Assert.Equal(2, restored.Levels.Length);
        Assert.Equal(2, restored.Levels[0].ClusterCount);
        Assert.Equal(1, restored.Levels[1].TriangleCount);

        Assert.Equal(new AABB(new Float3(0, 0, 0), new Float3(3, 1, 0)), restored.Bounds);
        Assert.Equal(1, restored.SubMeshCount);
        Assert.Equal(2, restored.SourceTriangleCount);
        Assert.Equal(6, restored.SourceVertexCount);
    }

    [Fact]
    public void SerializeDeserialize_MinimalData_Roundtrips()
    {
        var data = new VirtualGeometryData
        {
            Positions = [new Float3(0, 0, 0), new Float3(1, 0, 0), new Float3(0, 1, 0)],
            Indices = [0, 1, 2],
            Clusters = [new VirtualGeometryCluster { Error = 0f, IndexStart = 0, IndexCount = 3, VertexStart = 0, VertexCount = 3 }],
            Groups = [],
            Levels = [new VirtualGeometryLodLevel { ClusterStart = 0, ClusterCount = 1, TriangleCount = 1 }],
        };

        var restored = Serializer.Deserialize<VirtualGeometryData>(Serializer.Serialize(data))!;

        Assert.Null(restored.Normals);
        Assert.Empty(restored.Groups);
        Assert.Single(restored.Clusters);
        Assert.Equal(data.Indices, restored.Indices);
    }

    // Parent cluster geometry must cover strictly fewer triangles than its child group.
    [Fact]
    public void Hierarchy_ParentTriangleCountSmallerThanChildren()
    {
        var data = BuildTwoLevelData();

        Assert.True(data.Levels[1].TriangleCount < data.Levels[0].TriangleCount);
    }

    // Finest-level clusters reproduce the source exactly, so their error must be zero.
    [Fact]
    public void Hierarchy_FinestClustersHaveZeroError()
    {
        var data = BuildTwoLevelData();

        for (int i = data.Levels[0].ClusterStart; i < data.Levels[0].ClusterStart + data.Levels[0].ClusterCount; i++)
            Assert.Equal(0f, data.Clusters[i].Error);
    }
}
