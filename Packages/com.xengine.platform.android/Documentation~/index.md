# Android Build Support (com.xengine.platform.android)

Metadata-only platform module: its `toolchain.json` pins every toolchain component the Android
build needs and where to get it. Installing this module plus clicking **Download & Install
Toolchain** in the Build window (or `XEngine.Editor --install-platform-tools android`) fetches:

| Component | Source | Size (approx) |
| --- | --- | --- |
| JDK 17 (Temurin) | Adoptium releases (GPL+CE, redistributable) | ~190 MB |
| cmdline-tools | dl.google.com (linked, not mirrored) | ~130 MB |
| platform-tools / build-tools;35.0.0 / platforms;android-36 / NDK r28 | via `sdkmanager` from dl.google.com | ~150 MB + ~600 MB (NDK) |
| bundletool | google/bundletool releases | ~13 MB |

Everything lands under `%LOCALAPPDATA%\XEngine\PlatformSDKs\android\`, licenses are accepted in an
editor dialog (never non-interactively), and the resolved roots are written into the existing
Android toolchain settings (`AndroidSdkRoot` / `AndroidJavaSdkRoot` / `AndroidNdkRoot` /
`AndroidBundletoolPath`) before the existing **Resolve, Lock & Dry Run** session runs. The Android
resolver's semantics are untouched — this module is a new acquisition producer, not a replacement.

Android builds support Windows x64 build hosts only (existing gate). Google components are linked
from official sources and never re-hosted as primary distribution; pinned mirrors, when present,
are fallback-only.

See the repo plan `docs/superpowers/plans/2026-09-09-platform-toolchain-modules.md` for the
overall design.
