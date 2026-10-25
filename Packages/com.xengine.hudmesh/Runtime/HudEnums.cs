namespace XEngine.Hudmesh;

/// <summary>Fixed nameplate slots, bottom to top (port of UnityHUD's HudComponentEnum).</summary>
public enum HudSlot
{
    /// <summary>Health bar.</summary>
    Blood = 0,
    /// <summary>Entity name.</summary>
    Name = 1,
    /// <summary>Title line.</summary>
    Title = 2,
    /// <summary>Guild / faction name.</summary>
    GuildName = 3,
    /// <summary>Guild / faction icon.</summary>
    GuildIcon = 4,
    /// <summary>Slot count.</summary>
    Total = 5,
}

/// <summary>
/// Style selector used as an index into per-relation config arrays. Treat it as an int —
/// the names follow UnityHUD's HUDRelationEnum, not necessarily their literal meaning.
/// </summary>
public enum HudRelation
{
    Self = 0,
    Team = 1,
    Friend = 2,
    Npc = 3,
    Enemy = 4,
    Monster0 = 5,
    Monster1 = 6,
    Monster2 = 7,
    Monster3 = 8,
    Monster4 = 9,
    All = 10,
}

/// <summary>Horizontal alignment of a widget inside its slot, in pixels around the anchor.</summary>
public enum HudAlignment
{
    Left = 0,
    Middle = 1,
    Right = 2,
}

/// <summary>Axis a sliced fill shrinks along (UnityHUD SliceTypeEnum; the combined axis is not used by fills).</summary>
public enum HudFillAxis
{
    Horizontal = 0,
    Vertical = 1,
}
