# Ads Demo

Import this sample from Package Manager after installing `com.xengine.ads`.

`AdsDemoComponent` is the complete rewarded-video flow a game needs:

1. **Bootstrap** — packaged players read `Settings/Ads.json` from the content root
   (written at build time from Project Settings → Ads) and activate the configured
   HarmonyOS provider; editor/desktop falls back to `MockAdProvider`, so the whole
   flow is testable without a device or ad account.
2. **Preload** — a reward video (and optionally an interstitial) is requested shortly
   after start.
3. **Show** — gameplay calls `ShowRewardVideo()` (e.g. from a "revive" button);
   the component reloads on demand when nothing is cached.
4. **Reward** — when `AdShowResult.RewardEarned` arrives, `OnRewardGranted` fires.
   Wire it to your gameplay and marshal to the game thread before mutating scene state
   (ad completions resume on the ad thread by design).

## Usage

```csharp
var ads = gameObject.AddComponent<AdsDemoComponent>();
ads.OnRewardGranted += () => player.GrantRevive();
// later, from a button handler:
ads.ShowRewardVideo().Forget();
```

No scene ships with this sample on purpose: attach the component in your own
bootstrap scene. Vendor slot ids come from Project Settings → Ads
(`mock | huawei-adskit | pangle`, test-slot switch, per-vendor app/slot ids,
pangle `openadsdk.har` path).
