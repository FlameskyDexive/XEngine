using System;

using XEngine.Editor.GUI;
using XEngine.Editor.Core;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;

using Color = System.Drawing.Color;

namespace XEngine.Editor.GUI.Panels;

/// <summary>Whether the Game view shows the raw game image or frames it in a simulated device.</summary>
internal enum GameViewMode
{
    Game,
    Simulator,
}

/// <summary>What happens to the editor layout when play mode starts.</summary>
internal enum PlayFocusMode
{
    /// <summary>Focus the Game view tab (Unity's default).</summary>
    Focused,
    /// <summary>Leave the current tab alone, so the Scene view stays visible while playing.</summary>
    Unfocused,
    /// <summary>Focus AND maximize the Game view for the duration of play mode (Unity's Maximize on Play).</summary>
    Maximized,
}

/// <summary>
/// The Game view toolbar: view mode, target display, resolution or simulated device, zoom, play-focus
/// behaviour, mute, stats and gizmos. Kept in its own file so <see cref="GameViewPanel"/> stays about
/// rendering and input routing.
/// </summary>
public partial class GameViewPanel
{
    /// <summary>
    /// Consulted by <see cref="EditorApplication.EnterPlayMode"/>. Static because the setting belongs to
    /// "playing", not to a particular Game view instance, and play mode can start with no view open.
    /// </summary>
    internal static PlayFocusMode PlayFocus { get; set; } = PlayFocusMode.Focused;

    /// <summary>Unity allows several Game views side by side; the primary menu entry keeps
    /// focus-if-open semantics, this one always spawns another instance.</summary>
    [MenuItem("Window/General/New Game View", priority: 2)]
    static void OpenNewGameView() => EditorApplication.Instance?.OpenPanelInstance(new GameViewPanel());

    private const float ControlHeight = 20f;
    private const float MaxZoom = 5f;

