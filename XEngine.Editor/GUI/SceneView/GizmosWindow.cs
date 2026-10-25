using System;
using System.Collections.Generic;

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;

using Color = System.Drawing.Color;

namespace XEngine.Editor.GUI.SceneView;

/// <summary>
/// Unity's Gizmos dropdown, shared verbatim by the Scene and Game views: the checkbox+slider header
/// block, a search box, and a component table with independent <c>icon</c> and <c>gizmo</c> columns
/// grouped into built-in components and project scripts.
///
/// Rendered as an anchored popover (the same construction as the Inspector's Add Component popup)
/// rather than a context menu, because a context menu row cannot host a slider. Only one is open at
/// a time across all views, keyed by the panel that opened it.
/// </summary>
internal static class GizmosWindow
{
    private const float Width = 300f;
    private const float RowHeight = 22f;
    private const float TableHeight = 220f;
    private const float LabelWidth = 128f;
    private const float ColumnWidth = 42f;
    private const float Pad = 6f;

    private static object? _owner;
    private static string _search = "";

    /// <summary>Whether the window is currently open for <paramref name="owner"/>'s toolbar.</summary>
    public static bool IsOpenFor(object? owner) => owner != null && ReferenceEquals(_owner, owner);

    /// <summary>Open for this owner, or close if it is already the open one.</summary>
    public static void Toggle(object? owner)
    {
        if (owner == null) return;
        _owner = IsOpenFor(owner) ? null : owner;
        _search = "";
    }

    public static void Close() => _owner = null;

    /// <summary>
    /// Draw the popover. Call from inside the trigger element's <c>Enter()</c> scope, guarded by
    /// <see cref="IsOpenFor"/>. <paramref name="onChanged"/> repaints the owning viewport.
    /// </summary>
    public static void Render(Paper paper, Scribe.FontFile font, string id, float triggerHeight,
        Action onChanged)
    {
        if (paper.IsKeyPressed(PaperKey.Escape))
        {
            Close();
            return;
        }

        // Fullscreen invisible click-catcher: clicking anywhere outside closes, matching how
        // Origami's own dropdowns dismiss (DropdownInternal.RenderBackdrop).
        paper.Box($"{id}_backdrop")
            .PositionType(PositionType.SelfDirected)
            .Position(-9999, -9999)
            .Size(99999, 99999)
            .Layer(Layer.Overlay)
            .StopEventPropagation()
            .OnClick(0, (_, _) => Close());

        using (paper.Column($"{id}_pop")
            .PositionType(PositionType.SelfDirected)
            .Position(0, triggerHeight + 4f)
            .Width(Width)
            .Height(UnitValue.Auto)
            .BackgroundColor(EditorTheme.Popover)
            .BorderColor(EditorTheme.BorderStrong).BorderWidth(1)
            .DropShadow(0, 14, 40, -6, EditorTheme.Shadow)
            .Rounded(EditorTheme.Roundness + 2f)
            .Padding(Pad, Pad, Pad, Pad)
            .ColBetween(2f)
            .HookToParent()
            .Layer(Layer.Topmost)
            .ClampToScreen()
            .StopEventPropagation()
            .Enter())
        {
            DrawHeaderBlock(paper, font, id, onChanged);
            Separator(paper, $"{id}_sep");
            DrawSearch(paper, id);
            DrawColumnHeaders(paper, font, id, onChanged);
            DrawTypeTable(paper, font, id, onChanged);
        }
    }

    // ── Header: the checkbox + slider rows ────────────────────────────────────────────

