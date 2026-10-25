// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;

namespace XEngine.Runtime;

/// <summary>Picks a tile from weighted entries. The same seed and cell always return the same pick.</summary>
[CreateAssetMenu("2D/Weighted Random Tile", Extension = ".weightedtile", Order = 64)]
public sealed class WeightedRandomTile : TileBase
{
    private readonly List<TileBase?> _tiles = [];
    private readonly List<int> _weights = [];

    public void Add(TileBase? tile, int weight)
    {
        _tiles.Add(tile);
        _weights.Add(Math.Max(weight, 0));
    }

    public TileBase? Pick(int seed, int x, int y)
    {
        int total = 0;
        for (int i = 0; i < _weights.Count; i++)
            total += _weights[i];
        if (total <= 0)
            return null;

        uint hash = (uint)(seed * 374761393 + x * 668265263 + y);
        hash ^= hash >> 13;
        hash *= 1274126177u;
        int roll = (int)(hash % (uint)total);
        int acc = 0;
        for (int i = 0; i < _weights.Count; i++)
        {
            acc += _weights[i];
            if (roll < acc)
                return _tiles[i];
        }

        return _tiles[_tiles.Count - 1];
    }
}
