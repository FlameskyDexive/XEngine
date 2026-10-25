using System;
using System.Collections.Generic;
using System.Linq;

using XEngine.Editor.Build.Profiles;
using XEngine.Editor.Utils;

using XEngine.Editor.Inspector;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Theming;

using XEngine.Editor.GUI;
namespace XEngine.Editor.Projects.Settings;

public enum BuildTarget
{
    Windows = 0,
    Linux = 1,
    MacOS = 2,
    Android = 3,
    iOS = 4,
    HarmonyOS = 5,
    Web = 6,
}
public enum BuildConfiguration { Debug, Release }
public enum AssetExportMode { AllAssets, DependenciesOnly }

public class SceneBuildEntry
{
    public string Path = "";
    public Guid SceneGuid;
    public bool Enabled = true;
}

[ProjectSettings("Build", EditorIcons.Hammer, order: 50, exportToBuild: false)]
public sealed class BuildSettings : ProjectSettingsBase
{
    public override bool DrawInProjectSettingsPanel => false;

    public List<SceneBuildEntry> Scenes = new();

    public BuildConfiguration Config = BuildConfiguration.Release;
    public string OutputDirectory = "Builds";
    public AssetPackagingMode PackagingMode = AssetPackagingMode.XEnginePak;
    public AssetExportMode AssetMode = AssetExportMode.DependenciesOnly;
    public int MaxPakSizeMB = 2048;

    /// <summary>
    /// Asset GUID of the active <c>BuildProfile</c>. This is the authoritative selection; the older
    /// <see cref="ActivePipelineId"/> is kept in sync from it so `--platform` and the editor's
    /// platform defines keep working. <see cref="Guid.Empty"/> means "no profile selected yet",
    /// which is the state a project is in before migration runs.
    /// </summary>
    public Guid ActiveProfileGuid = Guid.Empty;

    /// <summary>
    /// Stable id (<see cref="BuildPipeline.Id"/>) of the currently selected build pipeline.
    /// Persisted so the Build panel and the <c>--buildmode</c> CLI agree on which platform to build.
    /// Empty/null resolves to the desktop pipeline for back-compat with pre-multi-platform projects.
    /// Derived from <see cref="ActiveProfileGuid"/> once build profiles exist.
    /// </summary>
    public string ActivePipelineId = "";

    /// <summary>
    /// Per-platform profiles.  Missing entries will be created with
    /// defaults on first access.
    /// </summary>
    public List<PlatformBuildProfile> PlatformProfiles = new();

    /// <summary>
    /// Platform settings owned by the active <c>BuildProfile</c>, but only when that profile targets
    /// <paramref name="pipelineType"/>. Returns null when no profile is active, when the active one
    /// builds a different platform, or when there is no project open at all (standalone
    /// <see cref="BuildSettings"/> instances in tests and the CLI) - in which case callers fall back
    /// to the legacy per-pipeline list below.
    /// </summary>
    private PlatformBuildProfile? GetActiveProfileSettings(Type pipelineType)
    {
        PlatformBuildProfile? settings = BuildProfileRegistry.GetActive(this)?.Profile.PlatformSettings;
        return settings != null && settings.GetPipelineType() == pipelineType ? settings : null;
    }

    /// <summary>
    /// Returns (or lazily creates) the profile for the given target.
    /// </summary>
    public T GetProfile<T>(Type pipelineType) where T : PlatformBuildProfile
    {
        if (GetActiveProfileSettings(pipelineType) is T fromActiveProfile)
            return fromActiveProfile;

        var profile = PlatformProfiles.FirstOrDefault(profile => pipelineType == profile.GetPipelineType());
        if (profile == null)
        {
            profile = System.Activator.CreateInstance<T>();
            PlatformProfiles.Add(profile);
        }
        return (T)profile;
    }

    /// <summary>
    /// Returns or creates the profile for the given target.
    /// If not present, returns null.
    /// </summary>
    public PlatformBuildProfile GetOrCreateProfile(Type pipelineType)
    {
        PlatformBuildProfile? fromActiveProfile = GetActiveProfileSettings(pipelineType);
        if (fromActiveProfile != null)
            return fromActiveProfile;

        var profile = PlatformProfiles.FirstOrDefault(profile => pipelineType == profile.GetPipelineType());

        // If the profile is null, create the profile
        if (profile == null)
        {
            Type targetType = null;
            foreach (var type in EditorUtils.GetAllTypes())
            {
                if (!type.IsSubclassOf(typeof(PlatformBuildProfile)) || type == typeof(PlatformBuildProfile)) continue;
                var check = Activator.CreateInstance(type) as PlatformBuildProfile;
                if (check?.GetPipelineType() == pipelineType)
                {
                    targetType = type;
                    break;
                }
            }

            if (targetType != null)
            {
                profile = (PlatformBuildProfile)System.Activator.CreateInstance(targetType);
                PlatformProfiles.Add(profile);
                EditorRegistries.SaveSettings();
            }
        }
        return profile;
    }

