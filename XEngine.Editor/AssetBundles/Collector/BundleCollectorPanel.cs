// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// BundleCollectorPanel - standalone asset-bundle collector editor (Window/AssetBundles/Bundle
// Collector), modeled on YooAsset's BundleCollectorWindow: Fix/Import/Export/Save toolbar,
// collapsible global + package settings, and a Groups|Collectors two-column editor with a
// live "Main Assets (N)" preview per collector.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using XEngine.Editor.AssetBundles.Builder;
using XEngine.Editor.AssetBundles.BuildPipeline;
using XEngine.Editor.AssetBundles.Collector;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Scribe;
using PaperTextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.Editor.GUI.Panels;

public sealed class BundleCollectorPanel : DockPanel
{
    [MenuItem("Window/AssetBundles/Bundle Collector", priority: 61)]
    private static void Open() => EditorApplication.Instance?.OpenPanel(typeof(BundleCollectorPanel));

    public override string Title => "Bundle Collector";
    public override string Icon => EditorIcons.Box;

    private const int PreviewRowCap = 120;

    private AssetBundleCollectorSetting Setting => EditorRegistries.GetSettings<AssetBundleCollectorSetting>();

    private bool _dirty;
    private int _packageIndex;
    private int _groupIndex;
    private bool _showGlobalSettings;
    private bool _showPackageSettings = true;

    private long _editVersion;
    private long _previewVersion = -1;
    private string? _previewKey;
    private List<(string address, string path)>? _previewRows;
    private string? _previewError;
    private int _previewTotal;

    private AssetBundleCollectorPackage? SelectedPackage
        => Setting.Packages.Count == 0 ? null : Setting.Packages[Math.Clamp(_packageIndex, 0, Setting.Packages.Count - 1)];

    private AssetBundleCollectorGroup? SelectedGroup
    {
        get
        {
            var package = SelectedPackage;
            if (package == null || package.Groups.Count == 0) return null;
            return package.Groups[Math.Clamp(_groupIndex, 0, package.Groups.Count - 1)];
        }
    }

    private void MarkDirty()
    {
        _dirty = true;
        _editVersion++;
    }

    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        ClampSelection();

