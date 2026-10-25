// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Vector;

using Color = System.Drawing.Color;

namespace XEngine.OrigamiUI;

/// <summary>
/// A single item in an Origami context menu.
/// </summary>
internal interface IContextItem
{
    void Draw(Paper paper, string id, int index, Scribe.FontFile font, OrigamiTheme theme, Action close, int layer, ContextBuilder builder);
}

/// <summary>Bounded measurements belonging to a menu session or a caller-owned panel element.</summary>
internal sealed class MenuWidthCache
{
    private struct Row
    {
        internal string? Label, Shortcut;
        internal float Width;
    }

    private const int MaximumRows = 4096, MaximumTextLength = 4096;
    private Row[] _rows = [];
    private Paper? _paper;
    private Scribe.FontFile? _font;
    private float _fontSize, _scale;
    private int _atlasRevision;

    internal void Begin(Paper paper, Scribe.FontFile font, float fontSize, int count)
    {
        int retained = Math.Min(count, MaximumRows);
        if (_rows.Length < retained) Array.Resize(ref _rows, retained);
        if (!ReferenceEquals(_paper, paper) || !ReferenceEquals(_font, font)
            || _fontSize != fontSize || _scale != paper.Canvas.FramebufferScale
            || _atlasRevision != paper.Canvas.FontAtlasRevision)
            Array.Clear(_rows);
        else if (retained < _rows.Length)
            Array.Clear(_rows, retained, _rows.Length - retained);
        _paper = paper; _font = font; _fontSize = fontSize;
        _scale = paper.Canvas.FramebufferScale;
        _atlasRevision = paper.Canvas.FontAtlasRevision;
    }

    internal void Forget(int index)
    {
        if (index < _rows.Length) _rows[index] = default;
    }

    internal float Measure(int index, string label, string shortcut)
    {
        if (index < _rows.Length)
        {
            ref var row = ref _rows[index];
            if (string.Equals(row.Label, label, StringComparison.Ordinal)
                && string.Equals(row.Shortcut, shortcut, StringComparison.Ordinal)) return row.Width;
        }
        float width = (float)_paper!.MeasureText(label, _fontSize, _font!).X;
        if (shortcut.Length > 0) width += (float)_paper.MeasureText(shortcut, _fontSize, _font!).X;
        if (index < _rows.Length)
            _rows[index] = label.Length <= MaximumTextLength && shortcut.Length <= MaximumTextLength
                ? new Row { Label = label, Shortcut = shortcut, Width = width } : default;
        return width;
    }
}

/// <summary>
/// Fluent builder for an Origami context menu. Build items, then the system renders it.
/// </summary>
public sealed class ContextBuilder
{
    internal readonly List<IContextItem> Items = [];
    internal Action? CloseAction;
    internal MenuNavigation? Navigation;
    internal int Depth;
    internal Paper? BoundsPaper;
    internal MenuWidthCache? WidthCache;
    internal float? MeasuredWidth;

    /// <summary>Add a clickable menu item.</summary>
    /// <param name="label">Row label.</param>
    /// <param name="onClick">Invoked on click (menu closes afterwards).</param>
    /// <param name="enabled">When false the row is dimmed and non-interactive.</param>
    /// <param name="icon">Optional text/glyph icon (glyph font may be empty — prefer <paramref name="iconDraw"/>).</param>
    /// <param name="shortcut">Optional trailing keyboard hint, e.g. "Ctrl D", "F2", "Del".</param>
    /// <param name="danger">Render the row in the danger (red) colour.</param>
    /// <param name="on">Render the row in the active/accent colour (the <c>.on</c> variant).</param>
    /// <param name="iconDraw">Optional leading icon, drawn tinted to the row's current text colour.</param>
    public ContextBuilder Item(string label, Action onClick, bool enabled = true, string icon = "",
        string shortcut = "", bool danger = false, bool on = false, IOrigamiIcon? iconDraw = null)
    {
        Items.Add(new CtxItem
        {
            Label = label,
            OnClick = onClick,
            Enabled = enabled,
            Icon = icon,
            Shortcut = shortcut,
            Danger = danger,
            On = on,
            IconDraw = iconDraw,
        });
        return this;
    }

    /// <summary>Add a non-interactive uppercase section header at the top of a group.</summary>
    public ContextBuilder Header(string text)
    {
        Items.Add(new CtxHeader { Text = text });
        return this;
    }

    /// <summary>Add a non-interactive title row (the subject of the menu, e.g. the clicked item's name),
    /// shown prominently in normal case with a divider beneath it.</summary>
    public ContextBuilder Title(string text, string icon = "", IOrigamiIcon? iconDraw = null)
    {
        Items.Add(new CtxTitle { Text = text, Icon = icon, IconDraw = iconDraw });
        return this;
    }

    /// <summary>Add a toggle item that shows a checkbox state.</summary>
    public ContextBuilder Toggle(string label, Action onClick, Func<bool> getValue, bool enabled = true)
    {
        Items.Add(new CtxToggle { Label = label, OnClick = onClick, GetValue = getValue, Enabled = enabled });
        return this;
    }

    /// <summary>Add a horizontal separator line.</summary>
    public ContextBuilder Separator()
    {
        Items.Add(new CtxSeparator());
        return this;
    }

