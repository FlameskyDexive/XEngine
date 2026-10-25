// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;

using Color = System.Drawing.Color;

namespace XEngine.OrigamiUI;

/// <summary>
/// Fluent builder for an Origami menu bar: a horizontal strip of top-level menu buttons
/// (File, Edit, …), each opening a dropdown built with the <see cref="ContextBuilder"/> API
/// (so items, submenus, icons, shortcuts, separators and toggles all work). Construct via
/// <see cref="Origami.MenuBar(Paper, string)"/>, add menus with <see cref="Menu"/>, then call
/// <see cref="Show"/>.
/// </summary>
/// <remarks>
/// <para>Interaction: a CLICK opens a menu; while one is open, hovering another button switches to
/// it; clicking the open button toggles it closed; clicking anywhere else closes it. The bar owns
/// its own full-screen click-catcher, so click-away works regardless of what the host draws around
/// it. State is kept per-instance in element storage, so multiple menu bars can coexist.</para>
/// <para>Self-contained: depends only on Paper + Origami primitives, so it works in any Paper app,
/// not just the editor sample.</para>
/// </remarks>
public sealed class MenuBarBuilder
{
    // Per-instance state, parked in element storage so callers hold no state of their own.
    private sealed class State
    {
        public int Open = -1;
        public readonly MenuNavigation Navigation = new();
        public readonly float[] AnchorX = new float[MaxMenus];
        public readonly float[] AnchorY = new float[MaxMenus];

        public void Select(int index, bool keyboard = false)
        {
            if (index == Open) return;
            if (index < 0) Navigation.ReleaseFocus();
            Navigation.Reset(keyboard);
            Open = index;
        }
    }

    private const int MaxMenus = 32;
    private const int MirrorCatcherLayer = Layer.Overlay + 900;
    private const int MirrorMenuLayer = Layer.Overlay + 901;

    /// <summary>True while a dropdown is mirrored onto other surfaces (full-window click catcher).</summary>
    public static bool HasOpenDropdown => s_openPaper != null;

    /// <summary>Drop a mirrored dropdown that outlived its owner (e.g. the launcher hid the menu bar).</summary>
    public static void ClearMirroredMenu()
    {
        s_openNavigation?.ReleaseFocus();
        s_openPaper = null;
        s_openBuild = null;
        s_openClose = null;
        s_openNavigation = null;
    }

    private static Paper? s_openPaper;
    private static float s_openX, s_openY;
    private static Action<ContextBuilder>? s_openBuild;
    private static Action? s_openClose;
    private static MenuNavigation? s_openNavigation;

    private readonly Paper _paper;
    private readonly string _id;
    private readonly OrigamiTheme _theme;
    private readonly List<(string Label, Action<ContextBuilder> Build)> _menus = new();
    private float _height = 32f;
    private bool _background = true;
    private Color? _itemInk;
    private Color? _itemInkMuted;
    private Color? _itemHover;

    internal MenuBarBuilder(Paper paper, string id, OrigamiTheme theme)
    {
        _paper = paper ?? throw new ArgumentNullException(nameof(paper));
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
    }

    /// <summary>Add a top-level menu. <paramref name="build"/> populates its dropdown via the
    /// <see cref="ContextBuilder"/> API (items, submenus, separators, shortcuts, toggles).</summary>
    public MenuBarBuilder Menu(string label, Action<ContextBuilder> build)
    {
        if (_menus.Count < MaxMenus) _menus.Add((label ?? string.Empty, build));
        return this;
    }

    /// <summary>Bar height in pixels (default 32).</summary>
    public MenuBarBuilder Height(float height) { _height = MathF.Max(20f, height); return this; }

    /// <summary>Draw the bar as floating text with no glass fill or border (default keeps the fill).</summary>
    public MenuBarBuilder Transparent(bool transparent = true) { _background = !transparent; return this; }

    /// <summary>Override item label / hover colours (Unity 6 light menu bar on a dark editor).</summary>
    public MenuBarBuilder ItemColors(Color ink, Color muted, Color hover)
    {
        _itemInk = ink;
        _itemInkMuted = muted;
        _itemHover = hover;
        return this;
    }

