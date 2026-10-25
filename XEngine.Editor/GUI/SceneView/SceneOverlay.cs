using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;
using XEngine.Vector;

using Color = System.Drawing.Color;

namespace XEngine.Editor.GUI.SceneView;

/// <summary>Which viewport corner an overlay's stored offset is measured from.</summary>
public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// A small floating panel inside the Scene view viewport: the transform tools column, grid and snap
/// settings, the camera preview, and so on. Overlays are dragged by their handle, collapse to just
/// the handle, and remember which corner they were parked in across sessions.
/// </summary>
public sealed class SceneOverlay
{
    public SceneOverlay(string id, string title, string icon, Action<Paper, Scribe.FontFile> body)
    {
        Id = id;
        Title = title;
        Icon = icon;
        Body = body;
    }

    public string Id { get; }
    public string Title { get; set; }
    public string Icon { get; set; }
    public IOrigamiIcon? IconDraw { get; set; }
    public Action<Paper, Scribe.FontFile> Body { get; }

    public OverlayCorner Corner = OverlayCorner.TopLeft;

    /// <summary>Distance from <see cref="Corner"/> to the overlay's nearest two edges, in logical px.</summary>
    public Float2 Offset = new(12, 12);

    public bool Visible = true;
    public bool Collapsed;

    /// <summary>Fixed width, or 0 to size to the body content.</summary>
    public float Width;

    /// <summary>Hide the title text and shrink the handle — for narrow overlays like the tool column.</summary>
    public bool CompactHandle;

    /// <summary>Extra menu entries appended to this overlay's right-click menu.</summary>
    public Action<ContextBuilder>? ContextMenu;

    /// <summary>When set and returning false, the overlay is skipped entirely (not just hidden).</summary>
    public Func<bool>? IsAvailable;

    /// <summary>Whether the user can toggle this overlay from the overlay bar.</summary>
    public bool ShowInOverlayBar = true;

    // Measured last frame so the anchored corners can be resolved without a layout pass.
    internal Float2 MeasuredSize = new(120, 40);

    internal bool Applicable => IsAvailable?.Invoke() ?? true;
}

/// <summary>
/// Draws and manages a Scene view's <see cref="SceneOverlay"/> set: positioning against the viewport
/// corners, dragging (with re-anchoring to whichever corner the overlay ends up nearest), the bottom
/// overlay bar of toggle buttons, and layout persistence.
/// </summary>
public sealed class SceneOverlayHost
{
    private const float HandleHeight = 16f;
    private const float EdgeMargin = 8f;

    private readonly List<SceneOverlay> _overlays = new();
    private string? _dragging;

    public IReadOnlyList<SceneOverlay> Overlays => _overlays;

    public SceneOverlay Add(SceneOverlay overlay)
    {
        _overlays.Add(overlay);
        return overlay;
    }

    public SceneOverlay? Find(string id)
    {
        foreach (var overlay in _overlays)
            if (overlay.Id == id) return overlay;
        return null;
    }

    /// <summary>Draw every applicable overlay plus the overlay bar. Call inside the viewport element.</summary>
    public void Draw(Paper paper, Scribe.FontFile font, float viewWidth, float viewHeight)
    {
        foreach (var overlay in _overlays)
        {
            if (!overlay.Visible || !overlay.Applicable) continue;
            DrawOverlay(paper, font, overlay, viewWidth, viewHeight);
        }

        DrawOverlayBar(paper, font, viewWidth, viewHeight);
    }

    private void DrawOverlay(Paper paper, Scribe.FontFile font, SceneOverlay overlay,
        float viewWidth, float viewHeight)
    {
        Float2 size = overlay.MeasuredSize;
        float x = overlay.Corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft
            ? overlay.Offset.X
            : viewWidth - overlay.Offset.X - size.X;
        float y = overlay.Corner is OverlayCorner.TopLeft or OverlayCorner.TopRight
            ? overlay.Offset.Y
            : viewHeight - overlay.Offset.Y - size.Y;

        // Keep at least a sliver of the handle reachable no matter how the panel was resized.
        x = Math.Clamp(x, EdgeMargin - size.X + 30f, MathF.Max(EdgeMargin, viewWidth - 30f));
        y = Math.Clamp(y, EdgeMargin, MathF.Max(EdgeMargin, viewHeight - HandleHeight - 4f));

        using (paper.Column($"ov_{overlay.Id}")
            .PositionType(PositionType.SelfDirected)
            .Position(x, y)
            .Width(overlay.Width > 0 ? overlay.Width : UnitValue.Auto)
            .Height(UnitValue.Auto)
            .Rounded(8).Clip()
            .BackgroundColor(EditorTheme.Glass)
            .BorderColor(_dragging == overlay.Id ? EditorTheme.Accent : EditorTheme.BorderSoft).BorderWidth(1)
            .OnPostLayout((_, rect) =>
                overlay.MeasuredSize = new Float2((float)rect.Size.X, (float)rect.Size.Y))
            .Enter())
        {
            DrawHandle(paper, font, overlay, viewWidth, viewHeight);

            if (!overlay.Collapsed)
                using (paper.Column($"ov_{overlay.Id}_body").Height(UnitValue.Auto)
                    .Padding(5, 5, 4, 5).ColBetween(3).Enter())
                    overlay.Body(paper, font);
        }
    }