    private static void DrawHeaderBlock(Paper paper, Scribe.FontFile font, string id, Action onChanged)
    {
        SliderRow(paper, font, $"{id}_icons", Loc.Get("gizmos.icons_3d"),
            GizmoSettings.Show3DIcons,
            v => { GizmoSettings.Show3DIcons = v; onChanged(); },
            GizmoSettings.IconScale,
            v => { GizmoSettings.IconScale = v; onChanged(); },
            GizmoSettings.IconScaleMin, GizmoSettings.IconScaleMax,
            Loc.Get("gizmos.icons_3d_tooltip"));

        SliderRow(paper, font, $"{id}_fade", Loc.Get("gizmos.fade"),
            GizmoSettings.FadeGizmos,
            v => { GizmoSettings.FadeGizmos = v; onChanged(); },
            GizmoSettings.FadeSize,
            v => { GizmoSettings.FadeSize = v; onChanged(); },
            GizmoSettings.FadeSizeMin, GizmoSettings.FadeSizeMax,
            Loc.Get("gizmos.fade_tooltip"));

        // Not a Unity row: authored gizmo sizes are fixed world sizes, so a scene whose content is a
        // few units across needs a way to pull them back. Scaled about each component's own origin.
        SliderRow(paper, font, $"{id}_scale", Loc.Get("gizmos.scale"),
            value: null, setEnabled: null,
            GizmoSettings.GizmoScale,
            v => { GizmoSettings.GizmoScale = v; onChanged(); },
            GizmoSettings.GizmoScaleMin, GizmoSettings.GizmoScaleMax,
            Loc.Get("gizmos.scale_tooltip"));

        CheckRow(paper, font, $"{id}_outline", Loc.Get("gizmos.selection_outline"),
            GizmoSettings.SelectionOutline,
            v => { GizmoSettings.SelectionOutline = v; onChanged(); },
            Loc.Get("gizmos.selection_outline_tooltip"));

        CheckRow(paper, font, $"{id}_wire", Loc.Get("gizmos.selection_wire"),
            GizmoSettings.SelectionWire,
            v => { GizmoSettings.SelectionWire = v; onChanged(); },
            Loc.Get("gizmos.selection_wire_tooltip"));
    }