    /// <summary>Add a submenu that expands on hover.</summary>
    public ContextBuilder Submenu(string label, Action<ContextBuilder> build, string icon = "")
    {
        var sub = new ContextBuilder();
        build(sub);
        Items.Add(new CtxSubmenu { Label = label, Icon = icon, Sub = sub });
        return this;
    }

    // ── Shared row geometry / palette (Nebula .w2menu spec) ───

    // Row height / fonts track the theme metrics so the menu follows RowHeight / FontSize changes.
    internal static float RowHeight => Origami.Current.Menus?.RowHeight ?? Origami.Current.Metrics.RowHeight + 6f;
    internal static float RowFont => Origami.Current.Menus?.FontSize ?? Origami.Current.Metrics.FontSize;
    internal static float SubFont => Origami.Current.Menus?.FontSize ?? Origami.Current.Metrics.FontSize - 3f;
    internal const float IconSize = 15f;  // leading vector icon
    internal static float RowPadX => Origami.Current.Menus.HasValue ? 8f : 9f;
    internal static float RowGap => Origami.Current.Menus.HasValue ? 8f : 9f;
    internal static float RowRadius => Origami.Current.Menus.HasValue ? 0f : 6f;

    private static Color RowText(OrigamiTheme theme, bool enabled, bool hovered, bool danger = false)
        => !enabled ? theme.Menus?.DisabledText ?? theme.Ink.C100
         : danger ? theme.Red.C500
         : hovered ? theme.Menus?.HoverText ?? theme.Ink.C500
         : theme.Menus?.Text ?? theme.Ink.C400;

    private static Color RowHover(OrigamiTheme theme, bool enabled = true)
        => enabled ? theme.Menus?.HoverBackground ?? theme.Hover : Color.Transparent;

    private static void DesktopLeading(Paper paper, string id, Scribe.FontFile font,
        OrigamiTheme theme, Color color, bool on, string icon = "", IOrigamiIcon? iconDraw = null)
    {
        using (paper.Box(id).Width(22).Height(RowHeight).IsNotInteractable().Enter())
        {
            if (on)
                paper.Box($"{id}_check").Size(22, RowHeight).IsNotInteractable()
                    .Icon(paper, OrigamiIconSet.Check, color, size: 12f);
            else if (iconDraw != null)
                paper.Box($"{id}_icon").Size(22, RowHeight).IsNotInteractable()
                    .Icon(paper, iconDraw, color, size: IconSize);
            else if (!string.IsNullOrEmpty(icon))
                paper.Box($"{id}_glyph").Size(22, RowHeight).IsNotInteractable()
                    .Text(icon, font).FontSize(RowFont).TextColor(color).Alignment(TextAlignment.MiddleCenter);
        }
    }

    // rgba(168,85,247,0.12) — .w2mrow:hover background.

    // ── Item types ───────────────────────────────────────────

    internal sealed class CtxItem : IContextItem
    {
        public string Label = "", Icon = "", Shortcut = "";
        public Action? OnClick;
        public IOrigamiIcon? IconDraw;
        public bool Enabled = true;
        public bool Danger;
        public bool On;

        public void Draw(Paper paper, string id, int index, Scribe.FontFile font, OrigamiTheme theme, Action close, int layer, ContextBuilder builder)
        {
            var ink = theme.Ink;

            var row = paper.Row($"{id}_i_{index}")
                .Height(RowHeight)
                .Padding(RowPadX, RowPadX, 0, 0)
                .RowBetween(RowGap)
                .Rounded(RowRadius);

            if (builder.Navigation == null) row.Hovered.BackgroundColor(RowHover(theme, Enabled)).End();
            else row.IsNotFocusable();

            if (Enabled)
                row.OnClick(0, (_, _) => { if (theme.Menus.HasValue) { close(); OnClick?.Invoke(); } else { OnClick?.Invoke(); close(); } });

            using (row.Enter())
            {
                if (Enabled) builder.Navigation?.Hover(paper, builder, builder.Depth, index);
                bool hovered = Enabled && (builder.Navigation?.Selected(builder.Depth, index) ?? paper.IsParentHovered);
                if (builder.Navigation != null) row.BackgroundColor(hovered ? RowHover(theme) : Color.Transparent);
                Color txt = RowText(theme, Enabled, hovered, Danger);

                if (theme.Menus.HasValue)
                    DesktopLeading(paper, $"{id}_ico_{index}", font, theme, txt, On, Icon, IconDraw);
                else
                    DrawLeadingIcon(paper, $"{id}_ico_{index}", font, txt);

                paper.Box($"{id}_l_{index}")
                    .Width(UnitValue.Stretch()).Height(RowHeight)
                    .Text(Label, font).TextColor(txt).Clip()
                    .FontSize(RowFont).Alignment(TextAlignment.MiddleLeft);

                if (!string.IsNullOrEmpty(Shortcut))
                    paper.Box($"{id}_k_{index}")
                        .Width(UnitValue.Auto).Height(RowHeight)
                        .Text(Shortcut, font).TextColor(theme.Menus.HasValue ? txt : Enabled ? ink.C200 : ink.C100)
                        .FontSize(SubFont).Alignment(TextAlignment.MiddleRight);

                // Active/checked items (e.g. the current "Sort By") show a trailing green check.
                if (On && !theme.Menus.HasValue)
                    paper.Box($"{id}_chk_{index}").Width(16).Height(RowHeight).IsNotInteractable()
                        .Icon(paper, OrigamiIconSet.Check, theme.Green.C500, size: 13f);
            }
        }