    private void DrawHandle(Paper paper, Scribe.FontFile font, SceneOverlay overlay,
        float viewWidth, float viewHeight)
    {
        bool active = _dragging == overlay.Id;

        using (paper.Row($"ov_{overlay.Id}_hdr").Height(HandleHeight)
            .Padding(5, 4, 0, 0).RowBetween(4)
            .BackgroundColor(active ? EditorTheme.ToolbarActive : Color.Transparent)
            .Hovered.BackgroundColor(EditorTheme.Hover).End()
            .Cursor(active ? PaperCursor.Grabbing : PaperCursor.Grab)
            .OnDragStart(_ => _dragging = overlay.Id)
            .OnDragging(e => Move(overlay, (float)e.Delta.X, (float)e.Delta.Y))
            .OnDragEnd(_ =>
            {
                _dragging = null;
                Reanchor(overlay, viewWidth, viewHeight);
            })
            .OnRightClick(_ => ShowOverlayMenu(paper, overlay))
            .Enter())
        {
            paper.Box($"ov_{overlay.Id}_grip").Width(10).Height(HandleHeight).IsNotInteractable()
                .Icon(paper, SceneViewIcons.Grip, EditorTheme.InkDim, size: 8f);

            if (!overlay.CompactHandle)
                paper.Box($"ov_{overlay.Id}_title").Height(HandleHeight).Width(UnitValue.Stretch())
                    .IsNotInteractable()
                    .Text(overlay.Title, font).TextColor(EditorTheme.Ink300)
                    .FontSize(EditorTheme.FontSizeSmall - 1f).Alignment(TextAlignment.MiddleLeft)
                    .TextTruncate();

            paper.Box($"ov_{overlay.Id}_fold").Width(12).Height(HandleHeight).Rounded(3)
                .Hovered.BackgroundColor(EditorTheme.Hover).End()
                .Icon(paper, overlay.Collapsed ? SceneViewIcons.ChevronDown : SceneViewIcons.ChevronUp,
                    EditorTheme.InkDim, size: 8f)
                .Tooltip(Loc.Get(overlay.Collapsed ? "overlay.expand" : "overlay.collapse"))
                .OnClick(0, (_, _) => overlay.Collapsed = !overlay.Collapsed);
        }
    }

    private static void Move(SceneOverlay overlay, float dx, float dy)
    {
        // Offsets grow inward from the anchored corner, so the sign flips on the far edges.
        overlay.Offset = new Float2(
            overlay.Offset.X + (overlay.Corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? dx : -dx),
            overlay.Offset.Y + (overlay.Corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? dy : -dy));
    }

    /// <summary>
    /// Re-home an overlay to whichever corner it was dropped nearest, so it keeps its visual position
    /// when the panel is later resized (a tool column parked bottom-right stays bottom-right).
    /// </summary>
    private static void Reanchor(SceneOverlay overlay, float viewWidth, float viewHeight)
    {
        if (viewWidth <= 1 || viewHeight <= 1) return;

        Float2 size = overlay.MeasuredSize;
        float left = overlay.Corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft
            ? overlay.Offset.X
            : viewWidth - overlay.Offset.X - size.X;
        float top = overlay.Corner is OverlayCorner.TopLeft or OverlayCorner.TopRight
            ? overlay.Offset.Y
            : viewHeight - overlay.Offset.Y - size.Y;

        bool right = left + size.X * 0.5f > viewWidth * 0.5f;
        bool bottom = top + size.Y * 0.5f > viewHeight * 0.5f;

        overlay.Corner = (right, bottom) switch
        {
            (false, false) => OverlayCorner.TopLeft,
            (true, false) => OverlayCorner.TopRight,
            (false, true) => OverlayCorner.BottomLeft,
            _ => OverlayCorner.BottomRight,
        };
        overlay.Offset = new Float2(
            MathF.Max(EdgeMargin, right ? viewWidth - left - size.X : left),
            MathF.Max(EdgeMargin, bottom ? viewHeight - top - size.Y : top));
    }

