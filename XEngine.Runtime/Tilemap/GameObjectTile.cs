// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>Spawns a named child under the tilemap at the cell's local origin.</summary>
[CreateAssetMenu("2D/GameObject Tile", Extension = ".gameobjecttile", Order = 65)]
public sealed class GameObjectTile : TileBase
{
    public string PrefabName = "GameObject";

    public GameObject? InstantiateAt(Tilemap map, int x, int y)
    {
        if (map?.GameObject == null)
            return null;

        string name = string.IsNullOrEmpty(PrefabName) ? "GameObject" : PrefabName;
        var instance = new GameObject(name);
        instance.SetParent(map.GameObject, worldPositionStays: false);

        Grid? grid = map.GetComponentInParent<Grid>(includeSelf: true);
        instance.Transform.LocalPosition = grid != null
            ? grid.CellToLocal(x, y)
            : new Float3(x, y, 0f);
        return instance;
    }
}
