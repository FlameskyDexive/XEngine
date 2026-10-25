// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Echo;
using XEngine.Runtime.MeshFeatures;
using XEngine.Vector;

namespace XEngine.Runtime.VirtualGeometry;

/// <summary>
/// One cluster of a virtualized mesh: a small triangle patch (default 128 triangles)
/// with its own vertex/index range, bounds, and geometric error relative to the source mesh.
/// </summary>
/// <remarks>
/// <see cref="ChildGroupsStart"/>/<see cref="ChildGroupsCount"/> index the groups of the
/// next FINER level whose union this cluster replaces; leaf clusters (finest level) have
/// count 0. Selection descends into children while a cluster's screen-space error exceeds
/// the renderer's pixel-error threshold.
/// </remarks>
public struct VirtualGeometryCluster
{
    public AABB Bounds;
    /// <summary>Max geometric deviation (mesh-local units) between this cluster's geometry and the source mesh.</summary>
    public float Error;
    public int IndexStart;
    public int IndexCount;
    public int VertexStart;
    public int VertexCount;
    public int ChildGroupsStart;
    public int ChildGroupsCount;
    public int SubMesh;
}

/// <summary>
/// A contiguous set of clusters at one level that was simplified together into the parent
/// clusters (<see cref="ParentCluster"/>, <see cref="ParentClusterCount"/>) on the next
/// coarser level.
/// </summary>
public struct VirtualGeometryClusterGroup
{
    public AABB Bounds;
    public float Error;
    public int ClusterStart;
    public int ClusterCount;
    public int ParentCluster;
    public int ParentClusterCount;
}

/// <summary>Cluster/group range of one LOD level. Level 0 is the finest (source) level.</summary>
public struct VirtualGeometryLodLevel
{
    public int ClusterStart;
    public int ClusterCount;
    public int GroupStart;
    public int GroupCount;
    public int TriangleCount;
}

/// <summary>
/// Clusterized mesh data for virtual geometry rendering (Unity 6 Virtual Geometry analog).
/// </summary>
/// <remarks>
/// Created by <see cref="XEngine.Runtime.VirtualGeometry.Generation.VirtualGeometryBaker"/>
/// during asset import; treat as read-only afterwards — to change it, edit the parent mesh's
/// importer settings and reimport. Vertex streams are parallel arrays covering every level;
/// each cluster references its own <see cref="VirtualGeometryCluster.VertexStart"/> slice.
/// The finest level's clusters reproduce the source mesh exactly (error 0).
/// </remarks>
public sealed class VirtualGeometryData : EngineObject, IMeshFeature
{
    private Float3[] _positions = Array.Empty<Float3>();
    private Float3[]? _normals;
    private Float4[]? _tangents;
    private Float2[]? _uv;
    private Float2[]? _uv2;
    private Color[]? _colors;
    private uint[] _indices = Array.Empty<uint>();
    private VirtualGeometryCluster[] _clusters = Array.Empty<VirtualGeometryCluster>();
    private VirtualGeometryClusterGroup[] _groups = Array.Empty<VirtualGeometryClusterGroup>();
    private VirtualGeometryLodLevel[] _levels = Array.Empty<VirtualGeometryLodLevel>();
    private AABB _bounds;
    private int _subMeshCount = 1;
    private int _sourceTriangleCount;
    private int _sourceVertexCount;

    public Float3[] Positions { get { EnsureNotDisposed(); return _positions; } set { EnsureNotDisposed(); _positions = value; } }
    public Float3[]? Normals { get { EnsureNotDisposed(); return _normals; } set { EnsureNotDisposed(); _normals = value; } }
    public Float4[]? Tangents { get { EnsureNotDisposed(); return _tangents; } set { EnsureNotDisposed(); _tangents = value; } }
    public Float2[]? UV { get { EnsureNotDisposed(); return _uv; } set { EnsureNotDisposed(); _uv = value; } }
    public Float2[]? UV2 { get { EnsureNotDisposed(); return _uv2; } set { EnsureNotDisposed(); _uv2 = value; } }
    public Color[]? Colors { get { EnsureNotDisposed(); return _colors; } set { EnsureNotDisposed(); _colors = value; } }
    public uint[] Indices { get { EnsureNotDisposed(); return _indices; } set { EnsureNotDisposed(); _indices = value; } }
    public VirtualGeometryCluster[] Clusters { get { EnsureNotDisposed(); return _clusters; } set { EnsureNotDisposed(); _clusters = value; } }
    public VirtualGeometryClusterGroup[] Groups { get { EnsureNotDisposed(); return _groups; } set { EnsureNotDisposed(); _groups = value; } }
    public VirtualGeometryLodLevel[] Levels { get { EnsureNotDisposed(); return _levels; } set { EnsureNotDisposed(); _levels = value; } }

