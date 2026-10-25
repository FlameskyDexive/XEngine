// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Echo;

namespace XEngine.Runtime;

/// <summary>Picks an output tile from neighbor rules. First matching rule wins.</summary>
[CreateAssetMenu("2D/Rule Tile", Extension = ".ruletile", Order = 62)]
public sealed class RuleTile : TileBase
{
    public enum Neighbor
    {
        DontCare = 0,
        This = 1,
        NotThis = 2
    }

    public struct Rule
    {
        public Neighbor North;
        public Neighbor South;
        public Neighbor East;
        public Neighbor West;
        public TileBase? Output;
    }

    public TileBase? DefaultTile;
    public List<Rule> Rules = [];

    public TileBase? Resolve(Tilemap map, int x, int y)
    {
        if (map == null)
            return DefaultTile;

        for (int i = 0; i < Rules.Count; i++)
        {
            Rule rule = Rules[i];
            if (Matches(rule.North, map.GetTile(x, y + 1)) &&
                Matches(rule.South, map.GetTile(x, y - 1)) &&
                Matches(rule.East, map.GetTile(x + 1, y)) &&
                Matches(rule.West, map.GetTile(x - 1, y)))
                return rule.Output;
        }

        return DefaultTile;
    }

    private bool Matches(Neighbor neighbor, TileBase? tile)
    {
        return neighbor switch
        {
            Neighbor.This => ReferenceEquals(tile, this),
            Neighbor.NotThis => !ReferenceEquals(tile, this),
            _ => true
        };
    }
}
