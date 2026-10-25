using System;

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;

using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Popups;
using XEngine.Editor.Inspector;

namespace XEngine.Editor.Projects.Settings;

/// <summary>
/// Project-wide rendering defaults. These settings are exported with player builds and can be
/// changed at runtime through <see cref="QualitySettings"/> for an in-game quality menu.
/// Assign <see cref="DefaultPipeline"/> so cameras use the Universal <see cref="UniversalRenderPipelineAsset"/>.
/// </summary>
[ProjectSettings("Graphics", EditorIcons.Display, order: 35)]
public sealed class GraphicsSettings : ProjectSettingsBase
{
    /// <summary>Project default Universal pipeline (Unity GraphicsSettings.defaultRenderPipeline).</summary>
    public AssetRef<UniversalRenderPipelineAsset> DefaultPipeline;

    /// <summary>
    /// URP global settings asset (Unity 6.7 m_RenderPipelineGlobalSettingsMap). Drives the
    /// shader-variant stripping switches at player-build collect time. Optional: an empty
    /// reference keeps the engine's default stripping behavior.
    /// </summary>
    public AssetRef<UniversalRenderPipelineGlobalSettings> PipelineGlobalSettings;

    public bool ShadowsEnabled = true;
    public float ShadowDistance = 100f;
    public int ShadowAtlasSize = 4096;
    public bool ImageEffectsEnabled = true;
    public QualitySettings.AntiAliasingMode AntiAliasing = QualitySettings.AntiAliasingMode.Disabled;

    public override void Apply()
    {
        ApplyPipeline();

        // Editing a project must retain the editor's Preview tier. The same serialized values are
        // applied when entering Play Mode and by PlayerSettingsLoader in a built player.
        if (Runtime.Application.IsEditor && !Runtime.Application.IsPlaying)
            return;

        QualitySettings.ShadowsEnabled = ShadowsEnabled;
        QualitySettings.ShadowDistance = ShadowDistance;
        QualitySettings.ShadowAtlasSize = ShadowAtlasSize;
        QualitySettings.ImageEffectsEnabled = ImageEffectsEnabled;
        QualitySettings.AntiAliasing = AntiAliasing;
    }

    /// <summary>Point <see cref="UniversalRenderPipeline.Default"/> at <see cref="DefaultPipeline"/>.</summary>
    public void ApplyPipeline()
    {
        ApplyGlobalSettings();

        // AssetRef.Res is non-blocking when async loading is on (the project default), so a
        // GUID-only Graphics.yaml would leave Default.Asset null until a later frame. Startup
        // must resolve the pipeline (and its renderer) synchronously, like Unity's SRP assign.
        if (DefaultPipeline.AssetID == Guid.Empty)
        {
            UniversalPipelineAssignment.GraphicsDefault = null;
            return;
        }

        DefaultPipeline.EnsureLoaded();
        UniversalRenderPipelineAsset? pipeline = DefaultPipeline.Res;
        if (pipeline == null)
        {
            Debug.LogWarning($"GraphicsSettings: default pipeline {DefaultPipeline.AssetID} is missing.");
            UniversalPipelineAssignment.GraphicsDefault = null;
            return;
        }

        DefaultPipeline.LockPermanent();
        pipeline.Renderer.EnsureLoaded();
        UniversalPipelineAssignment.GraphicsDefault = pipeline;
    }

    /// <summary>Cache <see cref="PipelineGlobalSettings"/> for build-time stripping decisions.</summary>
    private void ApplyGlobalSettings()
    {
        if (PipelineGlobalSettings.AssetID == Guid.Empty)
        {
            UniversalRenderPipelineGlobalSettings.Active = null;
            return;
        }

        PipelineGlobalSettings.EnsureLoaded();
        UniversalRenderPipelineGlobalSettings? settings = PipelineGlobalSettings.Res;
        if (settings == null)
        {
            Debug.LogWarning($"GraphicsSettings: pipeline global settings {PipelineGlobalSettings.AssetID} is missing.");
            UniversalRenderPipelineGlobalSettings.Active = null;
            return;
        }

        PipelineGlobalSettings.LockPermanent();
        UniversalRenderPipelineGlobalSettings.Active = settings;
    }

    public override void ResetToDefaults()
    {
        DefaultPipeline = default;
        PipelineGlobalSettings = default;
        ShadowsEnabled = true;
        ShadowDistance = 100f;
        ShadowAtlasSize = 4096;
        ImageEffectsEnabled = true;
        AntiAliasing = QualitySettings.AntiAliasingMode.Disabled;
    }

    public override void OnGUI(Paper paper, float width)
    {
        Origami.Header(paper, "graphics_h_pipeline", $"{EditorIcons.Display}  Scriptable Render Pipeline Settings").Underline().Show();
        DrawDefaultPipeline(paper);
        DrawPipelineGlobalSettings(paper);

        paper.Box("graphics_sp_shadows").Height(8);
        Origami.Header(paper, "graphics_h_shadows", $"{EditorIcons.Sun}  Shadows").Underline().Show();

        EditorGUI.SettingsToggle(paper, "graphics_shadows", "Realtime Shadows", ShadowsEnabled,
            value => { ShadowsEnabled = value; Apply(); EditorRegistries.SaveSettings(); });
        EditorGUI.SettingsSliderField(paper, "graphics_shadow_distance", "Shadow Distance", ShadowDistance, 1f, 1000f,
            value => { ShadowDistance = value; Apply(); EditorRegistries.SaveSettings(); }, "F0");
        EditorGUI.SettingsEnumDropdown(paper, "graphics_shadow_atlas", "Shadow Atlas", ToAtlasSize(ShadowAtlasSize),
            value => { ShadowAtlasSize = (int)value; Apply(); EditorRegistries.SaveSettings(); });

        paper.Box("graphics_sp_post").Height(8);
        Origami.Header(paper, "graphics_h_post", $"{EditorIcons.Display}  Post Processing").Underline().Show();

        EditorGUI.SettingsToggle(paper, "graphics_image_effects", "Image Effects", ImageEffectsEnabled,
            value => { ImageEffectsEnabled = value; Apply(); EditorRegistries.SaveSettings(); });
        EditorGUI.SettingsEnumDropdown(paper, "graphics_aa", "Anti-Aliasing", AntiAliasing,
            value => { AntiAliasing = value; Apply(); EditorRegistries.SaveSettings(); });
    }

