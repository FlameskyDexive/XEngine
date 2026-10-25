using System;

using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;

using Color = System.Drawing.Color;

namespace XEngine.Editor.GUI.SceneView;

/// <summary>
/// The strip above the Scene view viewport: handle pivot/orientation, grid and snap increments, the
/// 2D / audio / effects view toggles, and the Gizmos menu. Split out of
/// <see cref="SceneViewPanel"/> so the panel keeps to rendering and input.
/// </summary>
internal static class SceneViewToolbar
{
    private const float ControlHeight = 20f;

    private static string DrawModeLabel(Runtime.Rendering.DebugViewMode mode) => mode switch
    {
        Runtime.Rendering.DebugViewMode.Wireframe => "Wireframe",
        Runtime.Rendering.DebugViewMode.Overdraw => "Overdraw",
        Runtime.Rendering.DebugViewMode.Unlit => "Unlit",
        _ => "Shaded",
    };

    public static void Draw(Paper paper, Scribe.FontFile font, SceneViewPanel panel,
        SceneViewSettings settings, SceneOverlayHost overlays, float height)
    {
        using (paper.Row("sv_tb").Height(height).Padding(6, 6, 0, 0).RowBetween(3)
            .BackgroundColor(EditorTheme.Neutral200).Clip().Enter())
        {
            // Draw-mode dropdown (Unity's leftmost Shaded/Wireframe selector).
            Menu(paper, font, "sv_tb_drawmode",
                DrawModeLabel(settings.DrawMode),
                SceneViewIcons.Pivot, 92f, "Scene draw mode", menu =>
                {
                    menu.Header("Draw Mode");
                    menu.Item("Shaded", () =>
                    {
                        settings.DrawMode = Runtime.Rendering.DebugViewMode.Shaded;
                        panel.InvalidateRender();
                    }, on: settings.DrawMode == Runtime.Rendering.DebugViewMode.Shaded);
                    menu.Item("Wireframe", () =>
                    {
                        settings.DrawMode = Runtime.Rendering.DebugViewMode.Wireframe;
                        panel.InvalidateRender();
                    }, on: settings.DrawMode == Runtime.Rendering.DebugViewMode.Wireframe);
                    menu.Item("Overdraw", () =>
                    {
                        settings.DrawMode = Runtime.Rendering.DebugViewMode.Overdraw;
                        panel.InvalidateRender();
                    }, on: settings.DrawMode == Runtime.Rendering.DebugViewMode.Overdraw);
                    menu.Item("Unlit", () =>
                    {
                        settings.DrawMode = Runtime.Rendering.DebugViewMode.Unlit;
                        panel.InvalidateRender();
                    }, on: settings.DrawMode == Runtime.Rendering.DebugViewMode.Unlit);
                });

            Divider(paper, "sv_tb_d0");

            Menu(paper, font, "sv_tb_pivot",
                Loc.Get(settings.Pivot == PivotMode.Center ? "scene.pivot_center" : "scene.pivot_pivot"),
                SceneViewIcons.Pivot, 76f, Loc.Get("scene.pivot_tooltip"), menu =>
                {
                    menu.Header(Loc.Get("scene.pivot"));
                    menu.Item(Loc.Get("scene.pivot_center"), () => settings.Pivot = PivotMode.Center,
                        on: settings.Pivot == PivotMode.Center);
                    menu.Item(Loc.Get("scene.pivot_pivot"), () => settings.Pivot = PivotMode.Pivot,
                        on: settings.Pivot == PivotMode.Pivot);
                });

            Menu(paper, font, "sv_tb_orient",
                Loc.Get(settings.Orientation == HandleOrientation.Local ? "scene.local" : "scene.global"),
                settings.Orientation == HandleOrientation.Local ? SceneViewIcons.Local : SceneViewIcons.Global,
                76f, Loc.Get("scene.orientation_tooltip"), menu =>
                {
                    menu.Header(Loc.Get("scene.orientation"));
                    menu.Item(Loc.Get("scene.local"), () => settings.Orientation = HandleOrientation.Local,
                        on: settings.Orientation == HandleOrientation.Local);
                    menu.Item(Loc.Get("scene.global"), () => settings.Orientation = HandleOrientation.Global,
                        on: settings.Orientation == HandleOrientation.Global);
                });

            Divider(paper, "sv_tb_d1");

            IconToggle(paper, "sv_tb_grid", SceneViewIcons.Grid, settings.ShowGrid,
                Loc.Get("scene.show_grid"), () =>
                {
                    settings.ShowGrid = !settings.ShowGrid;
                    panel.InvalidateRender();
                });
            Number(paper, "sv_tb_gridsize", settings.GridSize, 46f, Loc.Get("scene.grid_size"),
                value => settings.GridSize = MathF.Max(0.001f, value));

            Divider(paper, "sv_tb_d2");

            IconToggle(paper, "sv_tb_snap", SceneViewIcons.Snap, settings.SnapEnabled,
                Loc.Get("scene.snap_tooltip"), () => settings.SnapEnabled = !settings.SnapEnabled);
            Number(paper, "sv_tb_snapmove", settings.MoveSnap, 46f, Loc.Get("scene.snap_move"),
                value => settings.MoveSnap = MathF.Max(0.0001f, value));
            Number(paper, "sv_tb_snaprot", settings.RotateSnap, 42f, Loc.Get("scene.snap_rotate"),
                value => settings.RotateSnap = MathF.Max(0.01f, value));
            Number(paper, "sv_tb_snapscale", settings.ScaleSnap, 42f, Loc.Get("scene.snap_scale"),
                value => settings.ScaleSnap = MathF.Max(0.0001f, value));

            Divider(paper, "sv_tb_d3");

            TextToggle(paper, font, "sv_tb_2d", "2D", settings.Is2D, Loc.Get("scene.2d_tooltip"),
                () =>
                {
                    settings.Is2D = !settings.Is2D;
                    if (settings.Is2D) settings.ViewOrthographic = true;
                    else settings.ViewOrthographic = false;
                    panel.InvalidateRender();
                });
            IconToggle(paper, "sv_tb_lighting",
                SceneViewIcons.Glyph(EditorIcons.Lightbulb),
                settings.SceneLightingEnabled, "Scene lighting", () =>
                {
                    settings.SceneLightingEnabled = !settings.SceneLightingEnabled;
                    panel.InvalidateRender();
                });
            IconToggle(paper, "sv_tb_audio",
                settings.AudioEnabled ? SceneViewIcons.Audio : SceneViewIcons.Glyph(EditorIcons.VolumeXmark),
                settings.AudioEnabled, Loc.Get("scene.audio_tooltip"), () =>
                {
                    settings.AudioEnabled = !settings.AudioEnabled;
                    ApplyAudio(settings);
                });
            TextToggle(paper, font, "sv_tb_fx", "fx", settings.EffectsEnabled, Loc.Get("scene.fx_tooltip"),
                () =>
                {
                    settings.EffectsEnabled = !settings.EffectsEnabled;
                    panel.InvalidateRender();
                });

            Divider(paper, "sv_tb_d4");

            GizmoSplitButton(paper, font, panel, settings);

            paper.Box("sv_tb_spacer");

            IconToggle(paper, "sv_tb_overlays", SceneViewIcons.Overlays, false,
                Loc.Get("overlay.menu"), () => Origami.ContextMenu(
                    (float)paper.PointerPos.X, (float)paper.PointerPos.Y, menu =>
                    {
                        menu.Header(Loc.Get("overlay.menu"));
                        foreach (var overlay in overlays.Overlays)
                        {
                            var captured = overlay;
                            menu.Toggle(overlay.Title, () => captured.Visible = !captured.Visible,
                                () => captured.Visible, enabled: overlay.IsAvailable?.Invoke() ?? true);
                        }
                        menu.Separator();
                        menu.Item(Loc.Get("overlay.reset_layout"), overlays.ResetLayout,
                            icon: EditorIcons.RotateLeft);
                    }));
        }
    }