        private void DrawLeadingIcon(Paper paper, string boxId, Scribe.FontFile font, Color color)
        {
            if (IconDraw != null)
            {
                var icon = IconDraw;
                using (paper.Box(boxId).Width(IconSize).Height(RowHeight).IsNotInteractable().Enter())
                    paper.Draw((canvas, rect) =>
                    {
                        // Center a square icon cell vertically within the full-height row box.
                        float sz = IconSize;
                        float ix = (float)(rect.Min.X + (rect.Size.X - sz) * 0.5f);
                        float iy = (float)(rect.Min.Y + (rect.Size.Y - sz) * 0.5f);
                        var cell = new XEngine.Vector.Rect(ix, iy, ix + sz, iy + sz);
                        icon.Draw(canvas, cell, color);
                    });
            }
            else if (!string.IsNullOrEmpty(Icon))
            {
                paper.Box(boxId).Width(IconSize).Height(RowHeight)
                    .Text(Icon, font).TextColor(color)
                    .FontSize(RowFont).Alignment(TextAlignment.MiddleCenter);
            }
            // No icon: draw nothing (label sits flush; no reserved leading gap).
        }
    }

    internal sealed class CtxTitle : IContextItem
    {
        public string Text = "";
        public string Icon = "";
        public IOrigamiIcon? IconDraw;

        public void Draw(Paper paper, string id, int index, Scribe.FontFile font, OrigamiTheme theme, Action close, int layer, ContextBuilder builder)
        {
            using (paper.Row($"{id}_ti_{index}").Height(RowHeight).Padding(RowPadX, RowPadX, 0, 0).RowBetween(RowGap)
                .IsNotInteractable().Enter())
            {
                if (IconDraw != null)
                    paper.Box($"{id}_tii_{index}").Width(IconSize).Height(RowHeight).IsNotInteractable()
                        .Icon(paper, IconDraw, theme.Primary.C700);
                else if (!string.IsNullOrEmpty(Icon))
                    paper.Box($"{id}_tii_{index}").Width(IconSize).Height(RowHeight)
                        .Text(Icon, font).TextColor(theme.Primary.C700).FontSize(RowFont).Alignment(TextAlignment.MiddleCenter);

                paper.Box($"{id}_tit_{index}").Width(UnitValue.Stretch()).Height(RowHeight).Clip()
                    .Text(Text, theme.SemiBold ?? theme.Bold ?? font).TextColor(theme.Menus?.Text ?? theme.Ink.C500)
                    .FontSize(RowFont - 1f).Alignment(TextAlignment.MiddleLeft);
            }
            paper.Box($"{id}_tid_{index}").Height(1).Margin(4, 4, 3, 4)
                .BackgroundColor(theme.Menus?.Border ?? theme.BorderSoft).IsNotInteractable();
        }
    }

    internal sealed class CtxHeader : IContextItem
    {
        public string Text = "";

        public void Draw(Paper paper, string id, int index, Scribe.FontFile font, OrigamiTheme theme, Action close, int layer, ContextBuilder builder)
        {
            using (paper.Row($"{id}_h_{index}")
                .Height(UnitValue.Auto)
                .Padding(RowPadX, RowPadX, 6, 4)
                .IsNotInteractable()
                .Enter())
            {
                paper.Box($"{id}_ht_{index}")
                    .Width(UnitValue.Stretch()).Height(14)
                    .Text(Text.ToUpperInvariant(), theme.SemiBold ?? theme.Bold ?? font)
                    .TextColor(theme.Menus?.DisabledText ?? theme.Ink.C100)
                    .LetterSpacing(0.5f)
                    .FontSize(SubFont).Alignment(TextAlignment.MiddleLeft);
            }
        }
    }

    internal sealed class CtxToggle : IContextItem
    {
        public string Label = "";
        public Action? OnClick;
        public Func<bool>? GetValue;
        public bool Enabled = true;

        public void Draw(Paper paper, string id, int index, Scribe.FontFile font, OrigamiTheme theme, Action close, int layer, ContextBuilder builder)
        {
            var ink = theme.Ink;

            var row = paper.Row($"{id}_i_{index}")
                .Height(RowHeight)
                .Padding(RowPadX, RowPadX, 0, 0)
                .RowBetween(RowGap)
                .Rounded(RowRadius);

            if (builder.Navigation == null) row.Hovered.BackgroundColor(RowHover(theme, Enabled)).End();
            else row.IsNotFocusable();

            if (Enabled)
                row.OnClick(0, (_, _) => { if (theme.Menus.HasValue) close(); OnClick?.Invoke(); });

            using (row.Enter())
            {
                if (Enabled) builder.Navigation?.Hover(paper, builder, builder.Depth, index);
                bool hovered = Enabled && (builder.Navigation?.Selected(builder.Depth, index) ?? paper.IsParentHovered);
                if (builder.Navigation != null) row.BackgroundColor(hovered ? RowHover(theme) : Color.Transparent);
                Color txt = RowText(theme, Enabled, hovered);
                bool on = GetValue?.Invoke() ?? false;

                if (theme.Menus.HasValue)
                    DesktopLeading(paper, $"{id}_ico_{index}", font, theme, txt, on);

                paper.Box($"{id}_l_{index}")
                    .Width(UnitValue.Stretch()).Height(RowHeight)
                    .Text(Label, font).TextColor(txt).Clip()
                    .FontSize(RowFont).Alignment(TextAlignment.MiddleLeft);

                if (theme.Menus.HasValue) return;

                // Pill toggle on the right (matches the Nebula .tg switch).
                var acc = theme.Primary.C500;
                using (paper.Box($"{id}_tg_{index}").Width(32).Height(18).Rounded(9)
                    .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                    .BackgroundColor(on ? acc : Color.FromArgb(26, 255, 255, 255))
                    .BorderColor(on ? Color.Transparent : theme.BorderSoft).BorderWidth(1)
                    .IsNotInteractable()
                    .Enter())
                    paper.Box($"{id}_tk_{index}").Width(14).Height(14).Rounded(7)
                        .PositionType(PositionType.SelfDirected).Position(on ? 16 : 2, 1.5f)
                        .BackgroundColor(Color.White).IsNotInteractable();
            }
        }
    }

