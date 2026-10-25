using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>Internal per-slot APIs shared by the public <see cref="Hud"/> handle.</summary>
public sealed partial class Hud
{
    /// <summary>
    /// Shows the health bar with the bundled default styling: 180×30 background
    /// (<c>Blood_bg</c>) + 160×13 fill at (10,10), horizontal fill. The fill art varies by
    /// relation (<c>_Fill_1..4</c>) exactly like UnityHUD's BloodRelationArray. Replaced by
    /// config-driven styles once config assets land.
    /// </summary>
    public void SetBloodVisible(bool show, HudRelation relation = HudRelation.Self)
    {
        if (Manager is null)
            return;
        if (!show)
        {
            Object?.SetSlotVisible(HudSlot.Blood, false);
            return;
        }

        HudBloodStyle style = Manager.Config.Blood;
        HudSpriteInfo? background = Manager.Sprites.Get(style.BackgroundSprite);
        int fillIndex = int.Clamp((int)relation + 1, 1, 4);
        HudSpriteInfo? fill = Manager.Sprites.Get(style.FillSpriteBase + fillIndex);
        Object?.SetBar(HudSlot.Blood, background, fill,
            style.BarSize, style.FillSize, style.FillOffset,
            style.Axis, style.Reverse, style.Align);
    }

    /// <summary>Updates the blood bar fill (0..1). No-op when the bar is hidden.</summary>
    public void SetBloodPercent(float percent) => Object?.SetBarPercent(HudSlot.Blood, percent);

    /// <summary>
    /// Spawns a floating damage number in <paramref name="styleIndex"/>'s look at a
    /// camera-space offset from the role position (UnityHUD HurtNumber semantics).
    /// </summary>
    public void PopNumber(int styleIndex, int number, Float3 cameraOffset)
    {
        if (Manager is null || Object is null)
            return;
        if (styleIndex < 0 || styleIndex >= Manager.NumberStyles.Count)
            return;
        Object.PopNumber(Manager.NumberStyles[styleIndex], number, cameraOffset);
    }

    /// <summary>Shows a chat bubble above the nameplate stack (style index is reserved for config assets).</summary>
    public void Talk(int styleIndex, string content) => Object?.PushTalk(content);
}