    /// <summary>Scene audio follows the toolbar toggle while the editor isn't playing.</summary>
    private static void ApplyAudio(SceneViewSettings settings)
    {
        EditorAudio.SceneViewAudible = settings.AudioEnabled;
        EditorAudio.Apply();
    }

    // ================================================================
    //  Grid & Snap overlay body (shares state with the toolbar)
    // ================================================================

    public static void DrawGridSnapBody(Paper paper, Scribe.FontFile font, SceneViewPanel panel,
        SceneViewSettings settings)
    {
        LabeledToggle(paper, font, "sv_gs_grid", Loc.Get("scene.show_grid"), settings.ShowGrid, () =>
        {
            settings.ShowGrid = !settings.ShowGrid;
            panel.InvalidateRender();
        });
        LabeledNumber(paper, font, "sv_gs_size", Loc.Get("scene.grid_size"), settings.GridSize,
            value => settings.GridSize = MathF.Max(0.001f, value));

        paper.Box("sv_gs_div").Height(1).Margin(0, 0, 3, 3)
            .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

        LabeledToggle(paper, font, "sv_gs_snap", Loc.Get("scene.snap"), settings.SnapEnabled,
            () => settings.SnapEnabled = !settings.SnapEnabled);
        LabeledNumber(paper, font, "sv_gs_move", Loc.Get("scene.snap_move"), settings.MoveSnap,
            value => settings.MoveSnap = MathF.Max(0.0001f, value));
        LabeledNumber(paper, font, "sv_gs_rot", Loc.Get("scene.snap_rotate"), settings.RotateSnap,
            value => settings.RotateSnap = MathF.Max(0.01f, value));
        LabeledNumber(paper, font, "sv_gs_scale", Loc.Get("scene.snap_scale"), settings.ScaleSnap,
            value => settings.ScaleSnap = MathF.Max(0.0001f, value));
    }