    internal sealed class CtxSeparator : IContextItem
    {
        public void Draw(Paper paper, string id, int index, Scribe.FontFile font, OrigamiTheme theme, Action close, int layer, ContextBuilder builder)
        {
            // .c2sep — 1px bd line, ~5px vertical margin, ~4px horizontal inset.
            paper.Box($"{id}_sep_{index}")
                .Height(1).Margin(theme.Menus.HasValue ? 8 : 4, theme.Menus.HasValue ? 8 : 4,
                    theme.Menus.HasValue ? 4 : 5, theme.Menus.HasValue ? 4 : 5)
                .BackgroundColor(theme.Menus?.Border ?? theme.Neutral.C200);
        }
    }

    internal sealed class CtxSubmenu : IContextItem
    {
        public string Label = "", Icon = "";
        public ContextBuilder? Sub;

        public void Draw(Paper paper, string id, int index, Scribe.FontFile font, OrigamiTheme theme, Action close, int layer, ContextBuilder builder)
        {
            var ink = theme.Ink;

            var rowBox = paper.Row($"{id}_i_{index}")
                .Height(RowHeight)
                .Padding(RowPadX, RowPadX, 0, 0)
                .RowBetween(RowGap)
                .Rounded(RowRadius)
                // Remember this row's on-screen rect so next frame the submenu can decide which side to
                // open on (this frame's layout isn't available yet at build time).
                .OnPostLayout((h, r) => paper.SetElementStorage(h, "srect", r));
            if (builder.Navigation == null) rowBox.Hovered.BackgroundColor(RowHover(theme)).End();
            else rowBox.IsNotFocusable();
            using (rowBox.Enter())
            {
                builder.Navigation?.Hover(paper, builder, builder.Depth, index);
                bool hovered = builder.Navigation?.Selected(builder.Depth, index) ?? paper.IsParentHovered;
                if (builder.Navigation != null) rowBox.BackgroundColor(hovered ? RowHover(theme) : Color.Transparent);
                Color txt = RowText(theme, true, hovered);

                if (theme.Menus.HasValue)
                    DesktopLeading(paper, $"{id}_ico_{index}", font, theme, txt, false, Icon);
                else if (!string.IsNullOrEmpty(Icon))
                    paper.Box($"{id}_ico_{index}")
                        .Width(IconSize).Height(RowHeight)
                        .Text(Icon, font).TextColor(txt)
                        .FontSize(RowFont).Alignment(TextAlignment.MiddleCenter);

                paper.Box($"{id}_l_{index}")
                    .Width(UnitValue.Stretch()).Height(RowHeight)
                    .Text(Label, font).TextColor(txt).Clip()
                    .FontSize(RowFont).Alignment(TextAlignment.MiddleLeft);

                Color arrow = txt;
                using (paper.Box($"{id}_a_{index}").Width(12).Height(RowHeight).IsNotInteractable().Enter())
                    paper.Draw((canvas, rect) => ContextMenu.DrawChevron(canvas, rect, arrow));

                if ((builder.Navigation?.Open(builder.Depth, index) ?? paper.IsParentHovered) && Sub != null)
                {
                    // The submenu is a self-directed child of this row, so its X is measured from the
                    // row's border box (offset by the row's left padding). It overlaps the row's edge by
                    // 1px so the mouse can cross from row to submenu without passing a dead strip.
                    // Both offsets follow the current parent and child widths.
                    //
                    // Choose the side from the row's on-screen position (captured last frame): flip left
                    // only when a right-opening submenu would run off the right edge AND there's room on
                    // the left - otherwise keep right and let ClampToScreen handle it. Rendered one layer
                    // above so the submenu sits on top of every row in this panel.
                    var row = paper.GetElementStorage<Rect>(paper.CurrentParent, "srect", default);
                    Paper boundsPaper = builder.BoundsPaper ?? paper;
                    Sub.WidthCache = builder.Navigation?.WidthAt(builder.Depth + 1)
                        ?? paper.GetElementStorage<MenuWidthCache>("submenuWidth");
                    if (Sub.WidthCache == null)
                    {
                        Sub.WidthCache = new MenuWidthCache();
                        paper.SetElementStorage("submenuWidth", Sub.WidthCache);
                    }
                    float subWidth = ContextMenu.GetMenuWidth(boundsPaper, Sub, theme, font);
                    var boundsOrigin = !ReferenceEquals(boundsPaper, paper) && ContextMenu.MapPointBetweenPapers != null
                        ? ContextMenu.MapPointBetweenPapers(boundsPaper, paper, 0, 0) : (0f, 0f);
                    bool flipLeft = row.Max.X + subWidth > boundsOrigin.Item1 + boundsPaper.Width - 4f
                                 && row.Min.X - subWidth >= boundsOrigin.Item1 + 4f;
                    float subX = flipLeft ? 1f - subWidth - RowPadX
                                          : (float)row.Size.X - RowPadX - 1f;
                    float subY = 0;
                    if (theme.Menus.HasValue)
                    {
                        float subHeight = MathF.Min(ContextMenu.GetContentHeight(Sub) + 8, MathF.Max(1, boundsPaper.Height - 8));
                        subY = Math.Clamp((float)row.Min.Y, boundsOrigin.Item2 + 4,
                            MathF.Max(boundsOrigin.Item2 + 4, boundsOrigin.Item2 + boundsPaper.Height - subHeight - 4)) - (float)row.Min.Y;
                    }
                    ContextMenu.RenderMenu(paper, $"{id}_s_{index}", Sub, subX, subY, close, layer + 1,
                        clampToScreen: ReferenceEquals(boundsPaper, paper), navigation: builder.Navigation,
                        depth: builder.Depth + 1, boundsPaper: boundsPaper);
                }
            }
        }
    }
}

