// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

/// <summary>
/// Recast-free triangle sink used by optional NavMesh packages. Core Runtime must not reference
/// Recast vendor types; the AI Navigation package implements this with its bake geometry buffer.
/// </summary>
public interface INavMeshTriangleSink
{
    void AddTriangle(Float3 a, Float3 b, Float3 c);
}

/// <summary>
/// Optional scene-geometry source for navmesh baking. Packages register collectors so the bake
/// pipeline does not take a compile-time dependency on Terrain (or other heightfield types).
/// Returning <see langword="true"/> consumes the GameObject and skips the default MeshRenderer /
/// MeshCollider path.
/// </summary>
public interface INavMeshGeometryCollector
{
    bool TryCollect(GameObject go, INavMeshTriangleSink geom);
}

/// <summary>Process-wide registry of <see cref="INavMeshGeometryCollector"/> implementations.</summary>
public static class NavMeshGeometryCollectors
{
    private static readonly List<INavMeshGeometryCollector> Collectors = [];
    private static readonly object Gate = new();

    public static int Count
    {
        get
        {
            lock (Gate)
                return Collectors.Count;
        }
    }

    public static void Register(INavMeshGeometryCollector collector)
    {
        ArgumentNullException.ThrowIfNull(collector);
        lock (Gate)
        {
            if (!Collectors.Contains(collector))
                Collectors.Add(collector);
        }
    }

    public static void Unregister(INavMeshGeometryCollector collector)
    {
        if (collector == null)
            return;
        lock (Gate)
            Collectors.Remove(collector);
    }

    public static bool TryCollect(GameObject go, INavMeshTriangleSink geom)
    {
        ArgumentNullException.ThrowIfNull(go);
        ArgumentNullException.ThrowIfNull(geom);
        lock (Gate)
        {
            for (int i = 0; i < Collectors.Count; i++)
            {
                if (Collectors[i].TryCollect(go, geom))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Hot-reload: drop collectors whose implementation type belongs to the collectible script
    /// AssemblyLoadContext so the registry cannot pin it across a reload. Engine/package
    /// implementations (default context) are kept.
    /// </summary>
    [XEngine.Runtime.OnAssemblyUnload]
    private static void RemoveCollectibleForReload()
    {
        lock (Gate)
        {
            Collectors.RemoveAll(static c => c.GetType().Assembly.IsCollectible);
        }
    }
}