    private static void LabeledToggle(Paper paper, Scribe.FontFile font, string id, string label,
        bool value, Action onClick)
    {
        using (paper.Row(id).Height(22).RowBetween(6).Enter())
        {
            Label(paper, font, $"{id}_l", label);
            using (paper.Box($"{id}_c").Width(38).Height(22)
                .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
                Origami.Switch(paper, $"{id}_sw", value, _ => onClick()).NoLabel().Show();
        }
    }

    private static void LabeledNumber(Paper paper, Scribe.FontFile font, string id, string label,
        float value, Action<float> setter)
    {
        using (paper.Row(id).Height(24).RowBetween(6).Enter())
        {
            Label(paper, font, $"{id}_l", label);
            using (paper.Box($"{id}_c").Width(64).Height(ControlHeight)
                .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
                Origami.NumericField(paper, $"{id}_v", value, setter)
                    .Width(UnitValue.Stretch()).Height(ControlHeight).Format("0.###").Stepper(false).Show();
        }
    }

    private static void Label(Paper paper, Scribe.FontFile font, string id, string text)
        => paper.Box(id).Width(UnitValue.Stretch()).Height(22)
            .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
            .Text(text, font).TextColor(EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).TextTruncate();

    // ================================================================
    //  Toolbar primitives
    // ================================================================

    private static void Divider(Paper paper, string id)
        => paper.Box(id).Width(1).Height(14).Margin(3, 3, UnitValue.StretchOne, UnitValue.StretchOne)
            .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

    private static void IconToggle(Paper paper, string id, IOrigamiIcon icon,
        bool active, string tooltip, Action onClick)
    {
        Color color = active ? EditorTheme.Ink500 : EditorTheme.Ink300;
        paper.Box(id).Width(24).Height(ControlHeight).Rounded(5)
            .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
            .BackgroundColor(active ? EditorTheme.ToolbarActive : Color.Transparent)
            .Hovered.BackgroundColor(active ? EditorTheme.ToolbarActive : EditorTheme.Hover).End()
            .Icon(paper, icon, color, size: 12f)
            .Tooltip(tooltip)
            .OnClick(0, (_, _) => onClick());
    }

    private static void TextToggle(Paper paper, Scribe.FontFile font, string id, string label,
        bool active, string tooltip, Action onClick)
        => paper.Box(id).Width(26).Height(ControlHeight).Rounded(5)
            .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
            .BackgroundColor(active ? EditorTheme.ToolbarActive : Color.Transparent)
            .Hovered.BackgroundColor(active ? EditorTheme.ToolbarActive : EditorTheme.Hover).End()
            .Text(label, font).TextColor(active ? EditorTheme.Ink500 : EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter)
            .Tooltip(tooltip)
            .OnClick(0, (_, _) => onClick());

    /// <summary>
    /// Unity-style Gizmos control: the left half toggles drawing on/off, the caret opens the
    /// per-type filter menu.
    /// </summary>
    private static void GizmoSplitButton(Paper paper, Scribe.FontFile font, SceneViewPanel panel,
        SceneViewSettings settings)
    {
        using (paper.Row("sv_tb_gizmos").Height(ControlHeight).Rounded(5).Padding(0, 0, 0, 0)
            .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
            .BackgroundColor(EditorTheme.Glass)
            .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .Hovered.BorderColor(EditorTheme.BorderStrong).End()
            .Enter())
        {
            paper.Box("sv_tb_gizmos_on").Width(62).Height(ControlHeight)
                .BackgroundColor(settings.ShowGizmos ? EditorTheme.ToolbarActive : Color.Transparent)
                .Hovered.BackgroundColor(settings.ShowGizmos ? EditorTheme.ToolbarActive : EditorTheme.Hover).End()
                .Text(Loc.Get("scene.gizmos"), font)
                .TextColor(settings.ShowGizmos ? EditorTheme.Ink500 : EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter)
                .Tooltip(Loc.Get("scene.gizmos_toggle_tooltip"))
                .OnClick(0, (_, _) =>
                {
                    settings.ShowGizmos = !settings.ShowGizmos;
                    panel.InvalidateRender();
                });

            paper.Box("sv_tb_gizmos_div").Width(1).Height(12)
                .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

            var caret = paper.Box("sv_tb_gizmos_dd").Width(18).Height(ControlHeight)
                .Hovered.BackgroundColor(EditorTheme.Hover).End()
                .Icon(paper, SceneViewIcons.Caret, EditorTheme.InkDim, size: 8f)
                .Tooltip(Loc.Get("scene.gizmos_menu_tooltip"))
                .OnClick(panel, (p, _) => GizmosWindow.Toggle(p));

            using (caret.Enter())
            {
                if (GizmosWindow.IsOpenFor(panel))
                    GizmosWindow.Render(paper, font, "sv_gizmos_win", ControlHeight,
                        panel.InvalidateRender);
            }
        }
    }

    /// <summary>An icon + label + caret pill that opens a context menu, like Unity's toolbar pickers.</summary>
    private static void Menu(Paper paper, Scribe.FontFile font, string id, string label, IOrigamiIcon icon,
        float width, string tooltip, Action<ContextBuilder> build)
    {
        using (paper.Row(id).Width(width).Height(ControlHeight).Rounded(5).Padding(5, 4, 0, 0).RowBetween(3)
            .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
            .BackgroundColor(EditorTheme.Glass)
            .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .Hovered.BorderColor(EditorTheme.BorderStrong).End()
            .Tooltip(tooltip)
            .OnClick(0, (_, _) => Origami.ContextMenu(
                (float)paper.PointerPos.X, (float)paper.PointerPos.Y, build))
            .Enter())
        {
            paper.Box($"{id}_i").Width(12).Height(ControlHeight).IsNotInteractable()
                .Icon(paper, icon, EditorTheme.Ink300, size: 10f);
            paper.Box($"{id}_l").Width(UnitValue.Stretch()).Height(ControlHeight).IsNotInteractable()
                .Text(label, font).TextColor(EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).TextTruncate();
            paper.Box($"{id}_c").Width(8).Height(ControlHeight).IsNotInteractable()
                .Icon(paper, SceneViewIcons.Caret, EditorTheme.InkDim, size: 8f);
        }
    }

    private static void Number(Paper paper, string id, float value, float width, string tooltip,
        Action<float> setter)
    {
        using (paper.Box(id).Width(width).Height(ControlHeight)
            .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).Tooltip(tooltip).Enter())
            Origami.NumericField(paper, $"{id}_v", value, setter)
                .Width(UnitValue.Stretch()).Height(ControlHeight).Format("0.###").Stepper(false).Show();
    }
}