/// <summary>
/// Static context menu system for Origami. Only one context menu open at a time.
/// Renders on Layer.Topmost + 500 so it sits above most UI but below modals.
///
/// Use <see cref="RightClickMenu"/> inside any element's scope to attach a right-click menu.
/// Use <see cref="Show"/> to open programmatically at a position.
/// </summary>
public static class ContextMenu
{
    private static bool _isOpen;
    private static float _x, _y;
    private static Action<ContextBuilder>? _buildMenu;
    private static bool _openedThisDraw;
    private static IModal? _modalHandle;
    private static Paper? _ownerPaper;
    private static MenuNavigation? _navigation;

    // Nebula .w2menu container literals.
    private const float MenuRadius = 9f;
    private const float MenuPad = 5f;
    internal const float MenuWidth = 200f;

    public static bool IsOpen => _isOpen;

    /// <summary>
    /// Maps a point from the paper that opened the menu into another paper's local space.
    /// The editor sets this so a menu opened on the main window can be painted onto detached
    /// native windows that would otherwise cover it.
    /// </summary>
    public static Func<Paper, Paper, float, float, (float x, float y)>? MapPointBetweenPapers { get; set; }

    /// <summary>Optional host hit test for popup hover. Mirrored surfaces may hold stale pointers;
    /// only the native surface actually under the pointer should change shared menu state.</summary>
    public static Func<Paper, bool>? PointerSurfaceFilter { get; set; }

    /// <summary>True while a menu is open and drawn on every Paper surface (not pinned to one window).</summary>
    public static bool SpreadsToAllSurfaces => _isOpen && _modalHandle != null;

    /// <summary>Open a context menu at the given screen position.</summary>
    public static void Show(float x, float y, Action<ContextBuilder> build)
    {
        Close(); // close any existing
        _openedThisDraw = true; // an explicit open counts as this frame's open (dedups with RightClickMenu)
        _isOpen = true;
        _x = x;
        _y = y;
        _buildMenu = build;
        _ownerPaper = Origami.ActivePaper;
        _navigation = Origami.PopupTheme.Menus.HasValue ? new MenuNavigation() : null;
        _navigation?.RememberFocus(_ownerPaper);

        _modalHandle = new CustomDrawModal((paper, layer, _) => DrawMenu(paper, layer))
        {
            CloseOnBackdrop = true,
            CloseOnEscape = _navigation == null,
            BackdropAlpha = 0,
            BackdropFocusable = _navigation == null,
            Closed = Close,
        };
        // Unowned: each editor surface draws the menu in its own framebuffer. Native detached
        // windows sit above the main HWND, so pinning to the opener would leave the menu covered.
        Modal.Push(_modalHandle);
    }

    /// <summary>Open a context menu at the active paper's current pointer position.</summary>
    public static void ShowAtPointer(Action<ContextBuilder> build)
    {
        var paper = Origami.ActivePaper;
        if (paper == null) { Show(0, 0, build); return; }
        Show((float)paper.PointerPos.X, (float)paper.PointerPos.Y, build);
    }

    /// <summary>Close the current context menu.</summary>
    public static void Close()
    {
        _navigation?.ReleaseFocus();
        _navigation = null;
        if (_modalHandle != null)
        {
            Modal.Remove(_modalHandle);
            _modalHandle = null;
        }
        _isOpen = false;
        _buildMenu = null;
        _ownerPaper = null;
    }

