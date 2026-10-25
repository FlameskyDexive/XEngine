// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

using XEngine.Editor.Build;
using XEngine.Editor.Build.Profiles;
using XEngine.Editor.GUI.Popups;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.GUI;
using static XEngine.Editor.GUI.EditorGUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;
using XEngine.Runtime;
using XEngine.Scribe;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Theming;

using TextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.Editor.GUI.Panels;

public class BuildSettingsPanel : DockPanel
{

    public static BuildSettingsPanel Instance { get; set; }

    public class BuildStatusReport
    {
        public enum BuildStatusReportType
        {
            Info,
            Progress,
        }

        public LogSeverity Severity;
        public BuildStatusReportType Type;
        public string Message;
        public float Progress; // Only used for Progress type
    }

    public struct BuildPipelineInfo
    {
        public string Name;
        public string Icon;
        public Type BuildPipelineType;
    }

    public static string BuildState = "";

    public static float BuildProgress = 0f;

    public static bool IsBuildRunning => _activeProgress != null && !_activeProgress.IsComplete;

    public override string Title => Loc.Get("panel.build");

    public override string Icon => EditorIcons.Hammer;

    private BuildSettings _buildSettings;

    private static BuildProgress _activeProgress;

    private List<BuildPipelineInfo> _buildPlatforms;

    private const float HeaderH = 44f;
    private const float FooterH = 56f;
    private const float PlatformW = 400f;

    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        _buildSettings ??= EditorRegistries.GetSettings<BuildSettings>();
        _buildPlatforms ??= GetBuildPlatforms();
        Instance ??= this;

        if (_activeProgress != null)
        {
            BuildProgress = _activeProgress.ProgressValue;
            var state = _activeProgress.GetState();
            if (state != null)
            {
                BuildState = state.Message.Contains("\n") ? state.Message.Split('\n')[0] : state.Message;
                BuildState = BuildState.Trim();
            }

            // Build finished: drop the reference so the footer returns to the idle summary
            // instead of being stuck on the last progress line forever, and so IsBuildRunning
            // (and thus a second Build click) sees the build as no longer running.
            if (_activeProgress.IsComplete)
                _activeProgress = null;
        }
        else
        {
            BuildProgress = 0f;
            BuildState = Loc.Get("editor.status_ready");
        }

        float bodyH = height - FooterH;
        float scenesW = Math.Max(width - PlatformW - 1, 200);