    /// <summary>
    /// A label + optional leading checkbox + trailing slider. <paramref name="value"/> null renders
    /// the row without a checkbox (the slider is always live), which is how Gizmo Scale appears.
    /// </summary>
    private static void SliderRow(Paper paper, Scribe.FontFile font, string id, string label,
        bool? value, Action<bool>? setEnabled,
        float slider, Action<float> setSlider, float min, float max, string tooltip)
    {
        using (paper.Row(id).Height(RowHeight).RowBetween(4f).Enter())
        {
            if (value is { } on && setEnabled != null)
            {
                using (paper.Box($"{id}_cb").Width(16).Height(RowHeight).Tooltip(tooltip).Enter())
                    Origami.Checkbox(paper, $"{id}_cbx", on, setEnabled).Small().NoLabel().Show();
            }
            else
            {
                paper.Box($"{id}_cbspace").Width(16).Height(RowHeight).IsNotInteractable();
            }

            paper.Box($"{id}_lbl")
                .Width(LabelWidth - 20f).Height(RowHeight).IsNotInteractable()
                .Text(label, font).TextColor(EditorTheme.Ink500)
                .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleLeft);

            using (paper.Box($"{id}_sl").Width(UnitValue.Stretch()).Height(RowHeight)
                .Tooltip(tooltip).Enter())
            {
                Origami.Slider(paper, $"{id}_slx", slider, setSlider, min, max)
                    .Small()
                    .Width(UnitValue.Stretch())
                    .ShowValue(false)
                    .Disabled(value == false)
                    .Show();
            }
        }
    }

    private static void CheckRow(Paper paper, Scribe.FontFile font, string id, string label,
        bool value, Action<bool> setter, string tooltip)
    {
        using (paper.Row(id).Height(RowHeight).RowBetween(4f).Enter())
        {
            using (paper.Box($"{id}_cb").Width(16).Height(RowHeight).Tooltip(tooltip).Enter())
                Origami.Checkbox(paper, $"{id}_cbx", value, setter).Small().NoLabel().Show();

            paper.Box($"{id}_lbl")
                .Width(UnitValue.Stretch()).Height(RowHeight).IsNotInteractable()
                .Text(label, font).TextColor(EditorTheme.Ink500)
                .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleLeft);
        }
    }

    // ── Component table ──────────────────────────────────────────────────────────────

    private static void DrawSearch(Paper paper, string id)
    {
        using (paper.Row($"{id}_searchrow").Height(26f).Enter())
            Origami.SearchField(paper, $"{id}_search", _search, v => _search = v,
                Loc.Get("gizmos.search")).Show();
    }

    private static void DrawColumnHeaders(Paper paper, Scribe.FontFile font, string id, Action onChanged)
    {
        using (paper.Row($"{id}_cols").Height(RowHeight).RowBetween(4f).Enter())
        {
            paper.Box($"{id}_cols_all")
                .Width(UnitValue.Stretch()).Height(RowHeight)
                .Text(Loc.Get("gizmos.all_none"), font)
                .TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft)
                .Tooltip(Loc.Get("gizmos.all_none_tooltip"))
                .OnClick(0, (_, _) =>
                {
                    bool anyOn = false;
                    foreach (GizmoSettings.TypeEntry e in GizmoSettings.Types)
                        if (e.GizmoVisible || e.IconVisible) { anyOn = true; break; }
                    GizmoSettings.SetAllVisible(!anyOn);
                    onChanged();
                });

            ColumnLabel(paper, font, $"{id}_cols_icon", Loc.Get("gizmos.column_icon"));
            ColumnLabel(paper, font, $"{id}_cols_gizmo", Loc.Get("gizmos.column_gizmo"));
        }
    }

    private static void ColumnLabel(Paper paper, Scribe.FontFile font, string id, string text)
        => paper.Box(id)
            .Width(ColumnWidth).Height(RowHeight).IsNotInteractable()
            .Text(text, font).TextColor(EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter);

    private static void DrawTypeTable(Paper paper, Scribe.FontFile font, string id, Action onChanged)
    {
        IReadOnlyList<GizmoSettings.TypeEntry> types = GizmoSettings.Types;

        Origami.ScrollView(paper, $"{id}_table", Width - Pad * 2, TableHeight)
            .Padding(0)
            .Body(() =>
            {
                bool any = false;
                bool scriptHeaderDrawn = false;
                bool builtinHeaderDrawn = false;

                for (int i = 0; i < types.Count; i++)
                {
                    GizmoSettings.TypeEntry entry = types[i];
                    if (_search.Length > 0
                        && entry.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    if (entry.IsScript && !scriptHeaderDrawn)
                    {
                        scriptHeaderDrawn = true;
                        SectionHeader(paper, font, $"{id}_h_scripts", Loc.Get("gizmos.section_scripts"));
                    }
                    else if (!entry.IsScript && !builtinHeaderDrawn)
                    {
                        builtinHeaderDrawn = true;
                        SectionHeader(paper, font, $"{id}_h_builtin", Loc.Get("gizmos.section_builtin"));
                    }

                    any = true;
                    TypeRow(paper, font, $"{id}_t{i}", entry, onChanged);
                }

                if (!any)
                    paper.Box($"{id}_empty")
                        .Height(RowHeight * 2).IsNotInteractable()
                        .Text(Loc.Get(types.Count == 0 ? "gizmos.no_components" : "gizmos.no_matches"), font)
                        .TextColor(EditorTheme.Ink300)
                        .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter);
            });
    }

    private static void SectionHeader(Paper paper, Scribe.FontFile font, string id, string text)
        => paper.Box(id)
            .Height(RowHeight).IsNotInteractable()
            .Text(text, font).TextColor(EditorTheme.Ink400)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

    private static void TypeRow(Paper paper, Scribe.FontFile font, string id,
        GizmoSettings.TypeEntry entry, Action onChanged)
    {
        using (paper.Row(id).Height(RowHeight).RowBetween(4f)
            .Hovered.BackgroundColor(EditorTheme.Hover).End()
            .Rounded(EditorTheme.Roundness)
            .Enter())
        {
            paper.Box($"{id}_n")
                .Width(UnitValue.Stretch()).Height(RowHeight).IsNotInteractable()
                .Text(entry.Name, font).TextColor(EditorTheme.Ink500)
                .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleLeft);

            // The icon column stays empty for types that never drew one, so the table says which
            // components actually have a billboard rather than offering a dead checkbox.
            using (paper.Box($"{id}_i").Width(ColumnWidth).Height(RowHeight).Enter())
            {
                if (entry.HasIcon)
                    Origami.Checkbox(paper, $"{id}_ix", entry.IconVisible, v =>
                    {
                        GizmoSettings.SetIconVisible(entry.Name, v);
                        onChanged();
                    }).Small().NoLabel().Show();
            }

            using (paper.Box($"{id}_g").Width(ColumnWidth).Height(RowHeight).Enter())
                Origami.Checkbox(paper, $"{id}_gx", entry.GizmoVisible, v =>
                {
                    GizmoSettings.SetGizmoVisible(entry.Name, v);
                    onChanged();
                }).Small().NoLabel().Show();
        }
    }

    private static void Separator(Paper paper, string id)
        => paper.Box(id).Height(1).Width(UnitValue.Stretch())
            .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();
}