    /// <summary>
    /// Attach a right-click context menu to the current parent element.
    /// Call this inside an element's Enter() scope.
    /// </summary>
    public static void RightClickMenu(Paper paper, string id, Action<ContextBuilder> build)
    {
        paper.CurrentParent.Data.OnRightClick += e =>
        {
            if (_openedThisDraw) return;
            _openedThisDraw = true;
            Show((float)paper.PointerPos.X, (float)paper.PointerPos.Y, build);
        };
    }

    /// <summary>Call once per frame to reset dedup state. The actual rendering is done by the modal system.</summary>
    public static void Tick()
    {
        _openedThisDraw = false;
    }

    /// <summary>Called by the modal system via CustomDrawModal.</summary>
    private static void DrawMenu(Paper paper, int layer)
    {
        if (!_isOpen || _buildMenu == null) return;

        paper.CaptureKeyboard();

        var builder = new ContextBuilder();
        _buildMenu(builder);
        builder.WidthCache = _navigation?.WidthAt(0);

        float x = _x, y = _y;
        bool foreign = _ownerPaper != null && !ReferenceEquals(paper, _ownerPaper);
        if (_ownerPaper != null && Origami.Current.Menus.HasValue)
        {
            if (Origami.Current.Font == null) return;
            float width = GetMenuWidth(_ownerPaper, builder, Origami.Current, Origami.Current.Font);
            float height = MathF.Min(GetContentHeight(builder) + 8, MathF.Max(1, _ownerPaper.Height - 8));
            x = Math.Clamp(x, 4, MathF.Max(4, _ownerPaper.Width - width - 4));
            y = Math.Clamp(y, 4, MathF.Max(4, _ownerPaper.Height - height - 4));
        }
        if (foreign)
        {
            if (MapPointBetweenPapers == null || _ownerPaper == null) return;
            (x, y) = MapPointBetweenPapers(_ownerPaper, paper, x, y);
        }

        RenderMenu(paper, "octx", builder, x, y, Close, layer: layer, clampToScreen: !foreign,
            navigation: _navigation, boundsPaper: _ownerPaper);
    }

    /// <summary>Render a positioned menu panel at the given screen position. Used for popups and submenus.</summary>
    internal static void RenderMenu(Paper paper, string id, ContextBuilder builder, float x, float y,
        Action close, int layer = Layer.Topmost + 501, bool clampToScreen = true,
        MenuNavigation? navigation = null, int depth = 0, Paper? boundsPaper = null, Action<Rect>? arranged = null)
    {
        var theme = Origami.Current;
        var font = theme.Font;
        if (font == null) return;

        if (depth == 0 && navigation != null && !navigation.Process(paper, builder, close)) return;
        builder.Navigation = navigation;
        builder.Depth = depth;
        builder.BoundsPaper = boundsPaper ?? paper;
        float contentHeight = GetContentHeight(builder);
        float maximumHeight = MathF.Max(1, builder.BoundsPaper.Height - 8);
        bool bounded = theme.Menus.HasValue && contentHeight + 8 > maximumHeight;

        var menuBox = paper.Box($"{id}_menu")
            .PositionType(PositionType.SelfDirected)
            .Position(x, y);
        if (theme.Menus.HasValue && builder.WidthCache == null)
        {
            builder.WidthCache = navigation?.WidthAt(depth)
                ?? paper.GetElementStorage<MenuWidthCache>(menuBox._handle, "menuWidth");
            if (builder.WidthCache == null)
            {
                builder.WidthCache = new MenuWidthCache();
                paper.SetElementStorage(menuBox._handle, "menuWidth", builder.WidthCache);
            }
        }
        menuBox.Width(GetMenuWidth(builder.BoundsPaper, builder, theme, font)).Height(bounded ? maximumHeight : UnitValue.Auto)
            .BackgroundColor(theme.Menus?.Background ?? theme.Popover)
            .BorderColor(theme.Menus?.Border ?? theme.BorderStrong).BorderWidth(1)
            .Rounded(theme.Menus?.Rounding ?? MenuRadius)
            .Layer(layer)
            .DropShadow(0, theme.Menus.HasValue ? 3 : 14, theme.Menus.HasValue ? 8 : 40, 0, theme.Shadow)
            .StopEventPropagation();
        if (clampToScreen)
            menuBox.ClampToScreen();
        if (arranged != null) menuBox.OnPostLayout((_, rect) => arranged(rect));

        using (menuBox.Enter())
        {
            if (depth == 0) navigation?.Focus(paper, paper.CurrentParent);
            if (bounded) DrawBoundedMenuBody(paper, id, builder, font, theme, close, layer, contentHeight, maximumHeight);
            else
            {
                if (navigation != null)
                {
                    var scroll = navigation.ScrollAt(depth);
                    scroll.Offset = scroll.Maximum = 0;
                    navigation.RevealAt(depth);
                }
                else if (paper.GetElementStorage<MenuScroll>("menuScroll") is { } scroll)
                {
                    scroll.Offset = scroll.Maximum = 0;
                    scroll.Leave();
                }
                DrawMenuBody(paper, id, builder, font, theme, close, layer);
            }
        }
    }

