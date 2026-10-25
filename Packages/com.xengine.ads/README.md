# XEngine Ads

Advertising monetization for XEngine games. v1 covers the two full-screen,
SDK-hosted ad formats that drive most game revenue — **rewarded video** and
**interstitial** — behind a small provider abstraction. The first target
platform is **HarmonyOS NEXT** (Huawei Ads Kit + Pangle); the bundled
**mock provider** runs the complete load → show → reward flow in the editor
and in unit tests without a device.

## Install

Package Manager → official registry → `com.xengine.ads` (optional package,
not mounted by default).

## Usage

```csharp
using XEngine.Ads;
using XEngine.Async;

// once at startup — pick a provider from your AdsSettings
AdsService.Configure(new MockAdProvider());
await AdsService.Initialize(settings);

// preload (Huawei requires show within 1 h of load; the provider tracks this)
await AdsService.Load(AdType.RewardVideo);

if (AdsService.IsLoaded(AdType.RewardVideo))
{
    AdShowResult result = await AdsService.Show(AdType.RewardVideo);
    if (result.Success && result.RewardEarned)
        GrantRevive();
}
```

Subscribe to lifecycle events on the provider instance for analytics:

```csharp
provider.ShowEnd += (type, rewardEarned) => Analytics.Send("ad_show_end", type, rewardEarned);
provider.Error += (type, message) => Log.Warn($"ad error: {message}");
```

## Platforms

| Platform | Provider | Status |
|---|---|---|
| Editor / desktop | `MockAdProvider` | v1 |
| HarmonyOS NEXT | Huawei Ads Kit (`@kit.AdsKit`) | v1 |
| HarmonyOS NEXT | Pangle (穿山甲 `openadsdk.har`) | v1 |
| Android / iOS | — | planned |

Documentation: `Documentation~/index.md`.