    private void DrawToolbar(Paper paper, Scribe.FontFile font, float height)
    {
        bool simulator = _viewMode == GameViewMode.Simulator;

        using (paper.Row("gv_tb").Height(height).Padding(6, 6, 0, 0).RowBetween(3)
            .BackgroundColor(EditorTheme.Neutral200).Clip().Enter())
        {
            using (paper.Row("gv_tb_chrome").Height(ControlHeight).RowBetween(3).Enter())
            {
                AttachPaintRegion(paper);
            Menu(paper, font, "gv_tb_mode",
                Loc.Get(simulator ? "game.simulator" : "panel.game"),
                simulator ? EditorIcons.MobileScreen : EditorIcons.Gamepad,
                86f, Loc.Get("game.view_mode_tooltip"), menu =>
                {
                    menu.Header(Loc.Get("game.view_mode"));
                    menu.Item(Loc.Get("panel.game"), () => SetViewMode(GameViewMode.Game),
                        on: !simulator, icon: EditorIcons.Gamepad);
                    menu.Item(Loc.Get("game.simulator"), () => SetViewMode(GameViewMode.Simulator),
                        on: simulator, icon: EditorIcons.MobileScreen);
                });

            Menu(paper, font, "gv_tb_display", DisplayName(_displayIndex), EditorIcons.Display, 90f,
                Loc.Get("game.display_tooltip"), menu =>
                {
                    menu.Header(Loc.Get("game.display"));
                    for (int i = 0; i < MaxDisplays; i++)
                    {
                        int index = i;
                        menu.Item(DisplayName(index), () =>
                        {
                            _displayIndex = index;
                            _renderSchedule.Invalidate();
                        }, on: index == _displayIndex);
                    }
                });

            if (simulator)
            {
                Menu(paper, font, "gv_tb_device", GameViewDevices.All[_deviceIndex].Name,
                    EditorIcons.MobileScreen, 190f, Loc.Get("game.device_tooltip"), menu =>
                    {
                        menu.Header(Loc.Get("game.device"));
                        for (int i = 0; i < GameViewDevices.All.Length; i++)
                        {
                            int index = i;
                            menu.Item(GameViewDevices.All[index].Name, () =>
                            {
                                _deviceIndex = index;
                                InvalidateRT();
                            }, on: index == _deviceIndex);
                        }
                    });

                IconToggle(paper, font, "gv_tb_rotate", EditorIcons.RotateRight, _landscape,
                    Loc.Get("game.rotate_tooltip"), () =>
                    {
                        _landscape = !_landscape;
                        InvalidateRT();
                    });
                IconToggle(paper, font, "gv_tb_safe", EditorIcons.BorderAll, _showSafeArea,
                    Loc.Get("game.safe_area_tooltip"), () => _showSafeArea = !_showSafeArea);
            }
            else
            {
                Menu(paper, font, "gv_tb_res", CurrentResolution.name, EditorIcons.Expand,
                    170f, Loc.Get("game.resolution_tooltip"), BuildResolutionMenu);
            }

            }

            Divider(paper, "gv_tb_d1");

            // Scale: the slider is a multiplier on top of "fit to panel", and the readout shows the
            // resulting pixel scale — so a 1920x1080 target in a small panel reads e.g. "0.45x".
            paper.Box("gv_tb_scale_l").Width(34).Height(ControlHeight)
                .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                .Text(Loc.Get("game.scale"), font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

            using (paper.Box("gv_tb_scale_c").Width(UnitValue.Stretch()).Height(ControlHeight)
                .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                .Tooltip(Loc.Get("game.scale_tooltip")).Enter())
                Origami.Slider(paper, "gv_tb_scale", _zoom, value =>
                {
                    _zoom = value;
                    _renderSchedule.Invalidate();
                }, 0.25f, MaxZoom).Small().Width(UnitValue.Stretch()).ShowValue(false).Show();

            paper.Box("gv_tb_scale_v").Width(40).Height(ControlHeight)
                .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                .Text($"{_effectiveScale:0.00}x", font).TextColor(EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleRight);

            Divider(paper, "gv_tb_d2");

            using (paper.Row("gv_tb_actions").Height(ControlHeight).RowBetween(3).Enter())
            {
            Menu(paper, font, "gv_tb_focus",
                PlayFocus switch
                {
                    PlayFocusMode.Maximized => "Maximized",
                    PlayFocusMode.Unfocused => Loc.Get("game.play_unfocused"),
                    _ => Loc.Get("game.play_focused"),
                },
                EditorIcons.Play, 118f, Loc.Get("game.play_focus_tooltip"), menu =>
                {
                    menu.Header(Loc.Get("game.play_focus"));
                    menu.Item(Loc.Get("game.play_focused"), () => PlayFocus = PlayFocusMode.Focused,
                        on: PlayFocus == PlayFocusMode.Focused);
                    menu.Item("Play Maximized", () => PlayFocus = PlayFocusMode.Maximized,
                        on: PlayFocus == PlayFocusMode.Maximized);
                    menu.Item(Loc.Get("game.play_unfocused"), () => PlayFocus = PlayFocusMode.Unfocused,
                        on: PlayFocus == PlayFocusMode.Unfocused);
                });

            IconToggle(paper, font, "gv_tb_mute",
                EditorAudio.GameViewMuted ? EditorIcons.VolumeXmark : EditorIcons.VolumeHigh,
                EditorAudio.GameViewMuted, Loc.Get("game.mute_tooltip"), () =>
                {
                    EditorAudio.GameViewMuted = !EditorAudio.GameViewMuted;
                    EditorAudio.Apply();
                });

            TextToggle(paper, font, "gv_tb_stats", Loc.Get("game.stats"), 42f, _showStats,
                Loc.Get("game.stats_tooltip"), () => _showStats = !_showStats);
            }

            GizmoSplitButton(paper, font);
        }
    }

    internal GameViewMode ViewMode
    {
        get => _viewMode;
        set => SetViewMode(value);
    }

    private void SetViewMode(GameViewMode mode)
    {
        if (_viewMode == mode) return;
        _viewMode = mode;
        InvalidateRT();
    }

    private static string DisplayName(int index) => Loc.Get("game.display_n", new { n = index + 1 });

    private void ToggleGizmos()
    {
        _showGizmos = !_showGizmos;
        _renderSchedule.Invalidate();
    }

    private void BuildResolutionMenu(ContextBuilder menu)
    {
        menu.Header(Loc.Get("game.resolution"));
        for (int i = 0; i < Resolutions.Length; i++)
        {
            int index = i;
            menu.Item(Resolutions[index].name, () =>
            {
                _resolutionIndex = index;
                _viewMode = GameViewMode.Game;
                InvalidateRT();
            }, on: index == _resolutionIndex);
        }

        if (_customResolutions.Count > 0)
        {
            menu.Separator();
            for (int i = 0; i < _customResolutions.Count; i++)
            {
                int index = Resolutions.Length + i;
                menu.Item(_customResolutions[i].name, () =>
                {
                    _resolutionIndex = index;
                    _viewMode = GameViewMode.Game;
                    InvalidateRT();
                }, on: index == _resolutionIndex);
            }
        }

        menu.Separator();
        menu.Item("Add Custom...", ShowAddCustomResolutionDialog, icon: EditorIcons.Plus);
        if (_customResolutions.Count > 0)
            menu.Submenu("Remove Custom", sub =>
            {
                for (int i = 0; i < _customResolutions.Count; i++)
                {
                    int custom = i;
                    sub.Item(_customResolutions[custom].name, () =>
                    {
                        _customResolutions.RemoveAt(custom);
                        if (_resolutionIndex >= Resolutions.Length + _customResolutions.Count)
                            _resolutionIndex = 0;
                        InvalidateRT();
                    });
                }
            }, icon: EditorIcons.Trash);
    }

    private int _customResW = 1920, _customResH = 1080;

    private void ShowAddCustomResolutionDialog()
    {
        Origami.Dialog("Add Custom Resolution", p =>
        {
            using (p.Row("gv_custom_res_row").Height(24).RowBetween(6).Enter())
            {
                p.Box("gv_custom_res_wl").Width(44).Height(24).IsNotInteractable()
                    .Text("Width", EditorTheme.DefaultFont).TextColor(EditorTheme.Ink300)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
                Origami.NumericField(p, "gv_custom_res_w", _customResW, v => _customResW = Math.Clamp(v, 16, 16384))
                    .Width(80).Height(24).Show();
                p.Box("gv_custom_res_hl").Width(48).Height(24).IsNotInteractable()
                    .Text("Height", EditorTheme.DefaultFont).TextColor(EditorTheme.Ink300)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
                Origami.NumericField(p, "gv_custom_res_h", _customResH, v => _customResH = Math.Clamp(v, 16, 16384))
                    .Width(80).Height(24).Show();
            }
        }, 340)
        .Button("Add", () =>
        {
            _customResolutions.Add(($"{_customResW}x{_customResH}", _customResW, _customResH));
            _resolutionIndex = Resolutions.Length + _customResolutions.Count - 1;
            _viewMode = GameViewMode.Game;
            InvalidateRT();
        }, OrigamiVariant.Primary)
        .Button("Cancel", () => { });
    }

    /// <summary>
    /// The Gizmos control, built exactly like the Scene view's: the left half is this view's own
    /// master switch, the caret opens the shared <see cref="SceneView.GizmosWindow"/>. Everything in
    /// that window is global, so a type hidden in one view is hidden in both — matching Unity.
    /// </summary>
    private void GizmoSplitButton(Paper paper, Scribe.FontFile font)
    {
        using (paper.Row("gv_tb_gizmos").Height(ControlHeight).Rounded(5)
            .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
            .BackgroundColor(EditorTheme.Glass)
            .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .Hovered.BorderColor(EditorTheme.BorderStrong).End()
            .Enter())
        {
            paper.Box("gv_tb_gizmos_on").Width(62).Height(ControlHeight)
                .BackgroundColor(_showGizmos ? EditorTheme.ToolbarActive : System.Drawing.Color.Transparent)
                .Hovered.BackgroundColor(_showGizmos ? EditorTheme.ToolbarActive : EditorTheme.Hover).End()
                .Text(Loc.Get("game.gizmos"), font)
                .TextColor(_showGizmos ? EditorTheme.Ink500 : EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter)
                .Tooltip(Loc.Get("game.gizmos_tooltip"))
                .OnClick(0, (_, _) => ToggleGizmos());

            paper.Box("gv_tb_gizmos_div").Width(1).Height(12)
                .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

            var caret = paper.Box("gv_tb_gizmos_dd").Width(18).Height(ControlHeight)
                .Hovered.BackgroundColor(EditorTheme.Hover).End()
                .Text(EditorIcons.CaretDown, font).TextColor(EditorTheme.InkDim)
                .FontSize(8f).Alignment(TextAlignment.MiddleCenter)
                .Tooltip(Loc.Get("scene.gizmos_menu_tooltip"))
                .OnClick(this, (p, _) => SceneView.GizmosWindow.Toggle(p));

            using (caret.Enter())
            {
                if (SceneView.GizmosWindow.IsOpenFor(this))
                    SceneView.GizmosWindow.Render(paper, font, "gv_gizmos_win", ControlHeight,
                        _renderSchedule.Invalidate);
            }
        }
    }

    // ================================================================
    //  Toolbar primitives
    // ================================================================

    private static void Divider(Paper paper, string id)
        => paper.Box(id).Width(1).Height(14).Margin(3, 3, UnitValue.StretchOne, UnitValue.StretchOne)
            .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

    private static void IconToggle(Paper paper, Scribe.FontFile font, string id, string glyph,
        bool active, string tooltip, Action onClick)
        => paper.Box(id).Width(24).Height(ControlHeight).Rounded(5)
            .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
            .BackgroundColor(active ? EditorTheme.ToolbarActive : Color.Transparent)
            .Hovered.BackgroundColor(active ? EditorTheme.ToolbarActive : EditorTheme.Hover).End()
            .Icon(paper, new EditorGlyphIcon(glyph, weight: GlyphWeight.Solid),
                active ? EditorTheme.Ink500 : EditorTheme.Ink300, size: 12f)
            .Tooltip(tooltip)
            .OnClick(0, (_, _) => onClick());

    private static void TextToggle(Paper paper, Scribe.FontFile font, string id, string label,
        float width, bool active, string tooltip, Action onClick)
        => paper.Box(id).Width(width).Height(ControlHeight).Rounded(5)
            .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
            .BackgroundColor(active ? EditorTheme.ToolbarActive : Color.Transparent)
            .Hovered.BackgroundColor(active ? EditorTheme.ToolbarActive : EditorTheme.Hover).End()
            .Text(label, font).TextColor(active ? EditorTheme.Ink500 : EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter)
            .Tooltip(tooltip)
            .OnClick(0, (_, _) => onClick());

    /// <summary>An icon + label + caret pill that opens a context menu, like Unity's toolbar pickers.</summary>
    private static void Menu(Paper paper, Scribe.FontFile font, string id, string label, string icon,
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
                .Text(icon, font).TextColor(EditorTheme.Ink300)
                .FontSize(10f).Alignment(TextAlignment.MiddleCenter);
            paper.Box($"{id}_l").Width(UnitValue.Stretch()).Height(ControlHeight).IsNotInteractable()
                .Text(label, font).TextColor(EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).TextTruncate();
            paper.Box($"{id}_c").Width(8).Height(ControlHeight).IsNotInteractable()
                .Text(EditorIcons.CaretDown, font).TextColor(EditorTheme.InkDim)
                .FontSize(8f).Alignment(TextAlignment.MiddleCenter);
        }
    }