    /// <summary>
    /// Render a static, inline <c>.w2menu</c> panel in the current layout flow (for showcases /
    /// documentation). Mirrors <see cref="Toasts.Preview"/>: normal flow, fixed width, no backdrop
    /// or positioning. Rows still hover; clicks fire their callbacks but there is nothing to close.
    /// </summary>
    public static void Preview(Paper paper, string id, Action<ContextBuilder> build)
    {
        var theme = Origami.Current;
        var font = theme.Font;
        if (font == null) return;

        var builder = new ContextBuilder();
        build(builder);

        var menuBox = paper.Box($"{id}_menu")
            .Width(GetMenuWidth(paper, builder, theme, font)).Height(UnitValue.Auto)
            .BackgroundColor(theme.Menus?.Background ?? theme.Popover)
            .BorderColor(theme.Menus?.Border ?? theme.BorderStrong).BorderWidth(1)
            .Rounded(theme.Menus?.Rounding ?? MenuRadius)
            .DropShadow(0, theme.Menus.HasValue ? 3 : 14, theme.Menus.HasValue ? 8 : 40, 0, theme.Shadow);

        using (menuBox.Enter())
            DrawMenuBody(paper, id, builder, font, theme, static () => { }, Layer.Topmost + 500);
    }

    /// <summary>
    /// Render a menu panel at an absolute screen position on a caller-chosen <paramref name="layer"/>,
    /// with a caller-owned <paramref name="close"/> action. Unlike <see cref="Show"/> this adds no
    /// backdrop and no modal push, so the caller controls focus/dismissal and stacking. Used by menu
    /// bars that keep the bar itself above their own dropdown.
    /// </summary>
    public static void Panel(Paper paper, string id, float x, float y, int layer,
        Action close, Action<ContextBuilder> build, bool clampToScreen = true)
    {
        var builder = new ContextBuilder();
        build(builder);
        RenderMenu(paper, id, builder, x, y, close, layer, clampToScreen);
    }

    internal static void InteractivePanel(Paper paper, string id, float x, float y, int layer,
        Action close, Action<ContextBuilder> build, MenuNavigation navigation, bool clampToScreen = true,
        Paper? boundsPaper = null, Action<Rect>? arranged = null)
    {
        var builder = new ContextBuilder();
        build(builder);
        RenderMenu(paper, id, builder, x, y, close, layer, clampToScreen, navigation,
            boundsPaper: boundsPaper, arranged: arranged);
    }

    /// <summary>Draw the padded item column shared by the popup and inline previews.</summary>
    private static void DrawMenuBody(Paper paper, string id, ContextBuilder builder,
        Scribe.FontFile font, OrigamiTheme theme, Action close, int layer)
    {
        using (paper.Column($"{id}_col")
            .Padding(theme.Menus.HasValue ? 0 : MenuPad, theme.Menus.HasValue ? 0 : MenuPad,
                theme.Menus.HasValue ? 3 : MenuPad, theme.Menus.HasValue ? 3 : MenuPad)
            .Height(UnitValue.Auto)
            .Enter())
        {
            for (int i = 0; i < builder.Items.Count; i++)
                builder.Items[i].Draw(paper, id, i, font, theme, close, layer, builder);
        }
    }

    private static float ItemHeight(IContextItem item) => item switch
    {
        ContextBuilder.CtxHeader => 24,
        ContextBuilder.CtxSeparator => 9,
        ContextBuilder.CtxTitle => ContextBuilder.RowHeight + 8,
        _ => ContextBuilder.RowHeight,
    };

    internal static float GetContentHeight(ContextBuilder builder)
    {
        float height = 0;
        for (int i = 0; i < builder.Items.Count; i++) height += ItemHeight(builder.Items[i]);
        return height;
    }

    private static void DrawBoundedMenuBody(Paper paper, string id, ContextBuilder builder,
        Scribe.FontFile font, OrigamiTheme theme, Action close, int layer, float contentHeight, float height)
    {
        float indicatorHeight = MathF.Min(16, MathF.Max(0, (height - 8 - ContextBuilder.RowHeight) * 0.5f));
        float viewportHeight = MathF.Max(1, height - 8 - 2 * indicatorHeight);
        var scroll = builder.Navigation?.ScrollAt(builder.Depth)
            ?? paper.GetElementStorage<MenuScroll>("menuScroll");
        if (scroll == null)
        {
            scroll = new MenuScroll();
            paper.SetElementStorage("menuScroll", scroll);
        }
        scroll.Maximum = MathF.Max(0, contentHeight - viewportHeight);
        scroll.Offset = Math.Clamp(scroll.Offset, 0, scroll.Maximum);
        int reveal = builder.Navigation?.RevealAt(builder.Depth) ?? -1;
        if (reveal >= 0)
        {
            float top = 0;
            for (int i = 0; i < reveal; i++) top += ItemHeight(builder.Items[i]);
            float bottom = top + ItemHeight(builder.Items[reveal]);
            if (top < scroll.Offset) scroll.Offset = top;
            else if (bottom > scroll.Offset + viewportHeight) scroll.Offset = bottom - viewportHeight;
            scroll.Offset = Math.Clamp(scroll.Offset, 0, scroll.Maximum);
        }
        void Scrolled() => builder.Navigation?.Scrolled(builder.Depth);
        using (paper.Column($"{id}_col").Height(MathF.Max(0, height - 2)).Padding(0, 0, 3, 3).Enter())
        {
            DrawScrollIndicator(paper, id + "_scroll_up", scroll, -1, indicatorHeight, theme, Scrolled);
            using (paper.Box($"{id}_viewport").Height(viewportHeight).Clip().IsNotFocusable()
                .OnScroll(e => {
                    if (scroll.By(-(float)e.Delta * ContextBuilder.RowHeight * 3)) Scrolled();
                    e.StopPropagation();
                }).Enter())
            {
                float top = 0;
                for (int i = 0; i < builder.Items.Count; i++)
                {
                    float rowHeight = ItemHeight(builder.Items[i]);
                    if (top < scroll.Offset + viewportHeight && top + rowHeight > scroll.Offset)
                        using (paper.Column($"{id}_slice_{i}").PositionType(PositionType.SelfDirected)
                            .Position(0, top - scroll.Offset).Width(UnitValue.Stretch()).Height(rowHeight).Enter())
                            builder.Items[i].Draw(paper, id, i, font, theme, close, layer, builder);
                    top += rowHeight;
                }
            }
            DrawScrollIndicator(paper, id + "_scroll_down", scroll, 1, indicatorHeight, theme, Scrolled);
        }
    }