    /// <summary>Local-space bounds over every level (matches the source mesh bounds).</summary>
    public AABB Bounds { get { EnsureNotDisposed(); return _bounds; } set { EnsureNotDisposed(); _bounds = value; } }
    public int SubMeshCount { get { EnsureNotDisposed(); return _subMeshCount; } set { EnsureNotDisposed(); _subMeshCount = value; } }
    public int SourceTriangleCount { get { EnsureNotDisposed(); return _sourceTriangleCount; } set { EnsureNotDisposed(); _sourceTriangleCount = value; } }
    public int SourceVertexCount { get { EnsureNotDisposed(); return _sourceVertexCount; } set { EnsureNotDisposed(); _sourceVertexCount = value; } }

    public bool HasNormals => _normals != null;
    public bool HasTangents => _tangents != null;
    public bool HasUV => _uv != null;
    public bool HasUV2 => _uv2 != null;
    public bool HasColors => _colors != null;

    public VirtualGeometryData() { }

    public void Serialize(ref EchoObject compoundTag, SerializationContext ctx)
    {
        compoundTag.Add("Bounds.Min.X", new(_bounds.Min.X));
        compoundTag.Add("Bounds.Min.Y", new(_bounds.Min.Y));
        compoundTag.Add("Bounds.Min.Z", new(_bounds.Min.Z));
        compoundTag.Add("Bounds.Max.X", new(_bounds.Max.X));
        compoundTag.Add("Bounds.Max.Y", new(_bounds.Max.Y));
        compoundTag.Add("Bounds.Max.Z", new(_bounds.Max.Z));
        compoundTag.Add("SubMeshCount", new(_subMeshCount));
        compoundTag.Add("SourceTriangleCount", new(_sourceTriangleCount));
        compoundTag.Add("SourceVertexCount", new(_sourceVertexCount));

        using MemoryStream memoryStream = new();
        using BinaryWriter writer = new(memoryStream);

        writer.Write(_positions.Length);
        foreach (var p in _positions) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }

        byte flags = (byte)((_normals != null ? 1 : 0) | (_tangents != null ? 2 : 0) | (_uv != null ? 4 : 0)
            | (_uv2 != null ? 8 : 0) | (_colors != null ? 16 : 0));
        writer.Write(flags);

        if (_normals != null)
        {
            writer.Write(_normals.Length);
            foreach (var n in _normals) { writer.Write(n.X); writer.Write(n.Y); writer.Write(n.Z); }
        }
        if (_tangents != null)
        {
            writer.Write(_tangents.Length);
            foreach (var t in _tangents) { writer.Write(t.X); writer.Write(t.Y); writer.Write(t.Z); writer.Write(t.W); }
        }
        if (_uv != null)
        {
            writer.Write(_uv.Length);
            foreach (var v in _uv) { writer.Write(v.X); writer.Write(v.Y); }
        }
        if (_uv2 != null)
        {
            writer.Write(_uv2.Length);
            foreach (var v in _uv2) { writer.Write(v.X); writer.Write(v.Y); }
        }
        if (_colors != null)
        {
            writer.Write(_colors.Length);
            foreach (var c in _colors) { writer.Write(c.R); writer.Write(c.G); writer.Write(c.B); writer.Write(c.A); }
        }

        writer.Write(_indices.Length);
        foreach (uint i in _indices) writer.Write(i);

        writer.Write(_clusters.Length);
        foreach (ref readonly var c in _clusters.AsSpan())
        {
            WriteAabb(writer, in c.Bounds);
            writer.Write(c.Error);
            writer.Write(c.IndexStart);
            writer.Write(c.IndexCount);
            writer.Write(c.VertexStart);
            writer.Write(c.VertexCount);
            writer.Write(c.ChildGroupsStart);
            writer.Write(c.ChildGroupsCount);
            writer.Write(c.SubMesh);
        }

        writer.Write(_groups.Length);
        foreach (ref readonly var g in _groups.AsSpan())
        {
            WriteAabb(writer, in g.Bounds);
            writer.Write(g.Error);
            writer.Write(g.ClusterStart);
            writer.Write(g.ClusterCount);
            writer.Write(g.ParentCluster);
            writer.Write(g.ParentClusterCount);
        }

        writer.Write(_levels.Length);
        foreach (ref readonly var l in _levels.AsSpan())
        {
            writer.Write(l.ClusterStart);
            writer.Write(l.ClusterCount);
            writer.Write(l.GroupStart);
            writer.Write(l.GroupCount);
            writer.Write(l.TriangleCount);
        }