    public override void OnGUI(Paper paper, float width)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        // Scene List
        Origami.Header(paper, "bld_scenes_h", $"{EditorIcons.Shapes}  Scenes").Underline().Show();

        for (int i = 0; i < Scenes.Count; i++)
        {
            int idx = i;
            var scene = Scenes[i];

            using (paper.Row($"bld_scene_{i}").Height(EditorTheme.RowHeight).RowBetween(4).ChildLeft(4).Enter())
            {
                // Index
                paper.Box($"bld_si_{i}")
                    .Width(20).Height(EditorTheme.RowHeight)
                    .Text(i.ToString(), font).TextColor(EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter);

                // Enable toggle
                Origami.Checkbox(paper, $"bld_se_{i}", scene.Enabled,
                        v => { scene.Enabled = v; EditorRegistries.SaveSettings(); })
                    .NoLabel().Show();

                // Scene name
                string displayName = !string.IsNullOrEmpty(scene.Path)
                    ? System.IO.Path.GetFileNameWithoutExtension(scene.Path)
                    : scene.SceneGuid.ToString()[..8];

                paper.Box($"bld_sn_{i}")
                    .Height(EditorTheme.RowHeight).ChildLeft(4)
                    .Text(displayName, font).TextColor(EditorTheme.Ink500)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

                // Move up/down
                if (i > 0)
                {
                    paper.Box($"bld_su_{i}")
                        .Width(18).Height(EditorTheme.RowHeight).Rounded(3)
                        .Hovered.BackgroundColor(EditorTheme.Ink200).End()
                        .Text(EditorIcons.ArrowUp, font).TextColor(EditorTheme.Ink400)
                        .FontSize(9f).Alignment(TextAlignment.MiddleCenter)
                        .OnClick(idx, (ci, _) =>
                        {
                            (Scenes[ci - 1], Scenes[ci]) = (Scenes[ci], Scenes[ci - 1]);
                            EditorRegistries.SaveSettings();
                        });
                }

                // Remove
                paper.Box($"bld_sr_{i}")
                    .Width(18).Height(EditorTheme.RowHeight).Rounded(3)
                    .Hovered.BackgroundColor(EditorTheme.Ink200).End()
                    .Text(EditorIcons.Xmark, font).TextColor(EditorTheme.Ink400)
                    .FontSize(9f).Alignment(TextAlignment.MiddleCenter)
                    .OnClick(idx, (ci, _) =>
                    {
                        Scenes.RemoveAt(ci);
                        EditorRegistries.SaveSettings();
                    });
            }
        }

        // Add scene buttons
        using (paper.Row("bld_add_row").Height(EditorTheme.RowHeight).RowBetween(6).ChildLeft(4).Enter())
        {
            Origami.Button(paper, "bld_add_open", $"{EditorIcons.Plus} Add Open Scene", () =>
                {
                    if (EditorSceneManager.CurrentScenePath != null)
                    {
                        var db = EditorAssetBackend.Instance;
                        var entry = db?.GetEntry(EditorSceneManager.CurrentScenePath);
                        if (entry != null && !Scenes.Any(s => s.SceneGuid == entry.Guid))
                        {
                            Scenes.Add(new SceneBuildEntry { Path = entry.Path, SceneGuid = entry.Guid });
                            EditorRegistries.SaveSettings();
                        }
                    }
                }).Width(140).Show();
        }

        paper.Box("bld_sp1").Height(12);

        // Build Configuration
        Origami.Header(paper, "bld_config_h", $"{EditorIcons.Gear}  Configuration").Underline().Show();

        EditorGUI.SettingsEnumDropdown(paper, "bld_config", "Configuration", Config, v => Config = v);
        EditorGUI.SettingsTextField(paper, "bld_output", "Output Directory", OutputDirectory, v => OutputDirectory = v);
        EditorGUI.SettingsEnumDropdown(paper, "bld_packaging", "Asset Packaging", PackagingMode, v => PackagingMode = v);
        EditorGUI.SettingsEnumDropdown(paper, "bld_assetmode", "Asset Export", AssetMode, v => AssetMode = v);

        if (PackagingMode == AssetPackagingMode.XEnginePak)
            EditorGUI.SettingsIntSlider(paper, "bld_maxpak", "Max Pak Size (MB)", MaxPakSizeMB, 256, 4096, v => MaxPakSizeMB = v);

        paper.Box("bld_sp3").Height(12);

    }

    public override void ResetToDefaults()
    {
        Scenes.Clear();

        // Drop the profiles rather than defaulting them in place: this instance is a process-wide
        // singleton that gets reset when a project is opened, so keeping the previous project's
        // platform list would leak its settings into the new one - and, now that build profile
        // migration reads this list, would mint profile assets for platforms the project never
        // configured.
        PlatformProfiles.Clear();
        ActiveProfileGuid = Guid.Empty;
        ActivePipelineId = "";
    }
}
