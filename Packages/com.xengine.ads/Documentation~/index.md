# Ads

Advertising monetization for XEngine: rewarded video and interstitial full-screen ads behind a
small provider abstraction. First target platform: **HarmonyOS NEXT** (Huawei Ads Kit, Pangle).
A mock provider runs the complete flow in the editor and in unit tests.

## Concepts

- `AdsService` — static facade (`Configure` / `Initialize` / `Load` / `IsLoaded` / `Show` / `Close`).
- `IAdProvider` — per-platform backend; implementations own the vendor SDK lifecycle and raise
  `Loaded` / `ShowStart` / `ShowEnd` / `Error` events. Ad completions resume XTask continuations
  on the ad-completion thread (XTask contract: continuations run on the completing thread);
  marshal before touching game state.
- `AdsSettings` — provider selection plus per-provider `AppId` / slot ids; `UseTestAds` keeps
  development on vendor test slots (no monetization agreement needed, never billed).
- `AdLoadResult` / `AdShowResult` — expected ad-flow conditions (no fill, nothing loaded)
  return result objects instead of throwing; cancellation surfaces as a canceled `XTask`.
- `AdsBootstrapper` — loads the build-written `Settings/Ads.json` payload and activates the
  configured provider (see "Build integration").

## Contract

1. `Configure(IAdProvider)` once at startup.
2. `Initialize(AdsSettings)`.
3. `Load(AdType)` — request and preload. Vendors invalidate cached ads after a window
   (Huawei Ads Kit: 1 hour between load and show); reload before every show you care about.
4. `Show(AdType)` — presents and consumes the cached ad. For `AdType.RewardVideo`,
   `AdShowResult.RewardEarned` reports whether the reward condition was met.

```csharp
using XEngine.Ads;

AdsService.Configure(new MockAdProvider());          // or the HarmonyOS providers
await AdsService.Initialize(settings);

await AdsService.Load(AdType.RewardVideo);
AdShowResult result = await AdsService.Show(AdType.RewardVideo);
if (result.Success && result.RewardEarned) GrantReward();
```

`async XTask`/`XTask<T>` (`XEngine.Async`) is the async surface throughout; synchronous
completion allocates nothing.

## Providers

| Provider | Id | Platform | SDK shape | Status |
|---|---|---|---|---|
| `MockAdProvider` | `mock` | Editor / desktop | none | v1 |
| Huawei Ads Kit (鲸鸿动能) | `huawei-adskit` | HarmonyOS NEXT | system kit `@kit.AdsKit` | v1 — glue targets reward + interstitial; validated on device with test slots |
| Pangle (穿山甲) | `pangle` | HarmonyOS NEXT | `openadsdk.har` (CSJ portal) | v1 — build integration complete; the vendor-call layer of the glue is wired from CSJ's `harmonyadsdk` demo (account-gated), see `Native/glue/XengineAdsPangle.ets` VENDOR-API |

Business prerequisites for **real** monetization (not development): Huawei requires an
enterprise developer account, AppGallery presence, and the 鲸鸿动能 media service agreement.
Games shipped through Huawei 联运 must not bundle third-party ad SDKs alongside Ads Kit — pick
one vendor per distribution channel. Pangle additionally requires media registration and
category approval on the CSJ console.

## Architecture (HarmonyOS)

```
game code (NativeAOT libxengine_player.so)
    │  AdsService / HarmonyOSAdProvider            C# — Runtime/HarmonyOS
    │  [DllImport("xengine_ads")] + [UnmanagedCallersOnly]      JSON protocol v1
    ▼
libxengine_ads.so  (Plugins/HarmonyOS/<abi>, dlopen path entry/libs/<abi>)
    │  NAPI threadsafe function                    Native/source/xengine_ads_napi.cpp
    ▼
ArkTS glue (entry/src/main/ets/xengineads/*.ets)   Native/glue — injected at build time
    │  @kit.AdsKit  /  openadsdk
    ▼
vendor ad SDK (full-screen reward/interstitial presentation)
```

The protocol is versioned flat JSON (`HarmonyOSAdsProtocol`); events complete the matching
`AutoResetXTaskCompletionSource` inline on the ArkTS thread.

## Build integration

- **Project Settings → Ads** (`AdsProjectSettings`): provider id, test-slot switch, per-vendor
  app/slot ids, pangle `openadsdk.har` path.
- On every HarmonyOS build the package's `IHarmonyOSStageProjectContributor` injects into the
  generated Stage project: the vendor ArkTS glue (`Native/glue/*.ets`), the EntryAbility wiring
  (side-effect import + `setAdsContext(this.context)`), `ohos.permission.INTERNET`, the pangle
  har into `entry/libs` + `oh-package.json5`, and the runtime payload `Settings/Ads.json`.
- `AdsBuildPostProcessor` fails the build when `Plugins/HarmonyOS/arm64/libxengine_ads.so` is
  missing or its sha256 drifts from `Native/native-manifest.json`.
- Rebuild the .so after changing `Native/source/xengine_ads_napi.cpp`:

  ```powershell
  Packages/com.xengine.ads/Native/build-harmonyos.ps1 -NdkRoot <ohos-sdk>\native
  ```

  (local toolchain root: `%LOCALAPPDATA%\XEngine\PlatformSDKs\harmonyos\ohos-sdk\native`.)

## Samples

`Samples~/AdsDemo` — `AdsDemoComponent` wires bootstrap → preload → show → reward. No scene
ships; attach the component in your bootstrap scene and hook `OnRewardGranted`.

## Tests

`Tests~/XEngine.Ads.Tests.csproj` (hidden `Tests~`, spine38-style; compiles the Runtime, the
standalone Stage patcher, and the sample component):

```
dotnet test Packages/com.xengine.ads/Tests~/XEngine.Ads.Tests.csproj
```

Coverage: mock flow and failure injection, provider state machines, JSON protocol round-trips,
Stage patcher idempotency (glue / EntryAbility / permissions / har / payload), bootstrapper
parsing.

## Acceptance status (0.1.0)

- Unit: 51/51 passing (`dotnet test` above).
- Stage contribution against the real SDK Bundle templates: glue + EntryAbility wiring +
  `ohos.permission.INTERNET` + `Settings/Ads.json` verified — regenerate with
  `Build~/StageContributionEvidence` and see
  [`evidence/2026-09-17-stage-contribution.md`](evidence/2026-09-17-stage-contribution.md).
- Device run with Huawei test slots / Pangle test mode: pending a provisioned device + media
  account; procedure: build HAP → `hdc install` → run the AdsDemo scene → observe
  load/show/reward logs and the vendor test creative.