    private void ShowOverlayMenu(Paper paper, SceneOverlay overlay)
        => Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, menu =>
        {
            menu.Title(overlay.Title, overlay.Icon);
            menu.Toggle(Loc.Get("overlay.collapse"), () => overlay.Collapsed = !overlay.Collapsed, () => overlay.Collapsed);
            overlay.ContextMenu?.Invoke(menu);
            menu.Separator();
            menu.Item(Loc.Get("overlay.hide"), () => overlay.Visible = false, icon: EditorIcons.EyeSlash);
        });

    // ================================================================
    //  Overlay bar (bottom-left strip of per-overlay toggles)
    // ================================================================

    private void DrawOverlayBar(Paper paper, Scribe.FontFile font, float viewWidth, float viewHeight)
    {
        const float barHeight = 26f;

        using (paper.Row("ov_bar")
            .PositionType(PositionType.SelfDirected)
            .Position(12, viewHeight - barHeight - 12)
            .Width(UnitValue.Auto).Height(barHeight)
            .Rounded(8).Padding(4, 4, 0, 0).RowBetween(2)
            .BackgroundColor(EditorTheme.Glass)
            .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .Enter())
        {
            foreach (var overlay in _overlays)
            {
                if (!overlay.ShowInOverlayBar) continue;
                bool applicable = overlay.Applicable;
                bool on = overlay.Visible && applicable;
                var captured = overlay;

                IOrigamiIcon overlayIcon = overlay.IconDraw ?? SceneViewIcons.Glyph(overlay.Icon);
                Color iconColor = !applicable ? EditorTheme.InkFaint : EditorTheme.Ink300;
                paper.Box($"ov_bar_{overlay.Id}").Width(20).Height(20).Rounded(5)
                    .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                    .BackgroundColor(on ? EditorTheme.ToolbarActive : Color.Transparent)
                    .Hovered.BackgroundColor(on ? EditorTheme.ToolbarActive : EditorTheme.Hover).End()
                    .Icon(paper, overlayIcon, iconColor, size: 12f)
                    .Tooltip(overlay.Title)
                    .OnClick(0, (_, _) =>
                    {
                        if (!captured.Applicable) return;
                        captured.Visible = !captured.Visible;
                    });
            }

            paper.Box("ov_bar_div").Width(1).Height(14).Margin(2, 2, UnitValue.StretchOne, UnitValue.StretchOne)
                .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

            paper.Box("ov_bar_menu").Width(20).Height(20).Rounded(5)
                .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                .Hovered.BackgroundColor(EditorTheme.Hover).End()
                .Icon(paper, SceneViewIcons.Ellipsis, EditorTheme.Ink300, size: 12f)
                .Tooltip(Loc.Get("overlay.menu"))
                .OnClick(0, (_, _) => Origami.ContextMenu(
                    (float)paper.PointerPos.X, (float)paper.PointerPos.Y, BuildOverlayMenu));
        }
    }

    private void BuildOverlayMenu(ContextBuilder menu)
    {
        menu.Header(Loc.Get("overlay.menu"));
        foreach (var overlay in _overlays)
        {
            var captured = overlay;
            menu.Toggle(overlay.Title, () => captured.Visible = !captured.Visible,
                () => captured.Visible, enabled: overlay.Applicable);
        }
        menu.Separator();
        menu.Item(Loc.Get("overlay.reset_layout"), ResetLayout, icon: EditorIcons.RotateLeft);
    }

    /// <summary>Send every overlay back to its default corner, offset, and visibility.</summary>
    public void ResetLayout()
    {
        foreach (var overlay in _overlays)
        {
            if (_defaults.TryGetValue(overlay.Id, out var def))
            {
                overlay.Corner = def.corner;
                overlay.Offset = def.offset;
                overlay.Visible = def.visible;
            }
            overlay.Collapsed = false;
        }
    }

    private readonly Dictionary<string, (OverlayCorner corner, Float2 offset, bool visible)> _defaults = new();

    /// <summary>Record the current placement of every overlay as the layout <see cref="ResetLayout"/> returns to.</summary>
    public void CaptureDefaults()
    {
        _defaults.Clear();
        foreach (var overlay in _overlays)
            _defaults[overlay.Id] = (overlay.Corner, overlay.Offset, overlay.Visible);
    }

    // ================================================================
    //  Persistence
    // ================================================================

    public JsonArray Serialize()
    {
        var array = new JsonArray();
        foreach (var overlay in _overlays)
            array.Add(new JsonObject
            {
                ["id"] = overlay.Id,
                ["corner"] = (int)overlay.Corner,
                ["x"] = overlay.Offset.X,
                ["y"] = overlay.Offset.Y,
                ["visible"] = overlay.Visible,
                ["collapsed"] = overlay.Collapsed,
            });
        return array;
    }

    public void Restore(JsonArray? array)
    {
        if (array == null) return;
        foreach (var node in array)
        {
            if (node is not JsonObject entry) continue;
            string? id = entry["id"]?.GetValue<string>();
            if (id == null) continue;
            var overlay = Find(id);
            if (overlay == null) continue;

            if (entry["corner"]?.GetValue<int>() is int corner && corner is >= 0 and <= 3)
                overlay.Corner = (OverlayCorner)corner;
            float x = entry["x"]?.GetValue<float>() ?? overlay.Offset.X;
            float y = entry["y"]?.GetValue<float>() ?? overlay.Offset.Y;
            overlay.Offset = new Float2(x, y);
            overlay.Visible = entry["visible"]?.GetValue<bool>() ?? overlay.Visible;
            overlay.Collapsed = entry["collapsed"]?.GetValue<bool>() ?? overlay.Collapsed;
        }
    }
}
