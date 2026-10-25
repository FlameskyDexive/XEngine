using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>
/// Public per-entity handle (UnityHUD HUDInfo port). Obtain one from
/// <see cref="HudManager.CreateHud"/>, attach it to a transform, drive its slots, and
/// <see cref="Release"/> it when the entity goes away. All heavy state lives in the
/// internal <see cref="HudObject"/>.
/// </summary>
public sealed partial class Hud
{
    public long Id { get; }

    internal HudObject? Object;
    internal HudManager? Manager;

    internal Hud(long id)
    {
        Id = id;
    }

    /// <summary>Attaches the HUD to <paramref name="target"/>'s position plus <paramref name="worldOffset"/>.</summary>
    public Hud Init(Transform target, Float3 worldOffset) => Init(target, worldOffset, Float2.Zero);

    /// <summary>Attaches the HUD with an additional pixel-space stack offset (e.g. off-center nameplates).</summary>
    public Hud Init(Transform target, Float3 worldOffset, Float2 pixelOffset)
    {
        Manager?.Attach(this, target, worldOffset, pixelOffset);
        return this;
    }

    /// <summary>
    /// Shows a sprite in a slot (e.g. the guild icon). Pass an empty
    /// <paramref name="spriteName"/> or an unknown name to hide the slot.
    /// <paramref name="relation"/> selects the per-relation style once config assets land.
    /// </summary>
    public void SetIcon(HudSlot slot, string spriteName, HudRelation relation = HudRelation.Self,
        HudAlignment align = HudAlignment.Middle)
    {
        if (Manager is null)
            return;
        HudSpriteInfo? sprite = spriteName.Length == 0 ? null : Manager.Sprites.Get(spriteName);
        Object?.SetSprite(slot, sprite, align);
    }

    /// <summary>Shows a nine-sliced sprite stretched to <paramref name="size"/> pixels (bar backgrounds).</summary>
    public void SetSlicedIcon(HudSlot slot, string spriteName, Float2 size,
        HudRelation relation = HudRelation.Self, HudAlignment align = HudAlignment.Middle)
    {
        if (Manager is null)
            return;
        HudSpriteInfo? sprite = spriteName.Length == 0 ? null : Manager.Sprites.Get(spriteName);
        Object?.SetSlicedSprite(slot, sprite, size, align);
    }

    /// <summary>Hides or re-shows a slot; the stack above it reflows.</summary>
    public void SetSlotVisible(HudSlot slot, bool visible) => Object?.SetSlotVisible(slot, visible);

    /// <summary>
    /// Shows SDF text in a slot (name, title, guild name). Empty text hides the slot.
    /// Text is depth-tested against the scene (UnityHUD parity) and always uses the shared
    /// font atlas. Per-relation styles arrive with config assets.
    /// </summary>
    public void SetText(HudSlot slot, string text, HudRelation relation = HudRelation.Self,
        HudAlignment align = HudAlignment.Middle)
        => Object?.SetText(slot, text, align);

    public bool IsSlotVisible(HudSlot slot) => Object?.IsSlotVisible(slot) ?? false;

    /// <summary>Detaches and releases this HUD back to the manager's pool.</summary>
    public void Release() => Manager?.DeleteHud(Id);
}
