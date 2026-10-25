// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// On-screen virtual joystick for touch/mouse play (battle HUD). The node itself is a
/// transparent touch zone; pressing anywhere inside activates the stick — the base ring and
/// thumb appear AT the press point (dynamic origin, the mobile battle convention from the
/// MagicCreator DlgBattle E_Control) and disappear on release. While held, <see cref="Value"/>
/// is the dead-zoned drag offset; <see cref="OnChanged"/> fires on change and
/// <see cref="OnReleased"/> on release. The visuals are serialized child references
/// (<see cref="BaseRect"/>/<see cref="ThumbRect"/>) authored into the scene/prefab by the HUD
/// builder — sprites and pixel sizes travel with the scene, not code.
/// Pointer math uses <see cref="PointerEventData.DesignPosition"/> (design px, +Y up) so the
/// drag direction matches screen space and values are scale-independent.
/// </summary>
[AddComponentMenu("UI/Joystick")]
public sealed class Joystick : UIBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    /// <summary>Normalized dead zone: output stays zero until the drag exceeds this fraction.</summary>
    public float DeadZone = 0.12f;

    /// <summary>When true (default) the stick appears where the finger lands; when false it stays
    /// centered at the zone's midpoint (console-style fixed stick).</summary>
    public bool DynamicOrigin = true;

    /// <summary>Drag distance (as a fraction of the shorter screen side) that maps to magnitude 1.</summary>
    public float MaxDragFraction = 0.22f;

    /// <summary>Base ring rect: moved to the press point, shown while active. Anchor it to this
    /// zone's TOP-LEFT (0,1) so <see cref="RectTransform.AnchoredPosition"/> maps 1:1 to the
    /// canvas-design pointer offset. Assigned by the HUD builder; a plain default is built when null.</summary>
    public RectTransform? BaseRect;

    /// <summary>Thumb knob rect: follows the finger, clamped to the ring radius. See <see cref="BaseRect"/>.</summary>
    public RectTransform? ThumbRect;

    /// <summary>Current normalized value (dead zone applied, magnitude ≤ 1). Zero while idle.</summary>
    public Float2 Value { get; private set; }

    /// <summary>True between press and release.</summary>
    public bool IsActive { get; private set; }

    public event Action<Float2>? OnChanged;
    public event Action? OnReleased;

    private Float2 _pressDesign;
    private Float2 _baseLocal;

    /// <summary>True: the touch zone participates in pointer hit testing even though it has no
    /// geometry of its own (the base class only counts Graphics — a bare zone would never be
    /// hit and the joystick would be dead).</summary>
    public override bool IsRaycastCandidate => true;

    /// <summary>The zone itself draws nothing; visuals are child Image nodes.</summary>
    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }

    public override void OnAddedToScene()
    {
        base.OnAddedToScene();
        // Builder-assigned visuals win; a bare control gets plain defaults so it remains usable.
        if (BaseRect == null || ThumbRect == null)
        {
            BaseRect ??= CreateDefaultChild("Base", 220f, 0.25f);
            ThumbRect ??= CreateDefaultChild("Thumb", 95f, 0.5f);
        }
        // The stick only appears on press — hide the authored/default visuals at rest.
        SetVisualsActive(false);
    }

    private RectTransform CreateDefaultChild(string name, float sizePx, float alpha)
    {
        var go = new GameObject(name);
        go.SetParent(GameObject, worldPositionStays: false);
        var rect = go.EnsureRectTransform();
        rect.AnchorMin = new Float2(0f, 1f);
        rect.AnchorMax = new Float2(0f, 1f);
        rect.Pivot = new Float2(0.5f, 0.5f);
        rect.SizeDelta = new Float2(sizePx, sizePx);
        rect.AnchoredPosition = Float2.Zero;
        var image = go.AddComponent<Image>();
        image.Color = new Color(1f, 1f, 1f, alpha);
        go.AddComponent<CanvasRenderer>();
        return rect;
    }

    private void SetVisualsActive(bool active)
    {
        if (BaseRect?.GameObject is { } baseGo) baseGo.Enabled = active;
        if (ThumbRect?.GameObject is { } thumbGo) thumbGo.Enabled = active;
    }

    public void OnPointerDown(PointerEventData e)
    {
        IsActive = true;
        _pressDesign = e.DesignPosition;

        // Zone-local position of the press (design px, origin at the zone's top-left corner —
        // matches the visuals' (0,1) top-left anchors).
        Float2 local = ZoneLocal(e.DesignPosition);
        if (DynamicOrigin)
        {
            _baseLocal = local;
            PlaceVisuals();
        }
        else
        {
            _baseLocal = ZoneCenterLocal();
            PlaceVisuals();
        }

        SetVisualsActive(true);
        OnDrag(e);
    }

    public void OnDrag(PointerEventData e)
    {
        if (!IsActive) return;

        float maxDrag = MaxDragPixels();
        // Design space is +Y up — the same orientation as UI and world up: screen-up drags
        // produce +Y, exactly what a "move forward" stick should report.
        Float2 offset = e.DesignPosition - _pressDesign;
        float length = Float2.Length(offset);

        Float2 value = length > maxDrag ? offset * (1f / length) : offset / MathF.Max(maxDrag, 1f);
        if (Float2.Length(value) < DeadZone)
            value = Float2.Zero;

        if (value != Value)
        {
            Value = value;
            OnChanged?.Invoke(Value);
        }

        Float2 clamped = length > maxDrag ? offset * (maxDrag / length) : offset;
        if (ThumbRect != null)
        {
            // Top-left anchor: +X right, −Y down from the zone's top edge.
            ThumbRect.AnchoredPosition = new Float2(
                _baseLocal.X + clamped.X,
                _baseLocal.Y + clamped.Y);
        }
    }

    public void OnPointerUp(PointerEventData e)
    {
        Release();
    }

    /// <summary>Programmatic release (play mode exit, focus loss).</summary>
    public void Release()
    {
        if (!IsActive) return;
        IsActive = false;
        Value = Float2.Zero;
        SetVisualsActive(false);
        OnReleased?.Invoke();
    }

    private void PlaceVisuals()
    {
        if (BaseRect != null)
            BaseRect.AnchoredPosition = _baseLocal;
        if (ThumbRect != null)
            ThumbRect.AnchoredPosition = _baseLocal;
    }

    /// <summary>Canvas-design point → zone-local point relative to the zone's top-left corner,
    /// expressed in the (0,1)-anchor convention: +X right, −Y down.</summary>
    private Float2 ZoneLocal(Float2 designPoint)
    {
        Rect zone = RectTransform.ComputedRect;
        return new Float2(designPoint.X - zone.Min.X, designPoint.Y - zone.Max.Y);
    }

    private Float2 ZoneCenterLocal()
    {
        Rect zone = RectTransform.ComputedRect;
        return new Float2(zone.Size.X * 0.5f, -zone.Size.Y * 0.5f);
    }

    private float MaxDragPixels()
    {
        Rect zone = RectTransform.ComputedRect;
        float minSide = MathF.Min(zone.Size.X, zone.Size.Y);
        return MathF.Max(60f, minSide * MaxDragFraction);
    }
}