    private static void DrawScrollIndicator(Paper paper, string id, MenuScroll scroll, int direction,
        float height, OrigamiTheme theme, Action scrolled)
    {
        bool enabled = direction < 0 ? scroll.Offset > 0 : scroll.Offset < scroll.Maximum;
        bool pointerSurface = PointerSurfaceFilter?.Invoke(paper) ?? true;
        var row = paper.Box(id).Height(height).IsNotFocusable();
        if (enabled)
        {
            if (pointerSurface) row.Hovered.BackgroundColor(theme.Menus!.Value.HoverBackground).End();
            row.OnClick(_ => { if (pointerSurface && scroll.By(direction * ContextBuilder.RowHeight)) scrolled(); })
                .OnHover(_ => { if (pointerSurface && scroll.Hover(paper.Time, direction, ContextBuilder.RowHeight)) scrolled(); })
                .OnLeave(_ => { if (pointerSurface) scroll.Leave(); });
        }
        using (row.Enter())
            paper.Draw((canvas, rect) => {
                if (!enabled) return;
                float x = (float)(rect.Min.X + rect.Size.X / 2), y = (float)(rect.Min.Y + rect.Size.Y / 2);
                canvas.SaveState(); canvas.SetStrokeColor(theme.Menus!.Value.Text);
                canvas.SetStrokeWidth(1.5f); canvas.BeginPath();
                canvas.MoveTo(x - 3, y - direction * 1.5f);
                canvas.LineTo(x, y + direction * 1.5f);
                canvas.LineTo(x + 3, y - direction * 1.5f);
                canvas.Stroke(); canvas.RestoreState();
            });
    }

    // Measure the largest row, including separate shortcut/check/arrow columns.
    // Session/element-owned measurements compare exact strings; builders and callbacks stay live.
    internal static float GetMenuWidth(Paper paper, ContextBuilder builder,
        OrigamiTheme theme, Scribe.FontFile font)
    {
        if (theme.Menus is not { } style) return MenuWidth;
        if (builder.MeasuredWidth is { } measured) return measured;
        var cache = builder.WidthCache;
        cache?.Begin(paper, font, style.FontSize, builder.Items.Count);
        float width = style.MinimumWidth;
        for (int i = 0; i < builder.Items.Count; i++)
        {
            string label;
            string shortcut = "";
            float trailing = 0;
            switch (builder.Items[i])
            {
                case ContextBuilder.CtxItem item:
                    label = item.Label;
                    if (!string.IsNullOrEmpty(item.Shortcut))
                    { shortcut = item.Shortcut; trailing = 8; }
                    break;
                case ContextBuilder.CtxSubmenu submenu:
                    label = submenu.Label; trailing = 8 + 12; break;
                case ContextBuilder.CtxToggle toggle: label = toggle.Label; break;
                case ContextBuilder.CtxTitle title: label = title.Text; break;
                case ContextBuilder.CtxHeader header: label = header.Text; break;
                default: cache?.Forget(i); continue;
            }
            float textWidth = cache != null ? cache.Measure(i, label, shortcut)
                : (float)paper.MeasureText(label, style.FontSize, font).X
                    + (shortcut.Length > 0 ? (float)paper.MeasureText(shortcut, style.FontSize, font).X : 0);
            width = MathF.Max(width, 2 + 16 + 22 + 8
                + textWidth + trailing);
        }
        float result = MathF.Min(MathF.Ceiling(width), MathF.Max(1, paper.Width - 8));
        builder.MeasuredWidth = result;
        return result;
    }

    /// <summary>Draw a small trailing chevron (submenu affordance) centred in its box.</summary>
    internal static void DrawChevron(Canvas canvas, Rect rect, Color color)
    {
        float cx = (float)(rect.Min.X + rect.Size.X * 0.5);
        float cy = (float)(rect.Min.Y + rect.Size.Y * 0.5);

        canvas.SaveState();
        canvas.SetStrokeColor(color);
        canvas.SetStrokeWidth(1.5f);
        canvas.SetStrokeCap(EndCapStyle.Round);
        canvas.BeginPath();
        canvas.MoveTo(cx - 1.5f, cy - 3.2f);
        canvas.LineTo(cx + 2f, cy);
        canvas.LineTo(cx - 1.5f, cy + 3.2f);
        canvas.Stroke();
        canvas.RestoreState();
    }
}