        compoundTag.Add("VGData", new EchoObject(memoryStream.ToArray()));
    }

    public void Deserialize(EchoObject value, SerializationContext ctx)
    {
        _bounds = new AABB(
            new Float3(value["Bounds.Min.X"].FloatValue, value["Bounds.Min.Y"].FloatValue, value["Bounds.Min.Z"].FloatValue),
            new Float3(value["Bounds.Max.X"].FloatValue, value["Bounds.Max.Y"].FloatValue, value["Bounds.Max.Z"].FloatValue));
        _subMeshCount = value["SubMeshCount"].IntValue;
        _sourceTriangleCount = value["SourceTriangleCount"].IntValue;
        _sourceVertexCount = value["SourceVertexCount"].IntValue;

        using MemoryStream memoryStream = new(value["VGData"].ByteArrayValue);
        using BinaryReader reader = new(memoryStream);

        int positionCount = reader.ReadInt32();
        _positions = new Float3[positionCount];
        for (int i = 0; i < positionCount; i++)
            _positions[i] = new Float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        byte flags = reader.ReadByte();
        if ((flags & 1) != 0)
        {
            int n = reader.ReadInt32();
            _normals = new Float3[n];
            for (int i = 0; i < n; i++) _normals[i] = new Float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
        if ((flags & 2) != 0)
        {
            int n = reader.ReadInt32();
            _tangents = new Float4[n];
            for (int i = 0; i < n; i++) _tangents[i] = new Float4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
        if ((flags & 4) != 0)
        {
            int n = reader.ReadInt32();
            _uv = new Float2[n];
            for (int i = 0; i < n; i++) _uv[i] = new Float2(reader.ReadSingle(), reader.ReadSingle());
        }
        if ((flags & 8) != 0)
        {
            int n = reader.ReadInt32();
            _uv2 = new Float2[n];
            for (int i = 0; i < n; i++) _uv2[i] = new Float2(reader.ReadSingle(), reader.ReadSingle());
        }
        if ((flags & 16) != 0)
        {
            int n = reader.ReadInt32();
            _colors = new Color[n];
            for (int i = 0; i < n; i++) _colors[i] = new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        int indexCount = reader.ReadInt32();
        _indices = new uint[indexCount];
        for (int i = 0; i < indexCount; i++) _indices[i] = reader.ReadUInt32();

        int clusterCount = reader.ReadInt32();
        _clusters = new VirtualGeometryCluster[clusterCount];
        for (int i = 0; i < clusterCount; i++)
        {
            var c = new VirtualGeometryCluster
            {
                Bounds = ReadAabb(reader),
                Error = reader.ReadSingle(),
                IndexStart = reader.ReadInt32(),
                IndexCount = reader.ReadInt32(),
                VertexStart = reader.ReadInt32(),
                VertexCount = reader.ReadInt32(),
                ChildGroupsStart = reader.ReadInt32(),
                ChildGroupsCount = reader.ReadInt32(),
                SubMesh = reader.ReadInt32(),
            };
            _clusters[i] = c;
        }

        int groupCount = reader.ReadInt32();
        _groups = new VirtualGeometryClusterGroup[groupCount];
        for (int i = 0; i < groupCount; i++)
        {
            var g = new VirtualGeometryClusterGroup
            {
                Bounds = ReadAabb(reader),
                Error = reader.ReadSingle(),
                ClusterStart = reader.ReadInt32(),
                ClusterCount = reader.ReadInt32(),
                ParentCluster = reader.ReadInt32(),
                ParentClusterCount = reader.ReadInt32(),
            };
            _groups[i] = g;
        }

        int levelCount = reader.ReadInt32();
        _levels = new VirtualGeometryLodLevel[levelCount];
        for (int i = 0; i < levelCount; i++)
        {
            _levels[i] = new VirtualGeometryLodLevel
            {
                ClusterStart = reader.ReadInt32(),
                ClusterCount = reader.ReadInt32(),
                GroupStart = reader.ReadInt32(),
                GroupCount = reader.ReadInt32(),
                TriangleCount = reader.ReadInt32(),
            };
        }
    }

    private static void WriteAabb(BinaryWriter writer, in AABB aabb)
    {
        writer.Write(aabb.Min.X); writer.Write(aabb.Min.Y); writer.Write(aabb.Min.Z);
        writer.Write(aabb.Max.X); writer.Write(aabb.Max.Y); writer.Write(aabb.Max.Z);
    }

    private static AABB ReadAabb(BinaryReader reader)
        => new(new Float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
               new Float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
}
