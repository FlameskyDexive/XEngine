// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Inspector;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Theming;
using XEngine.Ads;
using XEngine.OrigamiUI;
using XEngine.PaperUI;

namespace XEngine.Ads.Editor;

/// <summary>
/// Project Settings "Ads" page: provider selection plus per-vendor app/slot ids. Values are
/// consumed at build time by <see cref="Build.AdsStageProjectContributor"/> (Stage-project
/// glue + permissions + Settings/Ads.json payload) and are the source of truth for the
/// runtime bootstrap (<c>AdsBootstrapper</c>).
/// </summary>
[ProjectSettings("Ads", EditorIcons.Mobile, order: 60, exportToBuild: false)]
public class AdsProjectSettings : ProjectSettingsBase
{
    public string ProviderId = AdProviderIds.Mock;
    public bool UseTestAds = true;

    public string HuaweiAppId = "";
    public string HuaweiRewardVideoSlotId = "";
    public string HuaweiInterstitialSlotId = "";

    public string PangleAppId = "";
    public string PangleRewardVideoSlotId = "";
    public string PangleInterstitialSlotId = "";

    /// <summary>
    /// Project-relative (or absolute) path to the vendor <c>openadsdk.har</c>; required for
    /// pangle builds. CSJ ships the har through its own portal, never the public registry.
    /// </summary>
    public string PangleHarPath = "";

    public override void OnGUI(Paper paper, float width)
    {
        Origami.Header(paper, "ads_header", $"{EditorIcons.Mobile}  Ads").Underline().Show();

        EditorGUI.SettingsTextField(
            paper, "ads_provider", "Provider (mock | huawei-adskit | pangle)", ProviderId,
            v => ProviderId = v.Trim());
        EditorGUI.SettingsToggle(paper, "ads_test", "Use Test Ad Slots", UseTestAds, v => UseTestAds = v);

        Origami.Header(paper, "ads_huawei_header", "Huawei Ads Kit (鲸鸿动能)").Show();
        EditorGUI.SettingsTextField(paper, "ads_hw_app", "App Id", HuaweiAppId, v => HuaweiAppId = v.Trim());
        EditorGUI.SettingsTextField(paper, "ads_hw_reward", "Reward Video Slot Id", HuaweiRewardVideoSlotId, v => HuaweiRewardVideoSlotId = v.Trim());
        EditorGUI.SettingsTextField(paper, "ads_hw_inter", "Interstitial Slot Id", HuaweiInterstitialSlotId, v => HuaweiInterstitialSlotId = v.Trim());

        Origami.Header(paper, "ads_pangle_header", "Pangle (穿山甲)").Show();
        EditorGUI.SettingsTextField(paper, "ads_pg_app", "App Id", PangleAppId, v => PangleAppId = v.Trim());
        EditorGUI.SettingsTextField(paper, "ads_pg_reward", "Reward Video Slot Id", PangleRewardVideoSlotId, v => PangleRewardVideoSlotId = v.Trim());
        EditorGUI.SettingsTextField(paper, "ads_pg_inter", "Interstitial Slot Id", PangleInterstitialSlotId, v => PangleInterstitialSlotId = v.Trim());
        EditorGUI.SettingsTextField(paper, "ads_pg_har", "openadsdk.har Path", PangleHarPath, v => PangleHarPath = v.Trim());
    }

    public override void ResetToDefaults()
    {
        ProviderId = AdProviderIds.Mock;
        UseTestAds = true;
        HuaweiAppId = HuaweiRewardVideoSlotId = HuaweiInterstitialSlotId = "";
        PangleAppId = PangleRewardVideoSlotId = PangleInterstitialSlotId = "";
        PangleHarPath = "";
    }

    /// <summary>Build-time transfer object for the active vendor's configuration.</summary>
    public AdsBuildConfig ResolveBuildConfig(Project? project)
    {
        AdsBuildConfig config = ProviderId switch
        {
            AdProviderIds.HuaweiAdsKit => new AdsBuildConfig
            {
                ProviderId = ProviderId,
                UseTestAds = UseTestAds,
                VendorAppId = HuaweiAppId,
                RewardVideoSlotId = HuaweiRewardVideoSlotId,
                InterstitialSlotId = HuaweiInterstitialSlotId,
            },
            AdProviderIds.Pangle => new AdsBuildConfig
            {
                ProviderId = ProviderId,
                UseTestAds = UseTestAds,
                VendorAppId = PangleAppId,
                RewardVideoSlotId = PangleRewardVideoSlotId,
                InterstitialSlotId = PangleInterstitialSlotId,
            },
            _ => new AdsBuildConfig { ProviderId = AdProviderIds.Mock, UseTestAds = UseTestAds },
        };
        config.HarSourcePath = ResolveProjectPath(project, PangleHarPath);
        return config;
    }

    /// <summary>Resolves a project-relative (or already absolute) path; empty stays empty.</summary>
    private static string ResolveProjectPath(Project? project, string value)
    {
        if (project == null || string.IsNullOrWhiteSpace(value))
            return value;
        if (Path.IsPathRooted(value))
            return Path.GetFullPath(value);
        return Path.GetFullPath(Path.Combine(project.RootPath, value.Replace('/', Path.DirectorySeparatorChar)));
    }
}
