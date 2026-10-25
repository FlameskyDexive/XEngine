// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.CompilerServices;

using XEngine.Runtime.Navigation;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Terrain;

/// <summary>
/// Rasterizes a <see cref="TerrainComponent"/> heightmap into Recast triangles.
/// Registered at Runtime assembly load so the navmesh bake pipeline stays terrain-agnostic.
/// </summary>
public sealed class TerrainNavMeshGeometryCollector : INavMeshGeometryCollector
{
    public static readonly TerrainNavMeshGeometryCollector Instance = new();

    [ModuleInitializer]
    internal static void Register() => NavMeshGeometryCollectors.Register(Instance);

    public bool TryCollect(GameObject go, INavMeshTriangleSink geom)
    {
        TerrainComponent? terrain = go.GetComponent<TerrainComponent>();
        if (terrain == null)
            return false;

        CollectTerrain(geom, terrain);
        return true;
    }

    internal static void CollectTerrain(INavMeshTriangleSink geom, TerrainComponent terrain)
    {
        TerrainData? data = terrain.Data.Res;
        if (data == null)
            return;

        int res = data.HeightmapResolution;
        if (res < 2)
            return;

        float size = data.Size;
        float height = data.Height;
        float cell = size / (res - 1);
        Float4x4 world = terrain.Transform.LocalToWorldMatrix;

        for (int z = 0; z < res - 1; z++)
        {
            for (int x = 0; x < res - 1; x++)
            {
                if (data.IsCellHole(x, z))
                    continue;
                Float3 v00 = Float4x4.TransformPoint(new Float3(x * cell, data.GetHeight(x, z) * height, z * cell), world);
                Float3 v10 = Float4x4.TransformPoint(new Float3((x + 1) * cell, data.GetHeight(x + 1, z) * height, z * cell), world);
                Float3 v01 = Float4x4.TransformPoint(new Float3(x * cell, data.GetHeight(x, z + 1) * height, (z + 1) * cell), world);
                Float3 v11 = Float4x4.TransformPoint(new Float3((x + 1) * cell, data.GetHeight(x + 1, z + 1) * height, (z + 1) * cell), world);
                geom.AddTriangle(v00, v10, v11);
                geom.AddTriangle(v00, v11, v01);
            }
        }
    }
}
