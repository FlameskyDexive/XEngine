// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using DotRecast.Core.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

/// <summary>
/// Combined triangle soup for Recast. Vertices are world-space XEngine positions (no axis flip).
/// Triangle indices are stored in Recast winding (input winding reversed).
/// </summary>
public sealed class NavMeshInputGeometry : IRcInputGeomProvider, INavMeshTriangleSink
{
    private readonly List<float> _verts = [];
    private readonly List<int> _faces = [];
    private readonly List<RcConvexVolume> _volumes = [];
    private readonly List<RcOffMeshConnection> _offMesh = [];
    private RcTriMesh? _mesh;
    private RcVec3f _bmin;
    private RcVec3f _bmax;
    private bool _hasBounds;

    public int TriangleCount => _faces.Count / 3;
    public int VertexCount => _verts.Count / 3;

    public void AddTriangle(Float3 a, Float3 b, Float3 c)
    {
        int i = _verts.Count / 3;
        Append(a);
        Append(b);
        Append(c);
        // Reverse winding so Recast's right-handed slope test matches XEngine's left-handed meshes.
        _faces.Add(i);
        _faces.Add(i + 2);
        _faces.Add(i + 1);
        _mesh = null;
    }

    public void AddMesh(Float3[] vertices, uint[] indices, Float4x4 world)
    {
        if (vertices == null || indices == null) return;
        int limit = indices.Length - 2;
        for (int i = 0; i < limit; i += 3)
        {
            uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
            if (i0 >= (uint)vertices.Length || i1 >= (uint)vertices.Length || i2 >= (uint)vertices.Length)
                continue;
            Float3 a = Float4x4.TransformPoint(vertices[i0], world);
            Float3 b = Float4x4.TransformPoint(vertices[i1], world);
            Float3 c = Float4x4.TransformPoint(vertices[i2], world);
            AddTriangle(a, b, c);
        }
    }

    public RcTriMesh GetMesh() => EnsureMesh();

    public RcVec3f GetMeshBoundsMin()
    {
        EnsureMesh();
        return _bmin;
    }

    public RcVec3f GetMeshBoundsMax()
    {
        EnsureMesh();
        return _bmax;
    }

    public IEnumerable<RcTriMesh> Meshes()
    {
        yield return EnsureMesh();
    }

    public void AddConvexVolume(RcConvexVolume convexVolume) => _volumes.Add(convexVolume);

    public IList<RcConvexVolume> ConvexVolumes() => _volumes;

    public List<RcOffMeshConnection> GetOffMeshConnections() => _offMesh;

    public void AddOffMeshConnection(RcVec3f start, RcVec3f end, float radius, bool bidir, int area, int flags)
    {
        _offMesh.Add(new RcOffMeshConnection(start, end, radius, bidir, area, flags));
    }

    public void RemoveOffMeshConnections(Predicate<RcOffMeshConnection> filter) => _offMesh.RemoveAll(filter);

    private void Append(Float3 v)
    {
        _verts.Add(v.X);
        _verts.Add(v.Y);
        _verts.Add(v.Z);
        var rv = new RcVec3f(v.X, v.Y, v.Z);
        if (!_hasBounds)
        {
            _bmin = rv;
            _bmax = rv;
            _hasBounds = true;
        }
        else
        {
            _bmin = RcVec3f.Min(_bmin, rv);
            _bmax = RcVec3f.Max(_bmax, rv);
        }
    }

    private RcTriMesh EnsureMesh()
    {
        if (_mesh != null) return _mesh;
        if (_verts.Count < 9 || _faces.Count < 3)
            throw new InvalidOperationException("NavMesh bake has no triangles.");
        _mesh = new RcTriMesh(_verts.ToArray(), _faces.ToArray());
        return _mesh;
    }
}