        using (paper.Column("bp_root").Size(width, height).Clip().Enter())
        {
            using (paper.Row("bp_body").Width(width).Height(bodyH).Clip().Enter())
            {
                DrawScenesColumn(paper, font, scenesW, bodyH);
                paper.Box("bp_vdiv").Width(1).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();
                DrawPlatformColumn(paper, font, PlatformW, bodyH);
            }

            DrawFooter(paper, font, width);
        }
    }

    // ---------------------------------------------------------------------
    //  LEFT COLUMN Scenes in Build
    // ---------------------------------------------------------------------

    private void DrawScenesColumn(Paper paper, FontFile font, float colW, float bodyH)
    {
        using (paper.Column("bp_scenes").Width(colW).Height(bodyH).Enter())
        {
            HeaderStrip(paper, "bp_scenes_h", Loc.Get("build.scenes_header"), font, () =>
                EditorGUI.Chip(paper, "bp_scenes_add", $"{EditorIcons.Plus}  {Loc.Get("build.add_open")}", AddOpenScene));

            Origami.ScrollView(paper, "bp_scenes_list", colW, bodyH - HeaderH - 1)
                .Padding(8, 8, 8, 8)
                .Body(() =>
                {
                    var scenes = _buildSettings.Scenes;
                    if (scenes.Count == 0)
                    {
                        paper.Box("bp_scenes_empty").Height(40).Margin(6, 6, 6, 0).IsNotInteractable()
                            .Text(Loc.Get("build.no_scenes"), font)
                            .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                            .Alignment(TextAlignment.MiddleCenter);
                        return;
                    }

                    // Container element that holds the drag state + per-row layout positions.
                    var listEl = paper.CurrentParent;

                    int buildIndex = 0;
                    for (int i = 0; i < scenes.Count; i++)
                        DrawSceneRow(paper, font, scenes, i, scenes[i].Enabled ? buildIndex++ : -1, listEl);
                });
        }
    }

    private void DrawSceneRow(Paper paper, FontFile font, List<SceneBuildEntry> scenes, int i, int buildIndex, ElementHandle listEl)
    {
        int idx = i;
        var scene = scenes[i];
        bool on = scene.Enabled;
        var mono = EditorTheme.FontMono ?? font;

        // The scene GUID is a stable per-row key so Paper element identity survives reorders.
        string sk = scene.SceneGuid.ToString();
        bool beingDragged = paper.GetElementStorage<string>(listEl, "dragSk", null!) == sk;

        string displayName = !string.IsNullOrEmpty(scene.Path)
            ? System.IO.Path.GetFileNameWithoutExtension(scene.Path)
            : sk[..8];

        const float rowH = 28f;

        var rowB = paper.Row($"bp_sc_{sk}").Height(rowH).Rounded(7).Margin(0, 0, 0, 3)
            .Padding(8, 4, 0, 0).RowBetween(8)
            .BackgroundColor(beingDragged || on ? EditorTheme.Selected : Color.Transparent)
            .Hovered.BackgroundColor(on ? EditorTheme.Selected : EditorTheme.Hover).End();

        // Record this row's centre-Y each frame so the drag can pick the nearest drop target.
        rowB.OnPostLayout(idx, (rowIndex, _, r) =>
        {
            var cys = paper.GetElementStorage<List<float>>(listEl, "rowCys", null!);
            if (cys == null) { cys = new List<float>(); paper.SetElementStorage(listEl, "rowCys", cys); }
            while (cys.Count <= rowIndex) cys.Add(0f);
            cys[rowIndex] = (float)(r.Min.Y + r.Size.Y * 0.5f);
        });

        using (rowB.Enter())
        {
            var grip = paper.Box($"bp_sc_{sk}_grip").Width(12)                .Text(EditorIcons.Grip, font).TextColor(beingDragged ? EditorTheme.Ink500 : EditorTheme.Ink300)
                .Hovered.TextColor(EditorTheme.Ink500).End()
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter);

            grip.OnDragStart(sk, (k, _) => paper.SetElementStorage(listEl, "dragSk", k))
                .OnDragging(sk, (k, _) =>
                {
                    int cur = scenes.FindIndex(s => s.SceneGuid.ToString() == k);
                    var cys = paper.GetElementStorage<List<float>>(listEl, "rowCys", null!);
                    if (cur < 0 || cys == null) return;

                    float py = (float)paper.PointerPos.Y;
                    int target = cur; float best = float.MaxValue;
                    for (int t = 0; t < cys.Count && t < scenes.Count; t++)
                    {
                        float d = MathF.Abs(cys[t] - py);
                        if (d < best) { best = d; target = t; }
                    }
                    MoveScene(scenes, cur, target);
                })
                .OnDragEnd(_ =>
                {
                    paper.SetElementStorage(listEl, "dragSk", (string?)null);
                    EditorRegistries.SaveSettings();
                });

            paper.Box($"bp_sc_{sk}_idx").Width(20).IsNotInteractable()
                .Text(buildIndex >= 0 ? buildIndex.ToString() : "-", mono)
                .TextColor(on ? EditorTheme.Ink400 : EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter);

            using (paper.Row($"bp_sc_{sk}_sww").Width(UnitValue.Auto).Height(UnitValue.Auto).Margin(4, 4, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
                Origami.Switch(paper, $"bp_sc_{sk}_sw", scene.Enabled, v =>
                {
                    scene.Enabled = v;
                    EditorRegistries.SaveSettings();
                }).NoLabel().Show();

            paper.Box($"bp_sc_{sk}_nm").IsNotInteractable()
                .Text(displayName, font).TextColor(on ? EditorTheme.Ink500 : EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).TextTruncate();

            paper.Box($"bp_sc_{sk}_rm").Width(rowH).Rounded(6)
                .Hovered.BackgroundColor(EditorTheme.Hover).End()
                .Text(EditorIcons.Xmark, font).TextColor(EditorTheme.Ink300)
                .Hovered.TextColor(EditorTheme.Red400).End()
                .FontSize(9f).Alignment(TextAlignment.MiddleCenter)
                .OnClick(idx, (ci, _) =>
                {
                    scenes.RemoveAt(ci);
                    EditorRegistries.SaveSettings();
                });
        }
    }

    // Reorder in-memory only; the drag-end handler persists once so a drag doesn't thrash the disk.
    private static void MoveScene(List<SceneBuildEntry> scenes, int from, int to)
    {
        if (from == to || from < 0 || from >= scenes.Count || to < 0 || to >= scenes.Count) return;
        var v = scenes[from];
        scenes.RemoveAt(from);
        scenes.Insert(to, v);
    }

    private void AddOpenScene()
    {
        var db = EditorAssetBackend.Instance;
        var entry = EditorSceneManager.CurrentScenePath != null ? db?.GetEntry(EditorSceneManager.CurrentScenePath) : null;
        if (entry == null)
        {
            Toasts.Warning(Loc.Get("build.toast_not_saved"), Loc.Get("build.toast_not_saved_msg"));
            return;
        }

        if (_buildSettings.Scenes.Any(s => s.SceneGuid == entry.Guid))
        {
            Toasts.Warning(Loc.Get("build.toast_already"), Loc.Get("build.toast_already_msg"));
            return;
        }

        _buildSettings.Scenes.Add(new SceneBuildEntry { Path = entry.Path, SceneGuid = entry.Guid });
        EditorRegistries.SaveSettings();
    }

    private void TryStartBuild(bool andRun)
    {
        if (IsBuildRunning)
        {
            Toasts.Warning(Loc.Get("build.toast_build_running"), Loc.Get("build.toast_build_running_msg"));
            return;
        }

        ResolvedBuildOptions options = ResolvedBuildOptions.From(_buildSettings);
        if (options.Scenes.Count(s => s.Enabled && s.SceneGuid != Guid.Empty) == 0)
        {
            Toasts.Warning(Loc.Get("build.toast_no_scenes"), Loc.Get("build.toast_no_scenes_msg"));
            return;
        }

        // Pass null so the pipeline resolves output from the profile; passing the shared
        // OutputDirectory here would make OverrideOutputDirectory a no-op.
        _activeProgress = ProjectBuilder.StartBuildAsync(andRun, null);
    }

    // ---------------------------------------------------------------------
    //  RIGHT COLUMN Platform + build configuration
    // ---------------------------------------------------------------------

    private void DrawPlatformColumn(Paper paper, FontFile font, float colW, float bodyH)
    {
        using (paper.Column("bp_plat").Width(colW).Height(bodyH).Enter())
        {
            HeaderStrip(paper, "bp_plat_h", Loc.Get("buildprofile.header"), font, () =>
                EditorGUI.Chip(paper, "bp_prof_add", $"{EditorIcons.Plus}  {Loc.Get("buildprofile.add")}",
                    () => BuildProfileCreateDialog.Open(created =>
                        BuildProfileRegistry.SetActive(_buildSettings, created.Guid))));

            Origami.ScrollView(paper, "bp_plat_scroll", colW, bodyH - HeaderH - 1).Body(() =>
            {
                DrawProfileList(paper, font);
                DrawActiveProfileSettings(paper, font);

                EditorGUI.SectionHeader(paper, "bp_cfg_h", Loc.Get("build.configuration"));

                EditorGUI.SettingsRow(paper, "bp_cfg_config", Loc.Get("build.configuration"), () =>
                    Origami.EnumDropdown(paper, "bp_cfg_config_v", _buildSettings.Config,
                        v => { _buildSettings.Config = v; EditorRegistries.SaveSettings(); }).Show(), separator: false);

                EditorGUI.SettingsRow(paper, "bp_cfg_out", Loc.Get("build.output_dir"), () =>
                    Origami.TextField(paper, "bp_cfg_out_v", _buildSettings.OutputDirectory,
                        v => { _buildSettings.OutputDirectory = v; EditorRegistries.SaveSettings(); }).Show(), separator: false);

                EditorGUI.SettingsRow(paper, "bp_cfg_pkg", Loc.Get("build.asset_packaging"), () =>
                    Origami.EnumDropdown(paper, "bp_cfg_pkg_v", _buildSettings.PackagingMode,
                        v => { _buildSettings.PackagingMode = v; EditorRegistries.SaveSettings(); }).Show(), separator: false);

                EditorGUI.SettingsRow(paper, "bp_cfg_asset", Loc.Get("build.asset_export"), () =>
                    Origami.EnumDropdown(paper, "bp_cfg_asset_v", _buildSettings.AssetMode,
                        v => { _buildSettings.AssetMode = v; EditorRegistries.SaveSettings(); }).Show(), separator: false);

                if (_buildSettings.PackagingMode == AssetPackagingMode.XEnginePak)
                    EditorGUI.SettingsRow(paper, "bp_cfg_pak", Loc.Get("build.max_pak_size"), () =>
                        Origami.IntSlider(paper, "bp_cfg_pak_v", _buildSettings.MaxPakSizeMB,
                            v => { _buildSettings.MaxPakSizeMB = v; EditorRegistries.SaveSettings(); },
                            256, 4096).Show(), separator: false);

                paper.Box("bp_plat_pad").Height(12);
            });
        }
    }

    // ---------------------------------------------------------------------
    //  Build profiles
    // ---------------------------------------------------------------------

    private void DrawProfileList(Paper paper, FontFile font)
    {
        List<BuildProfileHandle> profiles = BuildProfileRegistry.LoadAll();

        using (paper.Column("bp_proflist").Height(UnitValue.Auto).Padding(10, 10, 10, 6).ColBetween(4).Enter())
        {
            if (profiles.Count == 0)
            {
                paper.Box("bp_prof_empty").Height(52).IsNotInteractable()
                    .Text(Loc.Get("buildprofile.empty"), font)
                    .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleCenter);
                return;
            }

            for (int i = 0; i < profiles.Count; i++)
                DrawProfileRow(paper, font, profiles[i]);
        }
    }

    private void DrawProfileRow(Paper paper, FontFile font, BuildProfileHandle handle)
    {
        bool active = _buildSettings.ActiveProfileGuid == handle.Guid;
        string key = handle.Guid.ToString("N");
        string platform = BuildProfileRegistry.ResolvePipelineDisplayName(handle.Profile.PipelineId);

        using (paper.Row($"bp_prof_{key}").Height(38).Rounded(8).Padding(10, 8, 0, 0).RowBetween(8)
            .BackgroundColor(active ? EditorTheme.Selected : EditorTheme.Glass)
            .BorderColor(active ? EditorTheme.Accent : EditorTheme.BorderSoft).BorderWidth(1)
            .Hovered.BorderColor(active ? EditorTheme.Accent : EditorTheme.BorderStrong).End()
            .OnClick(handle.Guid, (g, _) => BuildProfileRegistry.SetActive(_buildSettings, g))
            .Enter())
        {
            paper.Box($"bp_prof_{key}_ic").Width(18).IsNotInteractable()
                .Text(ResolveProfileIcon(handle.Profile.PipelineId), font)
                .TextColor(active ? EditorTheme.Ink500 : EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleCenter);

            using (paper.Column($"bp_prof_{key}_c").Height(30)
                .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).ColBetween(1).Enter())
            {
                paper.Box($"bp_prof_{key}_nm").Height(15).IsNotInteractable()
                    .Text(handle.DisplayName, font)
                    .TextColor(active ? EditorTheme.Ink500 : EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).TextTruncate();

                paper.Box($"bp_prof_{key}_pl").Height(14).IsNotInteractable()
                    .Text(platform, font).TextColor(EditorTheme.Ink300)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).TextTruncate();
            }

            if (active)
                paper.Box($"bp_prof_{key}_badge").Width(52).Height(18).Rounded(9)
                    .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                    .BackgroundColor(EditorTheme.Accent).IsNotInteractable()
                    .Text(Loc.Get("buildprofile.active"), font).TextColor(EditorTheme.AccentText)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter);

            paper.Box($"bp_prof_{key}_rm").Width(24).Height(24).Rounded(6)
                .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                .Hovered.BackgroundColor(EditorTheme.Hover).End()
                .Text(EditorIcons.Xmark, font).TextColor(EditorTheme.Ink300)
                .Hovered.TextColor(EditorTheme.Red400).End()
                .FontSize(9f).Alignment(TextAlignment.MiddleCenter)
                .OnClick(handle, (h, _) => DeleteProfile(h));
        }
    }

    private static string ResolveProfileIcon(string pipelineId)
    {
        foreach (BuildPipeline pipeline in BuildProfileRegistry.EnumeratePipelines())
            if (string.Equals(pipeline.Id, pipelineId, StringComparison.OrdinalIgnoreCase))
                return pipeline.Icon;

        return EditorIcons.Hammer;
    }

    private void DeleteProfile(BuildProfileHandle handle)
    {
        EditorAssetBackend.Instance?.DeleteAsset(handle.RelativePath);

        // Deleting the active profile leaves the project with no selection rather than silently
        // promoting another platform, which would change what the Build button does.
        if (_buildSettings.ActiveProfileGuid == handle.Guid)
            BuildProfileRegistry.SetActive(_buildSettings, Guid.Empty);
    }

    private void DrawActiveProfileSettings(Paper paper, FontFile font)
    {
        BuildProfileHandle? active = BuildProfileRegistry.GetActive(_buildSettings);
        if (active == null) return;

        BuildProfile profile = active.Value.Profile;

        if (profile.PlatformSettings != null)
        {
            EditorGUI.SectionHeader(paper, "bp_prof_h",
                BuildProfileRegistry.ResolvePipelineDisplayName(profile.PipelineId), first: true);
            profile.PlatformSettings.OnGUI(paper);
        }

        EditorGUI.SectionHeader(paper, "bp_prof_ovr_h", Loc.Get("buildprofile.overrides"));

        EditorGUI.SettingsToggle(paper, "bp_prof_ovr_out", Loc.Get("buildprofile.override_output"),
            profile.OverrideOutputDirectory,
            v => { profile.OverrideOutputDirectory = v; SaveActiveProfile(); }, separator: false);

        if (profile.OverrideOutputDirectory)
            EditorGUI.SettingsRow(paper, "bp_prof_ovr_out_v", Loc.Get("build.output_dir"), () =>
                Origami.TextField(paper, "bp_prof_ovr_out_vv", profile.OutputDirectory,
                    v => { profile.OutputDirectory = v; SaveActiveProfile(); }).Show(), separator: false);

        EditorGUI.SettingsToggle(paper, "bp_prof_ovr_cfg", Loc.Get("buildprofile.override_config"),
            profile.OverrideConfiguration,
            v => { profile.OverrideConfiguration = v; SaveActiveProfile(); }, separator: false);

        if (profile.OverrideConfiguration)
            EditorGUI.SettingsRow(paper, "bp_prof_ovr_cfg_v", Loc.Get("build.configuration"), () =>
                Origami.EnumDropdown(paper, "bp_prof_ovr_cfg_vv", profile.Configuration,
                    v => { profile.Configuration = v; SaveActiveProfile(); }).Show(), separator: false);

        EditorGUI.SectionHeader(paper, "bp_prof_imp_h", Loc.Get("buildprofile.import_overrides"));

        EditorGUI.SettingsToggle(paper, "bp_prof_imp_on", Loc.Get("buildprofile.import_enabled"),
            profile.AssetImportOverrides.Enabled,
            v => { profile.AssetImportOverrides.Enabled = v; SaveActiveProfile(); }, separator: false);

        if (profile.AssetImportOverrides.Enabled)
        {
            EditorGUI.SettingsToggle(paper, "bp_prof_mesh_compress", "Mesh Vertex Compression",
                profile.AssetImportOverrides.CompressMeshVertexData,
                v => { profile.AssetImportOverrides.CompressMeshVertexData = v; SaveActiveProfile(); }, separator: false);
            EditorGUI.SettingsRow(paper, "bp_prof_imp_max", Loc.Get("buildprofile.import_max_texture"), () =>
                Origami.NumericField<int>(paper, "bp_prof_imp_max_v", profile.AssetImportOverrides.MaxTextureSize,
                    v => { profile.AssetImportOverrides.MaxTextureSize = Math.Max(0, v); SaveActiveProfile(); })
                    .Min(0).Show(), separator: false);

            EditorGUI.SettingsToggle(paper, "bp_prof_imp_unc", Loc.Get("buildprofile.import_uncompressed"),
                profile.AssetImportOverrides.ForceUncompressed,
                v => { profile.AssetImportOverrides.ForceUncompressed = v; SaveActiveProfile(); }, separator: false);
        }
    }

    /// <summary>
    /// Persist the active profile asset. Saving swaps the cached instance, so the next frame re-reads
    /// the profile through the registry rather than holding on to the one that was just edited.
    /// </summary>
    private void SaveActiveProfile()
    {
        BuildProfileHandle? active = BuildProfileRegistry.GetActive(_buildSettings);
        if (active != null) BuildProfileRegistry.Save(active.Value);
    }

    // ---------------------------------------------------------------------
    //  FOOTER summary + build actions / progress
    // ---------------------------------------------------------------------

    private void DrawFooter(Paper paper, FontFile font, float width)
    {
        paper.Box("bp_ftr_top").Width(width).Height(1).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

        var mono = EditorTheme.FontMono ?? font;
        bool running = IsBuildRunning;

        int enabled = _buildSettings.Scenes.Count(s => s.Enabled);
        BuildProfileHandle? active = BuildProfileRegistry.GetActive(_buildSettings);
        string profileName = active?.DisplayName ?? Loc.Get("buildprofile.none");
        string leftText = running
            ? BuildState
            : Loc.Get("build.scene_count", new { count = enabled }) + $"  ·  {profileName}  ·  {_buildSettings.Config}";

        using (paper.Row("bp_footer").Width(width).Height(FooterH - 1).Padding(16, 16, 0, 0).Enter())
        {
            paper.Box("bp_ftr_ic").Width(20).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                .Text(EditorIcons.Hammer, font)
                .TextColor(running ? EditorTheme.Accent : EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleCenter);

            paper.Box("bp_ftr_sum").Width(running ? UnitValue.Pixels(180) : UnitValue.Auto)                .Margin(10, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                .Text(leftText, mono).TextColor(EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).TextTruncate();

            // Progress bar fills the middle, between the summary and the action buttons.
            using (paper.Row("bp_ftr_progw").Height(UnitValue.Auto).Margin(14, 14, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
                Origami.ProgressBar(paper, "bp_ftr_bar", BuildProgress).Thickness(8).ShowPercent("F0").Show();

            EditorGUI.Chip(paper, "bp_ftr_build", $"{EditorIcons.Hammer}  {Loc.Get("build.build")}", () => TryStartBuild(false));

            paper.Box("bp_ftr_bgap").Width(8).Height(1).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable();

            EditorGUI.CtaButton(paper, "bp_ftr_buildrun", $"{EditorIcons.Play}  {Loc.Get("build.build_run")}", EditorTheme.Accent, () => TryStartBuild(true));
        }
    }

    // ---------------------------------------------------------------------
    //  Shared chrome helpers
    // ---------------------------------------------------------------------

    private static void HeaderStrip(Paper paper, string id, string title, FontFile font, Action drawAction)
    {
        var semi = EditorTheme.FontSemiBold ?? font;

        using (paper.Row(id).Height(HeaderH).Padding(14, 14, 0, 0).RowBetween(8).Enter())
        {
            paper.Box($"{id}_t").Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                .Text(title.ToUpperInvariant(), semi).TextColor(EditorTheme.AccentText)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

            drawAction?.Invoke();
        }
        paper.Box($"{id}_d").Height(1).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();
    }

    public List<BuildPipelineInfo> GetBuildPlatforms()
    {
        List<BuildPipelineInfo> pipelines = new List<BuildPipelineInfo>();

        foreach (var type in ScriptAssemblyManager.GetAllTypes())
        {
            if (!type.IsSubclassOf(typeof(BuildPipeline)) || type.IsAbstract)
                continue;

            string name = type.Name;
            string icon = EditorIcons.Desktop;
            try
            {
                if (Activator.CreateInstance(type) is BuildPipeline bp)
                {
                    name = bp.DisplayName;
                    icon = bp.Icon;
                }
            }
            catch { }

            pipelines.Add(new BuildPipelineInfo { BuildPipelineType = type, Name = name, Icon = icon });
        }

        return pipelines;
    }

}
