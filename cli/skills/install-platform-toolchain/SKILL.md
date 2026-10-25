# install-platform-toolchain

Install a platform's build toolchain from the platform module's pinned manifest — the Unity
"install the platform module and everything is ready" experience. Android and HarmonyOS download
real toolchains from vendor sources into a managed SDK root; iOS locates the user's Xcode and
downloads nothing (Apple licensing).

## When to use

- "装一下 Android 打包环境" / "install the HarmonyOS toolchain"
- "打包报找不到 SDK/JDK/NDK/hdc" / "Xcode 在哪 / 版本够不够"
- CI preparing a build host.

## How it works

1. The platform module (`com.xengine.platform.android|harmonyos|ios`) resolves **editor-level
   first**: the per-user editor module store
   (`%LOCALAPPDATA%\XEngine\EditorModules\<engine-version>\`, npm `@inxep/platform.*` — installed
   from the launcher Settings page or automatically by the CLI), then the Editor bundle (modules
   ship with every install). A project that pins the module in `Packages/manifest.json` keeps
   precedence for its own copy. **No project is required.**
2. The module's `toolchain.json` pins every component: vendor source URLs (sha256-pinned where
   calibrated), target layout, verify sentinel, and env exports.
3. The channel downloads (resumable, multi-source), verifies, extracts into
   `%LOCALAPPDATA%\XEngine\PlatformSDKs\<platform>\`, records a spec fingerprint + sentinel, and
   writes the resolved roots into the existing EditorSettings slots (**empty or already-managed
   slots only — user-configured values are never overwritten**). Reruns with a matching
   fingerprint are fully offline (fingerprint + sentinel ⇒ `Cached`).

## Commands

Headless (the editor binary is the CLI):

```
XEngine.Editor --install-platform-tools android --accept-sdk-licenses      # module auto-installs if missing
XEngine.Editor --install-platform-tools harmonyos [--harmonyos-bundle <local.zip>]
XEngine.Editor --install-platform-tools ios        # locator only, downloads nothing
```

- `--project <path>` is now **optional** — pass it only when the module must resolve from a
  project's manifest pin, or to let a local HarmonyOS bundle import into that project. Without a
  project the CLI first installs the module from npm into the editor module store, then runs the
  channel (a no-project HarmonyOS install skips the bundle import and reports Partial guidance).
- `--accept-sdk-licenses` is **explicit consent** to the Google Android SDK licenses
  (developer.android.com/studio/terms); without it the channel stops before sdkmanager (exit 2).
  The editor Build window exposes the same flow as a checkbox + button.
- `--harmonyos-bundle` routes a local OpenHarmony SDK Bundle archive through the existing verified
  importer (used while the XEngine-Toolchains GitHub feed is not hosted).
- `--platform-toolchain-cache <dir>` redirects the managed SDK root (tests/CI).

Launcher UI: **project list window → Settings (gear, top right) → Platforms** — one-click
"Install module + SDK" per platform with progress and cancel; Android shows the license checkbox;
iOS runs the Xcode locator (Partial guidance on non-mac hosts); module updates (npm) and uninstall
live on the same cards. Deep links for automation/screenshots:
`XEngine.Editor --launcher-settings [--install-module com.xengine.platform.<platform>]`.

In-editor: Build window → platform profile → "Managed Toolchain (module)" → Download & Install
(license checkbox on Android; progress + cancel through the editor progress UI).

## After install

- **Android**: run the existing toolchain session ("Resolve, Lock & Dry Run" in the profile, or
  `--resolve-android-toolchain <lock-path>` with `--android-jdk/--android-sdk/--android-ndk/
  --android-bundletool` overrides pointing into the managed root). The resolver's probe + minimal
  APK dry-run is the acceptance gate.
- **HarmonyOS**: the channel fills `Official SDK`/`OHOS_SDK_HOME` semantics
  (`<root>/<folder>/native/oh-uni-package.json`) and locates `hdc`; the Bundle side uses the
  existing "Import Bundle" / resolve chain.
- **iOS**: "Check Xcode" validates DEVELOPER_DIR/xcode-select/PATH (min 16.0) and the Signing
  Preflight row shows what this host can prepare. Signing/publish/devicectl run on macOS — that
  end-to-end pass is a deliberate follow-up.

## Troubleshooting

- **403 / exit 2 on Android**: licenses not consented — read the terms, pass
  `--accept-sdk-licenses` or tick the checkbox.
- **Integrity mismatch**: the mirror/vendor artifact changed — do NOT weaken the pin; update the
  manifest hash from the vendor sidecar and bump the module package version.
- **"already published"/npm 409-style**: see [publish-npm-package](../publish-npm-package/SKILL.md).
- **Module not found at editor level (CLI)**: the npm install failed — check network and
  `XENGINE_NPM_REGISTRY`; npm auth for private registries comes from `XENGINE_NPM_TOKEN` or
  `.xengine/secrets.json` (project or `%USERPROFILE%\.xengine\secrets.json`).
- **Stale module content after a repo update**: the editor bundles the module; a project
  `Library/PackageCache/<module>@<ver>` copy may be outdated — bump the package version or clear
  that cache folder. Editor-store modules update via the launcher Settings card (Update button)
  or reinstall; the store is per engine version (`EditorModules\<version>\`).
- **Concurrent installs**: one installer at a time per machine (downloads share
  `.downloads/.part` files).
