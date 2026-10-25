using System;
using System.Linq;

using XEngine.OrigamiUI;
using XEngine.Editor.GUI;
using static XEngine.Editor.GUI.EditorGUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;
using XEngine.Runtime;

using Color = System.Drawing.Color;
using XEngine.Editor.Thumbnails;
using XEngine.Editor.Projects;
using XEngine.Editor.Core;
using XEngine.Editor.Theming;
using XEngine.Editor.Utils;

namespace XEngine.Editor.GUI.Panels;

public class PreferencesPanel : DockPanel
{
    [MenuItem("Window/General/Preferences", priority: 7)]
    static void Open() => EditorApplication.Instance?.OpenPanel(typeof(PreferencesPanel));

    public override string Title => Loc.Get("panel.preferences");
    public override string Icon => EditorIcons.Sliders;

    private enum Tab { General, Shortcuts }
    private Tab _tab = Tab.General;
    private string _shortcutSearch = "";
    private string? _rebindingId;

    // Theme-driven spacing (between elements) / padding (internal).
    private static float SP => EditorTheme.Spacing;

    // label holds a localization key, resolved via Loc.Get where the tabs are rendered.
    private static readonly (string id, string label, string icon)[] Cats =
    {
        ("general",   "pref.general",   EditorIcons.Gear),
        ("shortcuts", "pref.shortcuts", EditorIcons.Keyboard),
    };

    private static string TabId(Tab t) => t == Tab.Shortcuts ? "shortcuts" : "general";
    private static Tab ParseTab(string id) => id == "shortcuts" ? Tab.Shortcuts : Tab.General;

    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        var settings = EditorSettings.Instance;

