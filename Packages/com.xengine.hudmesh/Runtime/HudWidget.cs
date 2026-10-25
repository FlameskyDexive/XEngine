using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>Camera basis pre-scaled to world units per pixel; captured once per camera per frame.</summary>
internal struct HudFillContext
{
    public Float3 RightStep;
    public Float3 UpStep;
}

/// <summary>
/// Base class of every HUD element. Geometry is authored as pixel-space quads and expanded
/// against the camera basis at fill time, so a widget never needs to know which camera is
/// looking at it. Widgets are pooled between <see cref="HudObject"/>s.
/// </summary>
[Pool]
internal abstract class HudWidget
{
    /// <summary>Stack offset in pixels (managed by <see cref="HudObject.Reflow"/>).</summary>
    public Float2 Offset;

    /// <summary>World-space offset from the role anchor (typically the head-top height).</summary>
    public Float3 RoleOffset;

    /// <summary>Pixel size of the widget's layout box (drives the stack).</summary>
    public Float2 Size;

    /// <summary>Extra vertical gap this widget contributes to whatever sits above it.</summary>
    public float ItemLineGap;

    public float Scale = 1f;

    /// <summary>Combined-mesh slot: 0 = SDF font, 1..N = sprite textures.</summary>
    public int MaterialSlot;

    public bool IsValid;

    /// <summary>World anchor, refreshed by <see cref="HudObject"/> whenever the target moves.</summary>
    public Float3 Anchor;

    /// <summary>Per-frame advance for animated widgets (numbers, bubbles). Time is manager-supplied.</summary>
    public virtual void Tick(float now) { }

    public abstract void Fill(List<HudMeshData?> targets, in HudFillContext context);

    public virtual void Release()
    {
        IsValid = false;
        Offset = Float2.Zero;
        RoleOffset = Float3.Zero;
        Size = Float2.Zero;
        ItemLineGap = 0f;
        Scale = 1f;
        MaterialSlot = 0;
    }
}
