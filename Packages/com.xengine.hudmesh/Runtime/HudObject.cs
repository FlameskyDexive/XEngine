using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Vector;
namespace XEngine.Hudmesh;

/// <summary>
/// Per-entity HUD: the five fixed nameplate slots plus dynamic widgets (damage numbers,
/// bubbles — later milestones). Owns stack layout, target position tracking and widget
/// recycling (UnityHUD HUDObject port).
/// </summary>
[Pool]
internal sealed class HudObject
{
    public const float PositionEpsilon = 0.00001f;

    /// <summary>Sprite-slot line gap used when no manager supplies one (headless/tests; matches HudManager's default).</summary>
    public const float DefaultSpriteLineGap = 12f;

    public long Id;
    public double SortKey;
    public bool HasVisibleContent;

    private HudManager? _manager;
    private Transform? _target;
    private Float3 _roleWorldOffset;
    private Float2 _pixelOffset;
    private Float3 _rolePos;
    private bool _initialized;

    internal readonly HudWidget?[] Slots = new HudWidget?[(int)HudSlot.Total];
    private readonly List<HudWidget> _dynamic = new();

    public Float3 RolePos => _rolePos;

    public void Init(HudManager manager, long id, Transform target, Float3 worldOffset, Float2 pixelOffset)
    {
        _manager = manager;
        _target = target;
        _rolePos = target.Position;
        InitCommon(id, worldOffset, pixelOffset);
    }

    /// <summary>Headless initialization for pure-layout tests (no manager, no target).</summary>
    internal void InitHeadless(Float2 pixelOffset)
        => InitCommon(0, Float3.Zero, pixelOffset);

    private void InitCommon(long id, Float3 worldOffset, Float2 pixelOffset)
    {
        Id = id;
        _roleWorldOffset = worldOffset;
        _pixelOffset = pixelOffset;
        _initialized = true;
        RefreshAnchors();
    }

