using System;
using System.IO;
using XEngine.Editor.Build.Profiles;

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
/// Per-quality-tier settings (Unity 6.7 QualitySettings customRenderPipeline). Each tier may
/// override the render pipeline asset; an empty reference falls back to the Graphics default
/// (tier override → Graphics default → engine built-in, resolved by UniversalPipelineAssignment).
/// </summary>
[ProjectSettings("Quality", EditorIcons.Gauge, order: 30)]
public sealed class QualitySettingsPanel : ProjectSettingsBase
{
    /// <summary>Pipeline override for the Preview tier (editor Scene/Game preview). Empty = Graphics default.</summary>
    public AssetRef<UniversalRenderPipelineAsset> PreviewPipeline;

    /// <summary>Pipeline override for the Full tier (play mode / player). Empty = Graphics default.</summary>
    public AssetRef<UniversalRenderPipelineAsset> FullPipeline;

    public override void Apply()
    {
        QualitySettings.SetPipelineOverride(QualitySettings.QualityTier.Preview, Resolve(PreviewPipeline));
        QualitySettings.SetPipelineOverride(QualitySettings.QualityTier.Full, Resolve(FullPipeline));

        // Re-resolve the assignment chain; the Graphics settings may apply before or after this
        // panel depending on load order, and GraphicsSettings.ApplyPipeline is idempotent.
        EditorRegistries.GetSettings<GraphicsSettings>().ApplyPipeline();
    }

    public override void ResetToDefaults()
    {
        PreviewPipeline = default;
        FullPipeline = default;
    }

    public override void OnGUI(Paper paper, float width)
    {
        Origami.Header(paper, "quality_h_pipeline", $"{EditorIcons.Gauge}  Render Pipeline per Quality Level")
            .Underline().Show();
        DrawTierPipeline(paper, "quality_preview", "Preview Tier",
            () => PreviewPipeline, value => PreviewPipeline = value);
        DrawTierPipeline(paper, "quality_full", "Full Tier",
            () => FullPipeline, value => FullPipeline = value);
        var active = BuildProfileRegistry.GetActive(EditorRegistries.GetSettings<BuildSettings>());
        var reference = FullPipeline;
        if (active != null)
        {
            Origami.Label(paper, "quality_profile", "Build Profile: " + active.Value.Profile.DisplayName).Show();
            foreach (var setting in active.Value.Profile.SettingOverrides)
                if (setting.Enabled && setting.SettingsName == "Quality" && setting.FieldPath == "FullPipeline" &&
                    Guid.TryParse(setting.Value.Trim('"'), out var guid))
                    reference = new AssetRef<UniversalRenderPipelineAsset>(guid);
        }
        var pipeline = Resolve(reference) ?? Resolve(EditorRegistries.GetSettings<GraphicsSettings>().DefaultPipeline);
        if (pipeline != null) DrawPlayerQuality(paper, pipeline);
    }

    private enum ShadowResolution { R512 = 512, R1024 = 1024, R2048 = 2048, R4096 = 4096, R8192 = 8192 }

