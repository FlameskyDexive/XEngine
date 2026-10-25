// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Ads;
using XEngine.Ads.HarmonyOS;
using XEngine.Async;
using XEngine.Runtime;

namespace XEngine.Ads.Samples;

/// <summary>
/// AdsDemo: the complete rewarded-video flow a game needs — bootstrap from the build
/// payload when present (HarmonyOS players), fall back to the mock provider in the
/// editor/desktop, preload on launch, and grant a reward on
/// <see cref="AdShowResult.RewardEarned"/>.
///
/// Attach to any GameObject and wire <see cref="OnRewardGranted"/> to your gameplay
/// (revive, currency, …). Show the video from gameplay via <see cref="ShowRewardVideo"/>.
/// See the package Documentation~/index.md for the Project Settings fields this sample
/// reads at build time.
/// </summary>
public sealed class AdsDemoComponent : MonoBehaviour
{
    /// <summary>Preload a reward video this many seconds after start (0 = immediately).</summary>
    public float PreloadDelaySeconds = 5f;

    /// <summary>Whether the demo also preloads an interstitial.</summary>
    public bool PreloadInterstitial;

    /// <summary>Invoked on the ad-completion thread when a reward is verified; marshal before touching scene state.</summary>
    public Action? OnRewardGranted;

    private bool _bootstrapped;

    public override void Start()
    {
        RunAsync().Forget();
    }

    public override void OnRemovedFromScene()
    {
        if (_bootstrapped)
        {
            AdsService.Provider?.ShowEnd -= OnShowEnd;
            AdsService.Close();
            _bootstrapped = false;
        }
    }

    /// <summary>
    /// Entry point for gameplay: show a reward video; the reward (if earned) flows through
    /// <see cref="OnRewardGranted"/>.
    /// </summary>
    public async XTask ShowRewardVideo()
    {
        if (!AdsService.IsLoaded(AdType.RewardVideo))
        {
            AdLoadResult load = await AdsService.Load(AdType.RewardVideo);
            if (!load.Success)
            {
                Debug.LogWarning("[AdsDemo] reward video load failed: " + load.Error);
                return;
            }
        }

        AdShowResult show = await AdsService.Show(AdType.RewardVideo);
        if (!show.Success)
        {
            Debug.LogWarning("[AdsDemo] reward video show failed: " + show.Error);
        }
    }

    private async XTaskVoid RunAsync()
    {
        if (!AdsService.IsInitialized)
        {
            await BootstrapAsync();
        }

        AdsService.Provider!.ShowEnd += OnShowEnd;

        if (PreloadDelaySeconds > 0f)
        {
            await XTask.Delay(TimeSpan.FromSeconds(PreloadDelaySeconds), DelayType.DeltaTime);
        }
        AdLoadResult reward = await AdsService.Load(AdType.RewardVideo);
        Debug.Log("[AdsDemo] reward video preload: " + (reward.Success ? "ready" : reward.Error));
        if (PreloadInterstitial)
        {
            await AdsService.Load(AdType.Interstitial);
        }
    }

    private async XTask BootstrapAsync()
    {
        // Packaged players: the build writes Settings/Ads.json into the content root
        // (resfile on HarmonyOS) from Project Settings → Ads.
        if (AdsBootstrapper.TryLoadFromPlayerSettings(out AdsSettings settings))
        {
            IAdProvider? provider = settings.ProviderId switch
            {
                AdProviderIds.HuaweiAdsKit => new HarmonyOSAdProvider(AdProviderIds.HuaweiAdsKit),
                AdProviderIds.Pangle => new HarmonyOSAdProvider(AdProviderIds.Pangle),
                _ => null,
            };
            if (provider != null)
            {
                AdsService.Configure(provider);
                await AdsService.Initialize(settings);
                _bootstrapped = true;
                return;
            }
        }

        // Editor / desktop / unpackaged player: the mock provider runs the full flow offline.
        AdsService.Configure(new MockAdProvider());
        await AdsService.Initialize(new AdsSettings { ProviderId = AdProviderIds.Mock, UseTestAds = true });
        _bootstrapped = true;
    }

    private void OnShowEnd(AdType type, bool rewardEarned)
    {
        if (type == AdType.RewardVideo && rewardEarned)
        {
            Debug.Log("[AdsDemo] reward verified — granting.");
            OnRewardGranted?.Invoke();
        }
    }
}
