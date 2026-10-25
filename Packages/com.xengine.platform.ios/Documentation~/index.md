# iOS Build Support (com.xengine.platform.ios)

**Downloads nothing.** Apple software licensing forbids redistributing Xcode, so this module is
a locator + preflight module:

- **Locator chain** (`toolchain.json` → `locators[0]`): `xcode-select -p` → `DEVELOPER_DIR` env →
  `xcodebuild` on PATH; validated with `xcodebuild -version` against `minVersion`. A
  Command-Line-Tools-only host (no full Xcode) is reported as degraded with guidance
  (`xcode-select --install` / App Store link).
- **On Windows**: full builds are Mac-gated by design (`IosBuildPipeline`); this module drives the
  preflight checklist — locator state, signing plan validity (`.mobileprovision` reading already
  works on Windows), size budgets — so everything except the final publish can be prepared.
- **On macOS**: `dotnet publish -r ios-arm64` + codesign via the located Xcode, device install via
  `xcrun devicectl` (existing bridge).

This mirrors the Unity model: the iOS "module" never ships Xcode; it finds the user's.

## Verification status (2026-10-04)

The .NET 10 / Xcode 27 Apple Silicon host has built, installed and launched the
`iossimulator-arm64` Zonezero Player and verified its rendered scene. See the
[Simulator evidence](../../../docs/superpowers/evidence/ios/2026-10-01-zonezero-ios-simulator.md).
This is a development smoke test; physical-device signing, installation, touch/audio/lifecycle
acceptance, cold-start measurements and an installable `.ipa` remain pending. The
[Apple delivery plan](../../../docs/superpowers/plans/2026-09-30-apple-platform-delivery-plan.md)
tracks these requirements before iOS is marked Supported.

Exported `verification/ios-startup.json` and `ios-device-matrix.json` record the actual
host OS and `status: "not-run"` when device measurements have not been collected. Timings
remain null, samples/cycles remain zero, and `supported` / `physicalDeviceVerified` remain
false. The package-size report measures the export tree before signed-app publication;
it does not establish the size of a signed app or IPA. See the
[report contract evidence](../../../docs/superpowers/evidence/ios/2026-10-04-a8-evidence-status/README.md).
