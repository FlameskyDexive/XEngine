# HarmonyOS Build Support (com.xengine.platform.harmonyos)

Metadata-only platform module. `toolchain.json` pins the two artifacts the HarmonyOS build chain
needs and where to get them:

| Component | Source | Purpose |
| --- | --- | --- |
| OpenHarmony SDK (`ohos-sdk`) | Huawei Cloud open mirror (`repo.huaweicloud.com/openharmony/os/…`, no login) | native toolchain (clang/sysroot per `oh-uni-package.json`) + `toolchains/hdc` |
| XEngine OpenHarmony SDK Bundle | `FlameskyDexive/XEngine-Toolchains` GitHub Releases (self-built) | local NuGet feed (`OpenHarmony.NET.*`), stage templates, SHA256SUMS |

**Download & Install Toolchain** (Build window, or `XEngine.Editor --install-platform-tools
harmonyos`) fetches both into `%LOCALAPPDATA%\XEngine\PlatformSDKs\harmonyos\`, then feeds them
through the **existing** import/resolve chain: the bundle via the same path as
`--import-harmonyos-bundle`, the SDK root via `HarmonyOSOfficialSdkRoot` / `OHOS_SDK_HOME`
semantics (validated by `<sdkRoot>/<folder>/native/oh-uni-package.json`). Nothing in
`HarmonyOSToolchainResolver` changes.

Debug HAP signing keeps its existing environment-variable contract
(`XENGINE_HARMONYOS_SIGNING_DIR` / `_STORE_PASSWORD` / `_KEY_PASSWORD`, DevEco-generated
material) — this module never touches signing.

Huawei/OpenHarmony SDK bits are linked from the official open mirror and never re-hosted as
primary distribution; pinned mirrors, when present, are fallback-only.
