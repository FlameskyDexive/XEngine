// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

/// <summary>
/// Paints a tile onto a <see cref="Tilemap"/> as a rectangle of cell indices or an axial hex disk.
/// Grid layout only changes world pose; fill happens in cell space.
/// </summary>
public sealed class GridBrush
{
    public TileBase? Tile;

    public void Paint(Tilemap map, int x, int y)
    {
        map?.SetTile(x, y, Tile);
    }

    public void FillRect(Tilemap map, int x0, int y0, int x1, int y1)
    {
        if (map == null)
            return;

        int minX = Math.Min(x0, x1);
        int maxX = Math.Max(x0, x1);
        int minY = Math.Min(y0, y1);
        int maxY = Math.Max(y0, y1);
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
                map.SetTile(x, y, Tile);
        }
    }

    /// <summary>Paints every axial cell whose cube distance from (<paramref name="cx"/>, <paramref name="cy"/>) is ≤ <paramref name="radius"/>.</summary>
    public void FillHex(Tilemap map, int cx, int cy, int radius)
    {
        if (map == null)
            return;

        radius = Math.Max(radius, 0);
        for (int q = -radius; q <= radius; q++)
        {
            for (int r = -radius; r <= radius; r++)
            {
                int s = -q - r;
                int aq = q < 0 ? -q : q;
                int ar = r < 0 ? -r : r;
                int asAbs = s < 0 ? -s : s;
                int dist = aq;
                if (ar > dist)
                    dist = ar;
                if (asAbs > dist)
                    dist = asAbs;
                if (dist <= radius)
                    map.SetTile(cx + q, cy + r, Tile);
            }
        }
    }
}