    private void DrawDefaultPipeline(Paper paper)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null)
            return;

        UniversalRenderPipelineAsset current = DefaultPipeline.Res;
        string name = current != null ? current.Name : "None";

        EditorGUI.SettingsRow(paper, "graphics_pipeline", "Default Render Pipeline", () =>
        {
            using (paper.Row("graphics_pipeline_row").Height(20).Enter())
            {
                paper.Box("graphics_pipeline_name")
                    .Width(UnitValue.Stretch()).Height(20).Clip().IsNotInteractable()
                    .Text(name, font)
                    .TextColor(current != null ? EditorTheme.Ink500 : EditorTheme.Ink200)
                    .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleLeft);

                paper.Box("graphics_pipeline_pick")
                    .Width(18).Height(18).Rounded(4)
                    .Text(EditorIcons.CircleDot, font).TextColor(EditorTheme.Ink200)
                    .FontSize(12f).Alignment(TextAlignment.MiddleCenter)
                    .Hovered.BackgroundColor(EditorTheme.Hover).End()
                    .OnClick((object)null, (_, e) =>
                    {
                        e.StopPropagation();
                        SelectorModal.Open("Select Pipeline Asset", typeof(UniversalRenderPipelineAsset), SelectorTabs.Assets,
                            asset =>
                            {
                                DefaultPipeline = new AssetRef<UniversalRenderPipelineAsset>(asset as UniversalRenderPipelineAsset);
                                EditorRegistries.SaveSettings();
                                Apply();
                            });
                    });

                if (current != null)
                {
                    paper.Box("graphics_pipeline_clear")
                        .Width(18).Height(18).Rounded(4)
                        .Text(EditorIcons.X, font).TextColor(EditorTheme.Ink200)
                        .FontSize(12f).Alignment(TextAlignment.MiddleCenter)
                        .Hovered.BackgroundColor(EditorTheme.Hover).End()
                        .OnClick((object)null, (_, e) =>
                        {
                            e.StopPropagation();
                            DefaultPipeline = default;
                            EditorRegistries.SaveSettings();
                            Apply();
                        });
                }
            }
        });
    }

    /// <summary>
    /// URP global settings mapping row (Unity 6.7 m_RenderPipelineGlobalSettingsMap): picks the
    /// asset that owns the shader-variant stripping switches. Empty keeps engine defaults.
    /// </summary>
    private void DrawPipelineGlobalSettings(Paper paper)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null)
            return;

        UniversalRenderPipelineGlobalSettings current = PipelineGlobalSettings.Res;
        string name = current != null ? current.Name : "None";

        EditorGUI.SettingsRow(paper, "graphics_global_settings", "Pipeline Global Settings", () =>
        {
            using (paper.Row("graphics_global_row").Height(20).Enter())
            {
                paper.Box("graphics_global_name")
                    .Width(UnitValue.Stretch()).Height(20).Clip().IsNotInteractable()
                    .Text(name, font)
                    .TextColor(current != null ? EditorTheme.Ink500 : EditorTheme.Ink200)
                    .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleLeft);

                paper.Box("graphics_global_pick")
                    .Width(18).Height(18).Rounded(4)
                    .Text(EditorIcons.CircleDot, font).TextColor(EditorTheme.Ink200)
                    .FontSize(12f).Alignment(TextAlignment.MiddleCenter)
                    .Hovered.BackgroundColor(EditorTheme.Hover).End()
                    .OnClick((object)null, (_, e) =>
                    {
                        e.StopPropagation();
                        SelectorModal.Open("Select Pipeline Global Settings", typeof(UniversalRenderPipelineGlobalSettings), SelectorTabs.Assets,
                            asset =>
                            {
                                PipelineGlobalSettings = new AssetRef<UniversalRenderPipelineGlobalSettings>(asset as UniversalRenderPipelineGlobalSettings);
                                EditorRegistries.SaveSettings();
                                Apply();
                            });
                    });

                if (current != null)
                {
                    paper.Box("graphics_global_clear")
                        .Width(18).Height(18).Rounded(4)
                        .Text(EditorIcons.X, font).TextColor(EditorTheme.Ink200)
                        .FontSize(12f).Alignment(TextAlignment.MiddleCenter)
                        .Hovered.BackgroundColor(EditorTheme.Hover).End()
                        .OnClick((object)null, (_, e) =>
                        {
                            e.StopPropagation();
                            PipelineGlobalSettings = default;
                            EditorRegistries.SaveSettings();
                            Apply();
                        });
                }
            }
        });
    }

    private static ShadowAtlasSizeOption ToAtlasSize(int value)
        => value switch
        {
            512 => ShadowAtlasSizeOption._512,
            1024 => ShadowAtlasSizeOption._1024,
            2048 => ShadowAtlasSizeOption._2048,
            8192 => ShadowAtlasSizeOption._8192,
            _ => ShadowAtlasSizeOption._4096,
        };

    private enum ShadowAtlasSizeOption
    {
        _512 = 512,
        _1024 = 1024,
        _2048 = 2048,
        _4096 = 4096,
        _8192 = 8192,
    }
}
