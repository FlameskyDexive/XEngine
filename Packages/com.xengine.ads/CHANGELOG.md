# Changelog

## [0.1.0] - 2026-09-16

- Official optional package skeleton: listed in Package Manager, not mounted until installed.
- Runtime API: `AdsService` facade, `IAdProvider`, `AdType` (RewardVideo / Interstitial),
  `AdLoadResult` / `AdShowResult`, `AdsSettings`, provider id constants. Async surface is
  `XTask`-based (`XEngine.Async`); synchronous completion allocates nothing.
- `MockAdProvider`: full load → show → reward flow with configurable delays, success rates,
  and reward behavior; events (`Loaded` / `ShowStart` / `ShowEnd` / `Error`) raised on the
  calling thread. Lets games develop and unit-test ad flows without a device or ad account.
- `Tests~`: dotnet xunit suite (spine38-style hidden test project) covering the mock flow,
  state transitions, failure injection, and the `AdsService` facade.
- HarmonyOS native bridge (`libxengine_ads.so` NAPI module), ArkTS glue, Huawei Ads Kit /
  Pangle providers, and Stage-project build integration land in subsequent 0.1.x releases.