    /// <summary>Render the menu bar.</summary>
    public void Show()
    {
        var ink = _theme.Ink;
        var font = _theme.Medium ?? _theme.Font;
        if (font == null || _menus.Count == 0) return;

        // State lives on the parent element, keyed by this bar's id (so sibling bars don't collide).
        var parentH = _paper.CurrentParent;
        var state = _paper.GetElementStorage<State>(parentH, $"{_id}_st", null!) ?? new State();
        _paper.SetElementStorage(parentH, $"{_id}_st", state);

        if (state.Open >= 0)
        {
            _paper.CaptureKeyboard();
            if (!_theme.Menus.HasValue && _paper.IsKeyPressed(PaperKey.Escape)) state.Select(-1);
        }

        bool open = state.Open >= 0 && state.Open < _menus.Count;

        // When open, the bar rides above a transparent click-catcher so the bar stays hoverable
        // (to switch menus) while a click anywhere else drops focus.
        Color barFill = _background ? _theme.Glass : Color.FromArgb(0, 0, 0, 0);
        Color barBorder = _background ? _theme.BorderSoft : Color.FromArgb(0, 0, 0, 0);
        using (_paper.Row(_id).Width(UnitValue.Auto).Height(_height).Rounded(_background ? 8 : 0).Padding(4, 4, 0, 0)
            .Layer(open ? Layer.Topmost + 2 : Layer.Base)
            .BackgroundColor(barFill).BorderColor(barBorder).BorderWidth(_background ? 1 : 0)
            .Enter())
        {
            for (int i = 0; i < _menus.Count; i++)
                DrawItem(i, parentH, state, font, ink);
        }

        if (open)
        {
            _paper.Box($"{_id}_bd").PositionType(PositionType.SelfDirected)
                .Position(-99999, -99999).Size(199999, 199999)
                .Layer(Layer.Topmost).BackgroundColor(Color.FromArgb(0, 0, 0, 0))
                .IsNotFocusable().StopEventPropagation().OnClick(0, (_, _) => { state.Select(-1); ClearMirroredMenu(); });

            Action close = () => { state.Select(-1); ClearMirroredMenu(); };
            if (_theme.Menus.HasValue)
            {
                state.Navigation.SwitchRoot = delta => state.Select((state.Open + delta + _menus.Count) % _menus.Count, true);
                ContextMenu.InteractivePanel(_paper, $"{_id}_drop", state.AnchorX[state.Open], state.AnchorY[state.Open],
                    Layer.Topmost + 3, close, _menus[state.Open].Build, state.Navigation,
                    arranged: rect => { s_openX = (float)rect.Min.X; s_openY = (float)rect.Min.Y; });
            }
            else
                ContextMenu.Panel(_paper, $"{_id}_drop", state.AnchorX[state.Open], state.AnchorY[state.Open],
                    Layer.Topmost + 3, close, _menus[state.Open].Build);

            if (state.Open < 0) return;

            s_openPaper = _paper;
            s_openX = state.AnchorX[state.Open];
            s_openY = state.AnchorY[state.Open];
            s_openBuild = _menus[state.Open].Build;
            s_openClose = close;
            s_openNavigation = _theme.Menus.HasValue ? state.Navigation : null;
        }
        else if (ReferenceEquals(s_openPaper, _paper))
        {
            ClearMirroredMenu();
        }
    }

    /// <summary>
    /// Paint the open dropdown onto a different Paper surface in that surface's local coordinates.
    /// Detached native windows cover the owner surface, so the same menu has to be drawn again
    /// on those windows or it disappears behind them.
    /// </summary>
    internal static void DrawMirrored(Paper paper)
    {
        if (s_openPaper == null || s_openBuild == null || s_openClose == null) return;
        if (ReferenceEquals(paper, s_openPaper)) return;
        if (ContextMenu.MapPointBetweenPapers == null) return;

        var (x, y) = ContextMenu.MapPointBetweenPapers(s_openPaper, paper, s_openX, s_openY);
        Action close = s_openClose;

        paper.CaptureKeyboard();
        if (s_openNavigation == null && paper.IsKeyPressed(PaperKey.Escape))
        {
            close();
            ClearMirroredMenu();
            return;
        }

        paper.Box("menubar_mirror_bd").PositionType(PositionType.SelfDirected)
            .Position(-99999, -99999).Size(199999, 199999)
            .Layer(MirrorCatcherLayer).BackgroundColor(Color.FromArgb(0, 0, 0, 0))
            .IsNotFocusable().StopEventPropagation().OnClick(0, (_, _) => close());

        if (s_openNavigation != null)
            ContextMenu.InteractivePanel(paper, "menubar_mirror_drop", x, y, MirrorMenuLayer, close, s_openBuild,
                s_openNavigation, clampToScreen: false, boundsPaper: s_openPaper);
        else
            ContextMenu.Panel(paper, "menubar_mirror_drop", x, y, MirrorMenuLayer, close, s_openBuild,
                clampToScreen: false);
    }

    private void DrawItem(int index, ElementHandle parentH, State state, Scribe.FontFile font, OrigamiRamp ink)
    {
        bool active = state.Open == index;
        Color hover = _itemHover ?? _theme.Hover;
        Color inkActive = _itemInk ?? ink.C500;
        Color inkIdle = _itemInkMuted ?? ink.C300;

        var box = _paper.Box($"{_id}_i{index}")
            .Width(UnitValue.Auto).Height(UnitValue.Auto).Rounded(3).Padding(8, 8, 3, 3)
            .Margin(0, 0, UnitValue.Stretch(), UnitValue.Stretch())
            .BackgroundColor(active ? hover : Color.FromArgb(0, 0, 0, 0))
            .Hovered.BackgroundColor(hover).End()
            .OnClick(index, (idx, _) => state.Select(state.Open == idx ? -1 : idx))
            // Record this item's drop anchor every frame (post-layout, so it's always the real
            // position, relative to the bar's parent where the dropdown is rendered). Switching by
            // hover then lands the drop correctly with no build-vs-event timing gap.
            .OnPostLayout((_, rect) =>
            {
                var pr = parentH.Data.LayoutRect;
                state.AnchorX[index] = (float)(rect.Min.X - pr.Min.X);
                state.AnchorY[index] = (float)(rect.Max.Y - pr.Min.Y) + 4f;
            });
        if (_theme.Menus.HasValue) box.IsNotFocusable();

        using (box.Enter())
        {
            // Click opens; hovering only SWITCHES while a menu is already open (never opens from idle).
            if (state.Open >= 0 && state.Open != index && _paper.IsParentHovered
                && (!_theme.Menus.HasValue || state.Navigation.CanHover(_paper)))
                state.Select(index);

            _paper.Box($"{_id}_t{index}").Width(UnitValue.Auto).Height(UnitValue.Auto).IsNotInteractable()
                .Text(_menus[index].Label, font)
                .TextColor(active ? inkActive : inkIdle)
                .FontSize(_theme.Metrics.FontSize).Alignment(TextAlignment.MiddleCenter);
        }
    }
}