    private void DrawPlayerQuality(Paper paper, UniversalRenderPipelineAsset pipeline)
    {
        Origami.Header(paper, "quality_player_h", "Player Render Quality").Underline().Show();
        Origami.Label(paper, "quality_player_asset", "Pipeline: " + pipeline.Name).Show();
        EditorGUI.SettingsToggle(paper, "quality_shadows", "Shadows", pipeline.ShadowsEnabled,
            value => { pipeline.ShadowsEnabled = value; SavePipeline(pipeline); });
        EditorGUI.SettingsRow(paper, "quality_atlas", "Shadow Atlas Size", () =>
            Origami.EnumDropdown(paper, "quality_atlas_v", (ShadowResolution)pipeline.ShadowAtlasSize,
                value => { pipeline.ShadowAtlasSize = (int)value; SavePipeline(pipeline); }).Show());
        EditorGUI.SettingsRow(paper, "quality_soft", "Soft Shadow Quality", () =>
            Origami.EnumDropdown(paper, "quality_soft_v", pipeline.SoftShadowQuality,
                value => { pipeline.SoftShadowQuality = value; SavePipeline(pipeline); }).Show());
        EditorGUI.SettingsRow(paper, "quality_cascades", "Max Shadow Cascades", () =>
            Origami.EnumDropdown(paper, "quality_cascades_v", (DirectionalLight.CascadeCount)pipeline.MaxShadowCascades,
                value => { pipeline.MaxShadowCascades = (int)value; SavePipeline(pipeline); }).Show());
        EditorGUI.SettingsRow(paper, "quality_distance", "Shadow Distance", () =>
            Origami.NumericField<float>(paper, "quality_distance_v", pipeline.ShadowDistance,
                value => { pipeline.ShadowDistance = Math.Clamp(value, 1f, 1000f); SavePipeline(pipeline); }).Min(1).Max(1000).Show());
        EditorGUI.SettingsRow(paper, "quality_scale", "Render Scale", () =>
            Origami.NumericField<float>(paper, "quality_scale_v", pipeline.RenderScale,
                value => { pipeline.RenderScale = Math.Clamp(value, .25f, 2f); SavePipeline(pipeline); }).Min(.25f).Max(2f).Show());
        EditorGUI.SettingsToggle(paper, "quality_hdr", "HDR", pipeline.Hdr,
            value => { pipeline.Hdr = value; SavePipeline(pipeline); });
    }

    private void SavePipeline(UniversalRenderPipelineAsset pipeline)
    {
        var entry = EditorAssetBackend.Instance?.GetEntry(pipeline.AssetID);
        if (entry == null || Projects.Project.Current == null) return;
        var echo = ScriptableObjectAssetEditor.SerializeFullAsset(pipeline);
        File.WriteAllText(Path.Combine(Projects.Project.Current.AssetsPath, entry.Path), echo.WriteToString());
        EditorAssetBackend.Instance!.Reimport(entry.Guid);
        Apply();
    }

    private static UniversalRenderPipelineAsset? Resolve(AssetRef<UniversalRenderPipelineAsset> reference)
    {
        if (reference.AssetID == Guid.Empty)
            return null;
        reference.EnsureLoaded();
        return reference.Res;
    }

    private void DrawTierPipeline(Paper paper, string id, string label,
        Func<AssetRef<UniversalRenderPipelineAsset>> get, Action<AssetRef<UniversalRenderPipelineAsset>> set)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null)
            return;

        UniversalRenderPipelineAsset current = get().Res;
        string name = current != null ? current.Name : "None (Graphics default)";

        EditorGUI.SettingsRow(paper, id, label, () =>
        {
            using (paper.Row(id + "_row").Height(20).Enter())
            {
                paper.Box(id + "_name")
                    .Width(UnitValue.Stretch()).Height(20).Clip().IsNotInteractable()
                    .Text(name, font)
                    .TextColor(current != null ? EditorTheme.Ink500 : EditorTheme.Ink200)
                    .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleLeft);

                paper.Box(id + "_pick")
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
                                set(new AssetRef<UniversalRenderPipelineAsset>(asset as UniversalRenderPipelineAsset));
                                EditorRegistries.SaveSettings();
                                Apply();
                            });
                    });

                if (current != null)
                {
                    paper.Box(id + "_clear")
                        .Width(18).Height(18).Rounded(4)
                        .Text(EditorIcons.X, font).TextColor(EditorTheme.Ink200)
                        .FontSize(12f).Alignment(TextAlignment.MiddleCenter)
                        .Hovered.BackgroundColor(EditorTheme.Hover).End()
                        .OnClick((object)null, (_, e) =>
                        {
                            e.StopPropagation();
                            set(default);
                            EditorRegistries.SaveSettings();
                            Apply();
                        });
                }
            }
        });
    }
}