    // ================================================================
    //  Persistence
    // ================================================================

    public override bool SerializeState(System.Text.Json.Nodes.JsonObject state)
    {
        state["mode"] = (int)_viewMode;
        state["display"] = _displayIndex;
        state["resolution"] = _resolutionIndex;
        state["zoom"] = _zoom;
        state["stats"] = _showStats;
        state["gizmos"] = _showGizmos;
        state["device"] = _deviceIndex;
        state["landscape"] = _landscape;
        state["safeArea"] = _showSafeArea;
        state["playFocus"] = (int)PlayFocus;
        state["muted"] = EditorAudio.GameViewMuted;

        var customs = new System.Text.Json.Nodes.JsonArray();
        foreach (var (_, w, h) in _customResolutions)
        {
            var entry = new System.Text.Json.Nodes.JsonObject { ["w"] = w, ["h"] = h };
            customs.Add(entry);
        }
        state["customResolutions"] = customs;
        return true;
    }

    public override void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        if (state["mode"]?.GetValue<int>() is int mode && mode is 0 or 1)
            _viewMode = (GameViewMode)mode;
        if (state["display"]?.GetValue<int>() is int display && display >= 0 && display < MaxDisplays)
            _displayIndex = display;
        if (state["customResolutions"] is System.Text.Json.Nodes.JsonArray customs)
        {
            _customResolutions.Clear();
            foreach (var node in customs)
            {
                if (node is not System.Text.Json.Nodes.JsonObject o) continue;
                int w = o["w"]?.GetValue<int>() ?? 0;
                int h = o["h"]?.GetValue<int>() ?? 0;
                if (w >= 16 && h >= 16)
                    _customResolutions.Add(($"{w}x{h}", w, h));
            }
        }
        if (state["resolution"]?.GetValue<int>() is int resolution
            && resolution >= 0 && resolution < Resolutions.Length + _customResolutions.Count)
            _resolutionIndex = resolution;
        if (state["device"]?.GetValue<int>() is int device
            && device >= 0 && device < GameViewDevices.All.Length)
            _deviceIndex = device;
        if (state["playFocus"]?.GetValue<int>() is int focus && focus is 0 or 1 or 2)
            PlayFocus = (PlayFocusMode)focus;

        _zoom = Math.Clamp(state["zoom"]?.GetValue<float>() ?? _zoom, 0.25f, MaxZoom);
        _showStats = state["stats"]?.GetValue<bool>() ?? _showStats;
        _showGizmos = state["gizmos"]?.GetValue<bool>() ?? _showGizmos;
        _landscape = state["landscape"]?.GetValue<bool>() ?? _landscape;
        _showSafeArea = state["safeArea"]?.GetValue<bool>() ?? _showSafeArea;
        EditorAudio.GameViewMuted = state["muted"]?.GetValue<bool>() ?? EditorAudio.GameViewMuted;
    }
}
