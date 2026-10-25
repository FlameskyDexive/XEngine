// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace XEngine.Runtime.Navigation;

/// <summary>Built-in Recast area ids and default travel costs. Area 63 is Recast's walkable mask.</summary>
public static class NavMeshAreas
{
    public const int Ground = 0;
    public const int Water = 1;
    public const int Road = 2;
    public const int Jump = 5;
    public const int Walkable = 63;

    public const int FlagWalk = 0x01;
    public const int FlagSwim = 0x02;
    public const int FlagJump = 0x08;
    public const int FlagAll = 0xffff;

    public static List<NavMeshAreaInfo> DefaultAreas() =>
    [
        new NavMeshAreaInfo { Id = Ground, Name = "Walkable", Cost = 1f },
        new NavMeshAreaInfo { Id = Water, Name = "Water", Cost = 8f },
        new NavMeshAreaInfo { Id = Road, Name = "Road", Cost = 1f },
        new NavMeshAreaInfo { Id = Jump, Name = "Jump", Cost = 1.5f },
    ];
}

public sealed class NavMeshAreaInfo
{
    public int Id;
    public string Name = "";
    public float Cost = 1f;
}

public sealed class NavMeshTileSummary
{
    public int TileX;
    public int TileZ;
    public int PolyCount;
    public int VertCount;
}