        using (paper.Column("bc_root").Size(width, height).Padding(8, 8, 8, 8).Clip().Enter())
        {
            DrawToolbar(paper, width);
            DrawHelpBox(paper, width);
            DrawGlobalSettings(paper, width);
            DrawPackageSettings(paper, width);

            float headerH = 30f + (_showGlobalSettings ? 66f : 26f) + (_showPackageSettings ? 96f : 26f);
            float bodyH = MathF.Max(120f, height - headerH - 40f);
            DrawBody(paper, width, bodyH, font);
        }
    }

    private void ClampSelection()
    {
        var packages = Setting.Packages;
        if (packages.Count == 0) { _packageIndex = 0; _groupIndex = 0; return; }
        _packageIndex = Math.Clamp(_packageIndex, 0, packages.Count - 1);
        var groups = packages[_packageIndex].Groups;
        _groupIndex = groups.Count == 0 ? 0 : Math.Clamp(_groupIndex, 0, groups.Count - 1);
    }

    // -- Toolbar -------------------------------------------------------

    private void DrawToolbar(Paper paper, float width)
    {
        using (paper.Row("bc_toolbar").Height(30).Margin(0, 0, 4, 0).RowBetween(6).Enter())
        {
            Origami.Button(paper, "bc_fix", "Fix", OnFixClicked).Show();
            Origami.Button(paper, "bc_import", "Import", OnImportClicked).Show();
            Origami.Button(paper, "bc_export", "Export", OnExportClicked).Show();
            paper.Box("bc_tb_spacer").Width(UnitValue.StretchOne).Height(1).IsNotInteractable();
            Origami.Button(paper, "bc_save", "Save", OnSaveClicked).Disabled(!_dirty).Show();
        }
    }

    private void OnSaveClicked()
    {
        EditorRegistries.SaveSettings();
        _dirty = false;
    }

    private void OnFixClicked()
    {
        var db = EditorAssetBackend.Instance;
        foreach (var package in Setting.Packages)
        {
            for (int g = package.Groups.Count - 1; g >= 0; g--)
            {
                var group = package.Groups[g];
                if (string.IsNullOrWhiteSpace(group.GroupName) && group.Collectors.Count == 0)
                {
                    package.Groups.RemoveAt(g);
                    continue;
                }
                for (int c = group.Collectors.Count - 1; c >= 0; c--)
                {
                    string path = group.Collectors[c].CollectPath;
                    if (string.IsNullOrWhiteSpace(path) || (db != null && db.GetEntry(path) == null))
                        group.Collectors.RemoveAt(c);
                }
            }
        }
        if (Setting.Packages.Count == 0)
            Setting.Packages.Add(new AssetBundleCollectorPackage { PackageName = "default" });
        ClampSelection();
        MarkDirty();
    }

    private void OnExportClicked()
    {
        EditorApplication.OpenFileDialog(FileDialogMode.Save, path =>
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path += ".json";
            var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
            File.WriteAllText(path, JsonSerializer.Serialize(Setting, options));
        }, null, ["json"], ["Collector JSON (*.json)"]);
    }

    private void OnImportClicked()
    {
        EditorApplication.OpenFileDialog(FileDialogMode.Open, path =>
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                var options = new JsonSerializerOptions { IncludeFields = true };
                var imported = JsonSerializer.Deserialize<AssetBundleCollectorSetting>(File.ReadAllText(path), options);
                if (imported == null) return;
                Setting.Packages = imported.Packages;
                Setting.ShowPackagesView = imported.ShowPackagesView;
                Setting.ShowRuleAlias = imported.ShowRuleAlias;
                ClampSelection();
                MarkDirty();
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogError($"[BundleCollector] Import failed: {ex.Message}");
            }
        }, null, ["json"], ["Collector JSON (*.json)"]);
    }

    // -- Help box ------------------------------------------------------

    private void DrawHelpBox(Paper paper, float width)
    {
        var package = SelectedPackage;
        string? warning = null;
        if (package != null && package.EnableAddressable && package.LocationToLower)
            warning = "[Enable Addressable] and [Location To Lower] should not be enabled at the same time (addresses lose case).";

        if (warning == null) return;
        using (paper.Row("bc_help").Height(24).Margin(0, 0, 4, 0).Enter())
        {
            paper.Box("bc_help_text").Width(UnitValue.StretchOne).Height(24)
                .Text("⚠ " + warning, EditorTheme.DefaultFont)
                .TextColor(EditorTheme.Amber400).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperTextAlignment.MiddleLeft).IsNotInteractable();
        }
    }

    // -- Collapsible settings -----------------------------------------

    private void DrawGlobalSettings(Paper paper, float width)
    {
        using (paper.Row("bc_gs_head").Height(24).Margin(0, 0, 3, 0).Enter())
        {
            paper.Box("bc_gs_head_btn").Width(UnitValue.StretchOne).Height(24)
                .Text("Global Settings", EditorTheme.DefaultFont)
                .TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSize)
                .Alignment(PaperTextAlignment.MiddleCenter)
                .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
                .OnClick(0, (_, _) => _showGlobalSettings = !_showGlobalSettings);
        }
        if (!_showGlobalSettings) return;

        using (paper.Column("bc_gs_body").Height(64).Padding(6, 6, 2, 2).Enter())
        {
            EditorGUI.SettingsToggle(paper, "bc_gs_pkgs", "Show Packages View",
                Setting.ShowPackagesView, v => { Setting.ShowPackagesView = v; MarkDirty(); },
                separator: false, compact: true);
            EditorGUI.SettingsToggle(paper, "bc_gs_alias", "Show Rule Alias",
                Setting.ShowRuleAlias, v => { Setting.ShowRuleAlias = v; MarkDirty(); },
                separator: false, compact: true);
            paper.Box("bc_gs_unique").Height(18)
                .Text("Bundle names are always package-prefixed (uniqueness is built in).", EditorTheme.DefaultFont)
                .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperTextAlignment.MiddleLeft).IsNotInteractable();
        }
    }

    private void DrawPackageSettings(Paper paper, float width)
    {
        var package = SelectedPackage;
        string title = package == null ? "Package Settings" : $"Package Settings ({package.PackageName})";
        using (paper.Row("bc_ps_head").Height(24).Margin(0, 0, 3, 0).Enter())
        {
            paper.Box("bc_ps_head_btn").Width(UnitValue.StretchOne).Height(24)
                .Text(title, EditorTheme.DefaultFont)
                .TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSize)
                .Alignment(PaperTextAlignment.MiddleCenter)
                .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
                .OnClick(0, (_, _) => _showPackageSettings = !_showPackageSettings);
        }
        if (!_showPackageSettings || package == null) return;

        using (paper.Column("bc_ps_body").Height(96).Padding(6, 6, 2, 2).Enter())
        {
            EditorGUI.SettingsToggle(paper, "bc_ps_addr", "Enable Addressable",
                package.EnableAddressable, v => { package.EnableAddressable = v; MarkDirty(); },
                separator: false, compact: true);
            EditorGUI.SettingsToggle(paper, "bc_ps_lower", "Location To Lower",
                package.LocationToLower, v => { package.LocationToLower = v; MarkDirty(); },
                separator: false, compact: true);
            EditorGUI.SettingsRow(paper, "bc_ps_ignore", "File Ignore Rule", () =>
                Origami.TextField(paper, "bc_ps_ignore_v", package.IgnoreExtensions, v =>
                {
                    package.IgnoreExtensions = v;
                    MarkDirty();
                }).Show());
        }
    }

    // -- Two-column body ----------------------------------------------

    private sealed class SplitDragState { public bool Active; }

    private readonly SplitDragState _splitDrag = new();

    private void DrawBody(Paper paper, float width, float bodyH, FontFile font)
    {
        float leftW = Math.Clamp(Setting.CollectorSplitWidth, 220f, MathF.Max(260f, width - 340f));
        using (paper.Row("bc_body").Size(width, bodyH).Margin(0, 0, 4, 0).Enter())
        {
            DrawGroupsColumn(paper, leftW, bodyH, font);
            using (paper.Box("bc_split").Width(6).Height(bodyH)
                .OnDragStart(_splitDrag, (s, e) => s.Active = true)
                .OnDragging(_splitDrag, (s, e) =>
                {
                    if (!s.Active) return;
                    Setting.CollectorSplitWidth = Math.Clamp(Setting.CollectorSplitWidth + e.Delta.X, 220f, MathF.Max(260f, width - 340f));
                })
                .OnDragEnd(_ =>
                {
                    _splitDrag.Active = false;
                    EditorRegistries.SaveSettings(); // persist the new split width immediately
                })
                .Cursor(PaperCursor.ResizeHorizontal)
                .BackgroundColor(EditorTheme.BorderSoft)
                .Enter())
            {
            }
            DrawCollectorsColumn(paper, width - leftW - 6, bodyH, font);
        }
    }

    private void DrawGroupsColumn(Paper paper, float width, float height, FontFile font)
    {
        var package = SelectedPackage;
        using (paper.Column("bc_left").Size(width, height).Enter())
        {
            paper.Box("bc_groups_h").Width(width).Height(26)
                .Text("Groups", font).TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSize)
                .Alignment(PaperTextAlignment.MiddleCenter)
                .BorderColor(EditorTheme.BorderSoft).BorderWidth(1).IsNotInteractable();

            if (package == null)
            {
                paper.Box("bc_left_empty").Width(width).Height(40)
                    .Text("No package.", font).TextColor(EditorTheme.Ink300)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft)
                    .IsNotInteractable();
                return;
            }

            using (paper.Column("bc_pkg_fields").Height(56).Padding(4, 4, 2, 0).Enter())
            {
                EditorGUI.SettingsRow(paper, "bc_pkg_name", "Package Name", () =>
                    Origami.TextField(paper, "bc_pkg_name_v", package.PackageName, v => { package.PackageName = v; MarkDirty(); }).Show());
                EditorGUI.SettingsRow(paper, "bc_pkg_desc", "Package Desc", () =>
                    Origami.TextField(paper, "bc_pkg_desc_v", package.PackageDescription, v => { package.PackageDescription = v; MarkDirty(); }).Show());
            }

            // Package list (optional, mirrors YooAsset's Show Packages).
            float listTop = 26f + 56f;
            float pkgListH = Setting.ShowPackagesView ? 90f : 0f;
            if (Setting.ShowPackagesView)
            {
                Origami.ScrollView(paper, "bc_pkg_list", width, pkgListH).Body(() =>
                {
                    for (int i = 0; i < Setting.Packages.Count; i++)
                    {
                        int index = i; // click lambdas fire after the loop — capture per iteration
                        DrawSelectableRow(paper, $"bc_pkgrow_{index}", width, index == _packageIndex,
                            RowLabel(Setting.Packages[index].PackageName, Setting.Packages[index].PackageDescription),
                            () => { _packageIndex = index; _groupIndex = 0; });
                    }
                });
            }

            float groupListH = height - listTop - pkgListH - 34f;
            Origami.ScrollView(paper, "bc_group_list", width, MathF.Max(60f, groupListH)).Body(() =>
            {
                for (int i = 0; i < package.Groups.Count; i++)
                {
                    int index = i; // click lambdas fire after the loop — capture per iteration
                    var group = package.Groups[index];
                    DrawSelectableRow(paper, $"bc_grprow_{index}", width, index == _groupIndex,
                        RowLabel(group.GroupName, group.GroupDescription),
                        () => { _groupIndex = index; }, dim: !group.Enabled);
                }
            });

            using (paper.Row("bc_group_btns").Height(28).Margin(0, 0, 2, 0).RowBetween(6).Enter())
            {
                Origami.Button(paper, "bc_group_remove", "-", () =>
                {
                    if (package.Groups.Count > 0 && _groupIndex < package.Groups.Count)
                    { package.Groups.RemoveAt(_groupIndex); _groupIndex = 0; MarkDirty(); }
                });
                Origami.Button(paper, "bc_group_add", "+", () =>
                {
                    package.Groups.Add(new AssetBundleCollectorGroup { GroupName = "Group " + (package.Groups.Count + 1) });
                    _groupIndex = package.Groups.Count - 1;
                    MarkDirty();
                });
                paper.Box("bc_group_sp").Width(UnitValue.StretchOne).Height(1).IsNotInteractable();
            }
        }
    }

    private static string RowLabel(string name, string? desc)
        => string.IsNullOrEmpty(desc) ? name : $"{name} ({desc})";

    private void DrawSelectableRow(Paper paper, string id, float width, bool selected,
        string label, Action onClick, bool dim = false)
    {
        var box = paper.Box(id).Width(width).Height(22)
            .Text(label, EditorTheme.DefaultFont)
            .TextColor(dim ? EditorTheme.Ink300 : selected ? EditorTheme.Ink600 : EditorTheme.Ink400)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft);
        if (selected) box.BackgroundColor(EditorTheme.Selected);
        else box.Hovered.BackgroundColor(EditorTheme.Hover);
        box.OnClick(0, (_, _) => onClick());
    }

    private void DrawCollectorsColumn(Paper paper, float width, float height, FontFile font)
    {
        var package = SelectedPackage;
        var group = SelectedGroup;
        using (paper.Column("bc_right").Size(width, height).Enter())
        {
            paper.Box("bc_col_h").Width(width).Height(26)
                .Text("Collectors", font).TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSize)
                .Alignment(PaperTextAlignment.MiddleCenter)
                .BorderColor(EditorTheme.BorderSoft).BorderWidth(1).IsNotInteractable();

            if (package == null || group == null)
            {
                paper.Box("bc_right_empty").Width(width).Height(40)
                    .Text("Select a group to edit its collectors.", font)
                    .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(PaperTextAlignment.MiddleLeft).IsNotInteractable();
                return;
            }

            using (paper.Column("bc_group_fields").Height(120).Padding(4, 4, 2, 0).Enter())
            {
                EditorGUI.SettingsToggle(paper, "bc_grp_enabled", "Group Active",
                    group.Enabled, v => { group.Enabled = v; MarkDirty(); },
                    separator: false, compact: true);
                EditorGUI.SettingsRow(paper, "bc_grp_name", "Group Name", () =>
                    Origami.TextField(paper, "bc_grp_name_v", group.GroupName, v => { group.GroupName = v; MarkDirty(); }).Show());
                EditorGUI.SettingsRow(paper, "bc_grp_desc", "Group Desc", () =>
                    Origami.TextField(paper, "bc_grp_desc_v", group.GroupDescription, v => { group.GroupDescription = v; MarkDirty(); }).Show());
                EditorGUI.SettingsRow(paper, "bc_grp_tags", "Asset Tags", () =>
                    Origami.TextField(paper, "bc_grp_tags_v", string.Join(',', group.Tags), v =>
                    {
                        group.Tags = new List<string>(v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        MarkDirty();
                    }).Show());
            }

            using (paper.Row("bc_col_btns").Height(28).Margin(0, 0, 2, 0).Enter())
            {
                paper.Box("bc_col_sp").Width(UnitValue.StretchOne).Height(1).IsNotInteractable();
                Origami.Button(paper, "bc_col_add", "[ + ]", () =>
                {
                    group.Collectors.Add(new AssetBundleCollector());
                    MarkDirty();
                });
            }

            float listH = MathF.Max(80f, height - 26f - 120f - 28f);
            Origami.ScrollView(paper, "bc_col_list", width, listH).Body(() =>
            {
                // Everything that actually lands in this group's bundles — direct collectors plus
                // the dependency closure (UI atlases, shader/texture deps, referenced prefabs).
                string packedKey = $"{package.PackageName}|{group.GroupName}|{_editVersion}";
                Origami.Foldout(paper, "bc_packed", PackedTitle(package, group, packedKey)).Body(() =>
                {
                    EnsurePackedClosure(package, group, packedKey);
                    if (_packedError != null)
                    {
                        paper.Box("bc_packed_err").Height(20)
                            .Text("⚠ " + _packedError, font).TextColor(EditorTheme.Red400)
                            .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft)
                            .IsNotInteractable();
                        return;
                    }
                    var rows = _packedRows ?? [];
                    int shown = Math.Min(rows.Count, 60);
                    for (int i = 0; i < shown; i++)
                    {
                        var (address, path) = rows[i];
                        paper.Box("bc_packed_" + i).Height(18)
                            .Text($"[{address}]  {path}", EditorTheme.DefaultFont)
                            .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                            .Alignment(PaperTextAlignment.MiddleLeft).IsNotInteractable();
                    }
                    if (rows.Count > shown)
                        paper.Box("bc_packed_more").Height(18)
                            .Text($"… and {rows.Count - shown} more", font)
                            .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                            .Alignment(PaperTextAlignment.MiddleLeft).IsNotInteractable();
                });

                for (int i = 0; i < group.Collectors.Count; i++)
                    DrawCollector(paper, font, width, group, i);
            });
        }
    }

    private void DrawCollector(Paper paper, FontFile font, float width, AssetBundleCollectorGroup group, int index)
    {
        var collector = group.Collectors[index];
        var package = SelectedPackage!;
        string id = $"bc_c_{index}";

        using (paper.Column(id).Width(width).Padding(2, 6, 4, 4).Enter())
        {
            using (paper.Row(id + "_r1").Height(24).RowBetween(6).Enter())
            {
                Origami.Button(paper, id + "_rm", " [- ]", () =>
                {
                    group.Collectors.RemoveAt(index);
                    MarkDirty();
                });
                paper.Box(id + "_lbl").Width(64).Height(24)
                    .Text("Collector", font).TextColor(EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft)
                    .IsNotInteractable();
                Origami.TextField(paper, id + "_path", collector.CollectPath, v =>
                {
                    collector.CollectPath = NormalizePath(v);
                    MarkDirty();
                }).Width(UnitValue.StretchOne).Show();
            }

            using (paper.Row(id + "_r2").Height(24).RowBetween(6).Enter())
            {
                paper.Box(id + "_sp").Width(88).Height(1).IsNotInteractable();
                paper.Box(id + "_pack_lbl").Width(52).Height(24)
                    .Text("Pack", font).TextColor(EditorTheme.Ink300)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft)
                    .IsNotInteractable();
                Origami.EnumDropdown(paper, id + "_pack", collector.PackRule, v =>
                {
                    collector.PackRule = v;
                    MarkDirty();
                }).Width(150).Show();
                if (package.EnableAddressable)
                {
                    paper.Box(id + "_addr_lbl").Width(56).Height(24)
                        .Text("Address", font).TextColor(EditorTheme.Ink300)
                        .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft)
                        .IsNotInteractable();
                    Origami.EnumDropdown(paper, id + "_addr", collector.AddressRule, v =>
                    {
                        collector.AddressRule = v;
                        MarkDirty();
                    }).Width(140).Show();
                }
                paper.Box(id + "_flt_lbl").Width(44).Height(24)
                    .Text("Filter", font).TextColor(EditorTheme.Ink300)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft)
                    .IsNotInteractable();
                Origami.EnumDropdown(paper, id + "_flt", collector.FilterRule, v =>
                {
                    collector.FilterRule = v;
                    MarkDirty();
                }).Width(140).Show();
            }

            using (paper.Row(id + "_r3").Height(24).RowBetween(6).Enter())
            {
                paper.Box(id + "_sp2").Width(88).Height(1).IsNotInteractable();
                paper.Box(id + "_ud_lbl").Width(64).Height(24)
                    .Text("User Data", font).TextColor(EditorTheme.Ink300)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft)
                    .IsNotInteractable();
                Origami.TextField(paper, id + "_ud", collector.UserData, v => { collector.UserData = v; MarkDirty(); })
                    .Width(200).Show();
                paper.Box(id + "_tags_lbl").Width(70).Height(24)
                    .Text("Asset Tags", font).TextColor(EditorTheme.Ink300)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft)
                    .IsNotInteractable();
                Origami.TextField(paper, id + "_tags", string.Join(',', collector.Tags), v =>
                {
                    collector.Tags = new List<string>(v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    MarkDirty();
                }).Width(UnitValue.StretchOne).Show();
            }

            string previewKey = $"{package.PackageName}|{group.GroupName}|{index}|{collector.CollectPath}|{_editVersion}";
            Origami.Foldout(paper, id + "_preview", PreviewTitle(collector, previewKey)).Body(() =>
            {                var rows = GetPreview(collector, previewKey);
                if (_previewError != null)
                {
                    paper.Box(id + "_pv_err").Height(20)
                        .Text("⚠ " + _previewError, font).TextColor(EditorTheme.Red400)
                        .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft)
                        .IsNotInteractable();
                    return;
                }
                int shown = Math.Min(rows.Count, PreviewRowCap);
                for (int i = 0; i < shown; i++)
                {
                    var (address, path) = rows[i];
                    paper.Box(id + "_pv_" + i).Height(18)
                        .Text($"[{address}]  {path}", EditorTheme.DefaultFont)
                        .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                        .Alignment(PaperTextAlignment.MiddleLeft).IsNotInteractable();
                }
                if (rows.Count > shown)
                    paper.Box(id + "_pv_more").Height(18)
                        .Text($"… and {rows.Count - shown} more", font)
                        .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                        .Alignment(PaperTextAlignment.MiddleLeft).IsNotInteractable();
            });

            paper.Box(id + "_gap").Height(8).IsNotInteractable();
        }
    }

    private static string NormalizePath(string path)
        => path?.Replace('\\', '/').Trim() ?? "";

    // -- Preview -------------------------------------------------------

    private long _collectionVersion = -1;
    private BundleBuildCollection? _cachedCollection;
    private string? _collectionError;

    /// <summary>The package's real collect result, cached per edit revision (shared by all previews).</summary>
    private BundleBuildCollection? GetCollection()
    {
        var package = SelectedPackage;
        if (package == null) { _collectionError = "No package selected."; return null; }
        if (_collectionVersion == _editVersion && _cachedCollection != null) return _cachedCollection;
        _cachedCollection = null;
        _collectionError = null;
        try
        {
            _cachedCollection = AssetBundleBuilder.CollectBuildCollection(package);
            if (_cachedCollection.AddressErrors.Count > 0)
                _collectionError = string.Join("\n", _cachedCollection.AddressErrors);
        }
        catch (Exception ex)
        {
            _collectionError = ex.Message;
        }
        _collectionVersion = _editVersion;
        return _cachedCollection;
    }

    private string PreviewTitle(AssetBundleCollector collector, string key)
    {
        EnsurePreview(collector, key);
        string kind = _previewError != null ? "Assets" : "Main Assets";
        return $"{kind} ({_previewTotal})";
    }

    private List<(string address, string path)> GetPreview(AssetBundleCollector collector, string key)
    {
        EnsurePreview(collector, key);
        return _previewRows ?? [];
    }

    private void EnsurePreview(AssetBundleCollector collector, string key)
    {
        if (_previewVersion == _editVersion && _previewKey == key) return;
        _previewVersion = _editVersion;
        _previewKey = key;
        _previewRows = null;
        _previewError = null;
        _previewTotal = 0;

        if (string.IsNullOrWhiteSpace(collector.CollectPath))
        {
            _previewError = "Collector path is empty.";
            return;
        }

        var collection = GetCollection();
        if (_collectionError != null) { _previewError = _collectionError; return; }
        if (collection == null) return;

        var db = EditorAssetBackend.Instance;
        var rows = new List<(string address, string path)>();
        string prefix = collector.CollectPath.Replace('\\', '/').TrimEnd('/');
        foreach (var guid in collection.ExplicitRootAssets)
        {
            var entry = db?.GetEntry(guid);
            string path = entry?.Path.Replace('\\', '/') ?? "";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string address = collection.AssetAddress.TryGetValue(guid, out var a) ? a : guid.ToString();
            rows.Add((address, path));
        }
        rows.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.path, b.path));
        _previewTotal = rows.Count;
        _previewRows = rows;
    }

    // -- Group packed closure (everything that actually lands in this group's bundles) --

    private long _packedVersion = -1;
    private string? _packedKey;
    private List<(string address, string path)>? _packedRows;
    private string? _packedError;
    private int _packedTotal;

    /// <summary>Bundles of the package that belong to this group (names embed "package_group/...").</summary>
    private IEnumerable<int> GroupBundleIndexes(AssetBundleCollectorPackage package, AssetBundleCollectorGroup group)
    {
        string tag = $"{package.PackageName}_{group.GroupName}";
        for (int b = 0; b < _cachedCollection!.BundleNames.Count; b++)
            if (_cachedCollection.BundleNames[b].Contains(tag)) yield return b;
    }

    private string PackedTitle(AssetBundleCollectorPackage package, AssetBundleCollectorGroup group, string key)
    {
        EnsurePackedClosure(package, group, key);
        string kind = _packedError != null ? "Packed" : "Packed Closure";
        return $"{kind} ({_packedTotal} incl. dependencies)";
    }

    private void EnsurePackedClosure(AssetBundleCollectorPackage package, AssetBundleCollectorGroup group, string key)
    {
        if (_packedVersion == _editVersion && _packedKey == key) return;
        _packedVersion = _editVersion;
        _packedKey = key;
        _packedRows = null;
        _packedError = null;
        _packedTotal = 0;

        var collection = GetCollection();
        if (_collectionError != null) { _packedError = _collectionError; return; }
        if (collection == null) return;

        var db = EditorAssetBackend.Instance;
        var rows = new List<(string address, string path)>();
        var seen = new HashSet<Guid>();
        foreach (var b in GroupBundleIndexes(package, group))
        {
            foreach (var guid in collection.BundleAssets[b])
            {
                if (!seen.Add(guid)) continue;
                string path = db?.GetEntry(guid)?.Path.Replace('\\', '/') ?? guid.ToString();
                string address = collection.AssetAddress.TryGetValue(guid, out var a) ? a : guid.ToString();
                rows.Add((address, path));
            }
        }
        rows.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.path, b.path));
        _packedTotal = rows.Count;
        _packedRows = rows;
    }
}