        using (paper.Row("pref_root").Width(width).Height(height).Clip().Enter())
        {
            var cats = Cats.Select(c => (c.id, Loc.Get(c.label), c.icon)).ToArray();
            float side = EditorGUI.Sidebar(paper, "pref_side", cats, TabId(_tab), c => _tab = ParseTab(c));
            paper.Box("pref_vdiv").Width(1).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

            float contentW = width - side - 1;

            Origami.ScrollView(paper, "pref_content", contentW, height).Body(() =>
            {
                using (paper.Column("pref_content_col").Height(UnitValue.Auto).Padding(0, 0, 8, 12).Enter())
                {
                    switch (_tab)
                    {
                        case Tab.Shortcuts: DrawShortcuts(paper, font, contentW); break;
                        default: DrawGeneral(paper, settings); break;
                    }
                }
            });
        }
    }

    // ================================================================
    //  General
    // ================================================================

    private void DrawGeneral(Paper paper, EditorSettings s)
    {
        EditorGUI.SectionHeader(paper, "pref_gen_hdr", Loc.Get("pref.general"), first: true);

        EditorGUI.SettingsRow(paper, "pref_lang", Loc.Get("pref.language"), () =>
            Origami.Dropdown(paper, "pref_lang_dd",
                LocaleHelper.GetIndex(Loc.CurrentLocale),
                LocaleHelper.SetLocale, LocaleHelper.Names).Show());

        // Interface scale rides on top of the OS DPI content scale; applies live.
        string[] scaleLabels = ["100%", "110%", "125%", "150%"];
        float[] scaleValues = [1f, 1.1f, 1.25f, 1.5f];
        int scaleIndex = 0;
        float bestDelta = float.MaxValue;
        for (int i = 0; i < scaleValues.Length; i++)
        {
            float d = Math.Abs(scaleValues[i] - s.UiScale);
            if (d < bestDelta) { bestDelta = d; scaleIndex = i; }
        }
        EditorGUI.SettingsRow(paper, "pref_ui_scale", Loc.Get("pref.ui_scale"), () =>
            Origami.Dropdown(paper, "pref_ui_scale_dd", scaleIndex,
                v =>
                {
                    s.UiScale = scaleValues[v];
                    s.Save();
                    EditorTheme.UserScale = s.UiScale;
                }, scaleLabels).Show());

        EditorGUI.SettingsRow(paper, "pref_proj_path", Loc.Get("pref.projects_path"), () =>
            Origami.TextField(paper, "pref_proj_path_v", s.DefaultProjectsPath,
                v => { s.DefaultProjectsPath = v; s.Save(); }).Show());

        EditorGUI.SettingsToggle(paper, "pref_auto_save", Loc.Get("pref.auto_save"), SaveManager.AutoSaveEnabled,
            v => { SaveManager.AutoSaveEnabled = v; s.AutoSaveLayout = v; s.Save(); });

        EditorGUI.SettingsToggle(paper, "pref_reimport_focus", Loc.Get("pref.reimport_focus"), s.ReimportOnFocusOnly,
            v => { s.ReimportOnFocusOnly = v; s.Save(); });

        EditorGUI.SettingsToggle(paper, "pref_on_demand", Loc.Get("pref.on_demand_rendering"), s.OnDemandRendering,
            v => { s.OnDemandRendering = v; s.Save(); });

        string[] thumbOptions = ["32", "64", "128"];
        int thumbIndex = s.ThumbnailSize switch { 64 => 1, 128 => 2, _ => 0 };
        EditorGUI.SettingsRow(paper, "pref_thumb_size", Loc.Get("pref.thumbnail_size"), () =>
            Origami.Dropdown(paper, "pref_thumb_size_v", thumbIndex,
                v =>
                {
                    s.ThumbnailSize = v switch { 1 => 64, 2 => 128, _ => 32 };
                    s.Save();
                    ThumbnailGenerator.DeleteAll();
                    EditorAssetBackend.Instance?.ClearThumbnailTextureCache();
                }, thumbOptions).Show());

        // ── Graphics backend ──
        // Takes effect on the next editor restart.
        string[] backendOptions = [Loc.Get("pref.auto"), "Metal", "OpenGL", "Vulkan", "Direct3D12", "Null (headless)"];
        string[] backendValues = ["auto", "metal", "opengl", "vulkan", "d3d12", "null"];
        string currentBackend = string.IsNullOrWhiteSpace(s.PreferredGraphicsBackend) ? "auto" : s.PreferredGraphicsBackend.ToLowerInvariant();
        int backendIndex = Array.IndexOf(backendValues, currentBackend);
        if (backendIndex < 0) backendIndex = 0;
        EditorGUI.SettingsRow(paper, "pref_gfx_backend", Loc.Get("pref.graphics_backend"), () =>
            Origami.Dropdown(paper, "pref_gfx_backend_dd", backendIndex,
                v =>
                {
                    s.PreferredGraphicsBackend = backendValues[v];
                    s.Save();
                }, backendOptions).Show());
        using (paper.Row("pref_gfx_backend_note").Height(UnitValue.Auto).Margin(0, 0, 2, SP).Enter())
            paper.Box("pref_gfx_backend_note_t").IsNotInteractable()
                .Text(Loc.Get("pref.graphics_restart"),
                      EditorTheme.DefaultFont)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperUI.TextAlignment.MiddleLeft);

        // ── External code editor ──
        // Where Console double-click / stack links and Project .cs double-click open source files.
        string[] editorOptions = [Loc.Get("pref.auto"), Loc.Get("pref.system_default"), "Visual Studio Code", "Rider", "Visual Studio", Loc.Get("pref.custom")];
        string[] editorValues = [ExternalCodeEditor.Auto, ExternalCodeEditor.SystemDefault, ExternalCodeEditor.VsCode,
            ExternalCodeEditor.Rider, ExternalCodeEditor.VisualStudio, ExternalCodeEditor.Custom];
        string currentEditor = string.IsNullOrWhiteSpace(s.ExternalCodeEditor) ? ExternalCodeEditor.Auto : s.ExternalCodeEditor;
        int editorIndex = Array.IndexOf(editorValues, currentEditor);
        if (editorIndex < 0) editorIndex = 0;
        EditorGUI.SettingsRow(paper, "pref_code_editor", Loc.Get("pref.code_editor"), () =>
            Origami.Dropdown(paper, "pref_code_editor_dd", editorIndex,
                v =>
                {
                    s.ExternalCodeEditor = editorValues[v];
                    s.Save();
                    ExternalCodeEditor.InvalidateCache();
                }, editorOptions).Show());
        if (currentEditor == ExternalCodeEditor.Custom)
        {
            EditorGUI.SettingsRow(paper, "pref_code_editor_path", Loc.Get("pref.code_editor_path"), () =>
                Origami.TextField(paper, "pref_code_editor_path_v", s.ExternalCodeEditorCustomPath,
                    v => { s.ExternalCodeEditorCustomPath = v; s.Save(); ExternalCodeEditor.InvalidateCache(); }).Show());
            EditorGUI.SettingsRow(paper, "pref_code_editor_args", Loc.Get("pref.code_editor_args"), () =>
                Origami.TextField(paper, "pref_code_editor_args_v", s.ExternalCodeEditorCustomArgs,
                    v => { s.ExternalCodeEditorCustomArgs = v; s.Save(); }).Show());
        }
        using (paper.Row("pref_code_editor_note").Height(UnitValue.Auto).Margin(0, 0, 2, SP).Enter())
            paper.Box("pref_code_editor_note_t").IsNotInteractable()
                .Text(ExternalCodeEditor.DescribeResolved(), EditorTheme.DefaultFont)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperUI.TextAlignment.MiddleLeft);

        EditorGUI.SectionHeader(paper, "pref_gen_maint", Loc.Get("pref.maintenance"));
        EditorGUI.SettingsRow(paper, "pref_clear_cache", Loc.Get("pref.clear_cache"), () =>
            Origami.Button(paper, "pref_clear_cache_b", $"{EditorIcons.ArrowsRotate}  {Loc.Get("pref.clear_cache_btn")}",
                () =>
                {
                    EditorApplication.Instance?.ClearEditorCache();
                    Toasts.Info(Loc.Get("pref.clear_cache"), Loc.Get("pref.clear_cache_done"));
                }).Width(200).Show());
    }

    // ================================================================
    //  Shortcuts
    // ================================================================

    private void DrawShortcuts(Paper paper, Scribe.FontFile font, float w)
    {
        var m = Origami.Current.Metrics;

        // Handle rebinding input each frame
        if (_rebindingId != null)
        {
            ShortcutManager.IsRebinding = true;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                _rebindingId = null;
                ShortcutManager.IsRebinding = false;
            }
            else
            {
                // Check for any non-modifier key press
                foreach (KeyCode key in Enum.GetValues<KeyCode>())
                {
                    if (key == KeyCode.Unknown || key == KeyCode.Escape) continue;
                    // Skip modifier keys themselves they're captured via flags
                    if (key is KeyCode.ShiftLeft or KeyCode.ShiftRight
                        or KeyCode.ControlLeft or KeyCode.ControlRight
                        or KeyCode.AltLeft or KeyCode.AltRight
                        or KeyCode.SuperLeft or KeyCode.SuperRight) continue;

                    if (Input.GetKeyDown(key))
                    {
                        var binding = new ShortcutBinding(key, Input.IsCtrlPressed, Input.IsShiftPressed, Input.IsAltPressed);
                        ShortcutManager.SetOverride(_rebindingId, binding);
                        _rebindingId = null;
                        ShortcutManager.IsRebinding = false;
                        break;
                    }
                }
            }
        }

        EditorGUI.SectionHeader(paper, "pref_sc_hdr", Loc.Get("pref.shortcuts"), first: true);

        // Search bar
        using (paper.Box("pref_sc_search_w").Height(UnitValue.Auto)
            .Padding(m.PaddingLarge, m.PaddingLarge, 0, 4).Enter())
            Origami.SearchField(paper, "pref_sc_search", _shortcutSearch,
                v => _shortcutSearch = v, Loc.Get("pref.search_shortcuts")).Show();

        paper.Box("pref_sc_sp1").Height(EditorTheme.Spacing * 2);

        // Reset All button
        using (paper.Row("pref_sc_actions").Height(EditorTheme.RowHeight).ChildLeft(m.PaddingLarge).Enter())
        {
            Origami.Button(paper, "pref_sc_reset_all", $"{EditorIcons.RotateLeft}  {Loc.Get("pref.reset_all")}", () => ShortcutManager.ClearAllOverrides()).Width(200).Show();
        }

        paper.Box("pref_sc_sp2").Height(EditorTheme.Padding * 2);

        // Group by category
        string? lastCategory = null;
        foreach (var shortcut in ShortcutManager.GetAllShortcuts())
        {
            // Filter by search
            if (!EditorUtils.MatchesSearch(shortcut.DisplayName, _shortcutSearch) &&
                !EditorUtils.MatchesSearch(shortcut.Id, _shortcutSearch))
                continue;

            // Category header
            if (shortcut.Category != lastCategory)
            {
                lastCategory = shortcut.Category;
                EditorGUI.SectionHeader(paper, $"pref_sc_cat_{lastCategory}", lastCategory);
            }

            bool isRebinding = _rebindingId == shortcut.Id;
            bool isOverridden = shortcut.Override != null;
            string bindDisplay = isRebinding
                ? Loc.Get("pref.press_key")
                : ShortcutManager.GetDisplayString(shortcut.Binding);

            using (paper.Row($"pref_sc_{shortcut.Id}")
                .Height(EditorTheme.RowHeight)
                .ChildLeft(m.PaddingLarge).RowBetween(EditorTheme.Spacing * 2)
                .Enter())
            {
                // Display name
                paper.Box($"pref_sc_name_{shortcut.Id}")
                    .Width(w * 0.4f).Height(EditorTheme.RowHeight)
                    .Text(shortcut.DisplayName, font)
                    .TextColor(EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft);

                // Binding button
                paper.Box($"pref_sc_bind_{shortcut.Id}")
                    .Width(160).Height(EditorTheme.RowHeight - 4).Rounded(8)
                    .BackgroundColor(isRebinding ? EditorTheme.Accent : EditorTheme.Glass)
                    .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
                    .Hovered.BackgroundColor(isRebinding ? EditorTheme.Accent : EditorTheme.Hover).BorderColor(EditorTheme.BorderStrong).End()
                    .Text(bindDisplay, font)
                    .TextColor(isRebinding ? Color.White : (isOverridden ? EditorTheme.Accent : EditorTheme.Ink400))
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleCenter)
                    .OnClick(shortcut.Id, (id, _) =>
                    {
                        _rebindingId = _rebindingId == id ? null : id;
                        ShortcutManager.IsRebinding = _rebindingId != null;
                    });

                // Reset button (only if overridden)
                if (isOverridden)
                {
                    paper.Box($"pref_sc_rst_{shortcut.Id}")
                        .Width(50).Height(EditorTheme.RowHeight - 4).Rounded(8)
                        .BackgroundColor(EditorTheme.Glass)
                        .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
                        .Hovered.BackgroundColor(EditorTheme.Hover).BorderColor(EditorTheme.BorderStrong).End()
                        .Text(Loc.Get("inspector.reset"), font)
                        .TextColor(EditorTheme.Ink400)
                        .FontSize(EditorTheme.FontSizeSmall)
                        .Alignment(TextAlignment.MiddleCenter)
                        .OnClick(shortcut.Id, (id, _) => ShortcutManager.ClearOverride(id));
                }
            }
        }
    }
}