    public void Tick(float now)
    {
        if (!_initialized)
            return;

        if (_target is not null)
        {
            Float3 position = _target.Position;
            float dx = position.X - _rolePos.X;
            float dy = position.Y - _rolePos.Y;
            float dz = position.Z - _rolePos.Z;
            if (dx * dx + dy * dy + dz * dz > PositionEpsilon * PositionEpsilon)
            {
                _rolePos = position;
                RefreshAnchors();
            }
        }

        for (int i = 0; i < _dynamic.Count; i++)
            _dynamic[i].Tick(now);

        for (int i = _dynamic.Count - 1; i >= 0; i--)
        {
            HudWidget widget = _dynamic[i];
            if (!widget.IsValid)
            {
                widget.Release();
                Recycle(widget);
                _dynamic.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Spawns a floating number at the role position plus a camera-space offset
    /// (UnityHUD PushNumber semantics; degrades to a world offset without a camera).
    /// </summary>
    public void PopNumber(HudNumberStyle style, int number, Float3 cameraOffset)
    {
        if (_manager is null || style is null)
            return;

        Float3 spawnPos = _rolePos + _roleWorldOffset;
        Camera? camera = _manager.ResolveCamera();
        if (camera is not null)
        {
            Transform camTransform = camera.Transform;
            spawnPos += camTransform.Right * cameraOffset.X
                      + camTransform.Up * cameraOffset.Y
                      + camTransform.Forward * cameraOffset.Z;
        }
        else
        {
            spawnPos += cameraOffset;
        }

        HudNumberWidget widget = HudPool<HudNumberWidget>.Pop();
        widget.Push(style, number, _manager.Sprites, spawnPos, Float3.Zero, Time.TimeSinceStartup);
        _dynamic.Add(widget);
        HasVisibleContent = true;
    }

    /// <summary>
    /// Shows a chat bubble above the whole stack. An existing bubble is marked
    /// ReadyRecycle so it fades out on the accelerated window (UnityHUD PushTalk).
    /// </summary>
    public void PushTalk(string content)
    {
        if (_manager is null || string.IsNullOrEmpty(content))
            return;

        for (int i = 0; i < _dynamic.Count; i++)
        {
            if (_dynamic[i] is HudTalkWidget existing && existing.IsValid)
            {
                existing.ReadyRecycle = true;
                break;
            }
        }

        float now = Time.TimeSinceStartup;
        HudTalkWidget widget = HudPool<HudTalkWidget>.Pop();
        widget.ItemLineGap = _manager.Config.Talk.LineGap;
        widget.RoleOffset = _roleWorldOffset;
        widget.PushTalk(_manager.Config.Talk, _manager.Sprites, content, now);
        widget.Anchor = _rolePos + widget.RoleOffset;
        widget.Offset = new Float2(_pixelOffset.X, HudStacking.TotalHeight(Slots) + widget.ItemLineGap);
        _dynamic.Add(widget);
        HasVisibleContent = true;
    }

    /// <summary>Recomputes every valid slot's stack offset (UnityHUD's incremental MeshHide, from scratch).</summary>
    public void Reflow()
    {
        HasVisibleContent = false;
        for (int i = 0; i < Slots.Length; i++)
        {
            HudWidget? widget = Slots[i];
            if (widget is not { IsValid: true })
                continue;
            widget.Offset = new Float2(_pixelOffset.X, HudStacking.StackHeight(Slots, i));
            HasVisibleContent = true;
        }
        if (_dynamic.Count > 0)
            HasVisibleContent = true;
    }

    public void RefreshAnchors()
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            HudWidget? widget = Slots[i];
            if (widget is not null)
                widget.Anchor = _rolePos + widget.RoleOffset;
        }
        for (int i = 0; i < _dynamic.Count; i++)
        {
            HudWidget widget = _dynamic[i];
            if (widget.IsValid)
                widget.Anchor = _rolePos + widget.RoleOffset;
        }
    }

    // ----- slot APIs (called by the public Hud handle) -----

    public void SetSprite(HudSlot slot, HudSpriteInfo? sprite, HudAlignment align)
    {
        int index = (int)slot;
        if (sprite is null)
        {
            SetSlotVisible(slot, false);
            return;
        }

        HudSpriteWidget widget = Slots[index] as HudSpriteWidget ?? CreateSlot<HudSpriteWidget>(index);
        widget.ItemLineGap = _manager?.SpriteLineGap ?? DefaultSpriteLineGap;
        widget.RoleOffset = _roleWorldOffset;
        if (sprite.MaterialSlot < 0 && _manager is not null)
            sprite.MaterialSlot = _manager.ResolveMaterialSlot(sprite);
        widget.PushSprite(sprite, align);
        widget.Anchor = _rolePos + widget.RoleOffset;
        Reflow();
    }

    public void SetSlicedSprite(HudSlot slot, HudSpriteInfo? sprite, Float2 size, HudAlignment align)
    {
        int index = (int)slot;
        if (sprite is null)
        {
            SetSlotVisible(slot, false);
            return;
        }

        HudSpriteWidget widget = Slots[index] as HudSpriteWidget ?? CreateSlot<HudSpriteWidget>(index);
        widget.ItemLineGap = _manager?.SpriteLineGap ?? DefaultSpriteLineGap;
        widget.RoleOffset = _roleWorldOffset;
        if (sprite.MaterialSlot < 0 && _manager is not null)
            sprite.MaterialSlot = _manager.ResolveMaterialSlot(sprite);
        widget.PushSlicedSprite(sprite, size, align);
        widget.Anchor = _rolePos + widget.RoleOffset;
        Reflow();
    }

    public void SetSlotVisible(HudSlot slot, bool visible)
    {
        HudWidget? widget = Slots[(int)slot];
        if (widget is null || widget.IsValid == visible)
            return;
        widget.IsValid = visible;
        Reflow();
    }

    public bool IsSlotVisible(HudSlot slot) => Slots[(int)slot] is { IsValid: true };

    public HudSpriteWidget? GetSpriteWidget(HudSlot slot) => Slots[(int)slot] as HudSpriteWidget;

    public void SetBar(HudSlot slot, HudSpriteInfo? background, HudSpriteInfo? fill, Float2 barSize,
        Float2 fillSize, Float2 fillOffset, HudFillAxis axis, bool reverse, HudAlignment align)
    {
        int index = (int)slot;
        if (background is null || fill is null)
        {
            SetSlotVisible(slot, false);
            return;
        }

        HudBarWidget widget = Slots[index] as HudBarWidget ?? CreateSlot<HudBarWidget>(index);
        widget.ItemLineGap = _manager?.SpriteLineGap ?? DefaultSpriteLineGap;
        widget.RoleOffset = _roleWorldOffset;
        if (_manager is not null)
        {
            if (background.MaterialSlot < 0)
                background.MaterialSlot = _manager.ResolveMaterialSlot(background);
            if (fill.MaterialSlot < 0)
                fill.MaterialSlot = _manager.ResolveMaterialSlot(fill);
        }
        widget.PushBar(background, fill, barSize, fillSize, fillOffset, axis, reverse, align);
        widget.Anchor = _rolePos + widget.RoleOffset;
        Reflow();
    }

    public void SetBarPercent(HudSlot slot, float percent)
        => (Slots[(int)slot] as HudBarWidget)?.PushValue(percent);

    /// <summary>
    /// Shows text in a slot (name, title, guild name). Empty text hides the slot.
    /// Styles are the widget defaults until config assets land (styles per relation come then).
    /// </summary>
    public void SetText(HudSlot slot, string text, HudAlignment align)
    {
        int index = (int)slot;
        if (string.IsNullOrEmpty(text))
        {
            SetSlotVisible(slot, false);
            return;
        }

        HudTextWidget widget = Slots[index] as HudTextWidget ?? CreateSlot<HudTextWidget>(index);
        widget.ItemLineGap = _manager?.TextLineGap ?? DefaultSpriteLineGap;
        widget.RoleOffset = _roleWorldOffset;
        _manager?.EnsureFontSlot();
        widget.PushText(text, align);
        widget.Anchor = _rolePos + widget.RoleOffset;
        Reflow();
    }

    public void Fill(List<HudMeshData?> targets, in HudFillContext context)
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            HudWidget? widget = Slots[i];
            if (widget is { IsValid: true })
                widget.Fill(targets, context);
        }
        for (int i = 0; i < _dynamic.Count; i++)
        {
            HudWidget widget = _dynamic[i];
            if (widget.IsValid)
                widget.Fill(targets, context);
        }
    }

    private T CreateSlot<T>(int index) where T : HudWidget, new()
    {
        T widget = HudPool<T>.Pop();
        Slots[index] = widget;
        return widget;
    }

    public void Release()
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            HudWidget? widget = Slots[i];
            if (widget is null)
                continue;
            widget.Release();
            Recycle(widget);
            Slots[i] = null;
        }
        for (int i = _dynamic.Count - 1; i >= 0; i--)
        {
            HudWidget widget = _dynamic[i];
            widget.Release();
            Recycle(widget);
        }
        _dynamic.Clear();
        _manager = null;
        _target = null;
        _initialized = false;
        HasVisibleContent = false;
    }

    private static void Recycle(HudWidget widget)
    {
        switch (widget)
        {
            case HudSpriteWidget sprite:
                HudPool<HudSpriteWidget>.Push(sprite);
                break;
            case HudTextWidget text:
                HudPool<HudTextWidget>.Push(text);
                break;
            case HudBarWidget bar:
                HudPool<HudBarWidget>.Push(bar);
                break;
            case HudNumberWidget number:
                HudPool<HudNumberWidget>.Push(number);
                break;
            case HudTalkWidget talk:
                HudPool<HudTalkWidget>.Push(talk);
                break;
        }
    }
}
